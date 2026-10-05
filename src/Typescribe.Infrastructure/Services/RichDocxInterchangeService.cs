using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Typescribe.Application.Abstractions;
using Typescribe.Application.Services;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

/// <summary>
/// Rich formatting layer over the existing semantic DOCX interchange service. The base service
/// remains responsible for package structure, images, numbering, footnotes and tables; this layer
/// maps TypeScribe's Markdown-first rich AST to/from WordprocessingML run and paragraph properties.
/// </summary>
public sealed class RichDocxInterchangeService
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private readonly IDocumentParser _parser;
    private readonly BibTeXDatabase _bibliography;
    private readonly DocxInterchangeService _inner;
    private readonly List<string> _warnings = [];

    public RichDocxInterchangeService(IDocumentParser parser, BibTeXDatabase bibliography)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _bibliography = bibliography ?? throw new ArgumentNullException(nameof(bibliography));
        _inner = new DocxInterchangeService(parser, bibliography);
    }

    public IReadOnlyList<string> LastWarnings => _warnings;

    public async Task ExportAsync(
        BookProject project,
        IReadOnlyList<(ProjectNode Node, string Content)> documents,
        string destination,
        CancellationToken cancellationToken = default)
    {
        _warnings.Clear();
        await _inner.ExportAsync(project, documents, destination, cancellationToken);
        var entries = (await _bibliography.LoadAsync(project.RootPath, cancellationToken))
            .ToDictionary(static entry => entry.CitationKey, StringComparer.OrdinalIgnoreCase);
        await ApplyExportFormattingAsync(documents, destination, entries, cancellationToken);
    }

    public async Task<string> ImportAsync(
        BookProject project,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        _warnings.Clear();
        var baseMarkdown = await _inner.ImportAsync(project, sourcePath, cancellationToken);
        return await ImportRichMarkdownAsync(sourcePath, baseMarkdown, cancellationToken);
    }

    private async Task ApplyExportFormattingAsync(
        IReadOnlyList<(ProjectNode Node, string Content)> documents,
        string path,
        IReadOnlyDictionary<string, BibliographyEntry> bibliography,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true);
        var document = LoadXml(archive, "word/document.xml")
            ?? throw new InvalidOperationException("Generated DOCX does not contain word/document.xml.");
        var body = document.Root?.Element(W + "body");
        if (body is null) return;

        var elements = body.Elements().Where(static element => element.Name.LocalName != "sectPr").ToList();
        var cursor = 0;
        for (var documentIndex = 0; documentIndex < documents.Count; documentIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ast = _parser.Parse(documents[documentIndex].Content);
            var definitions = ast.Blocks.OfType<FootnoteDefinitionBlock>()
                .GroupBy(static note => note.Identifier, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.Last().Inlines, StringComparer.OrdinalIgnoreCase);

            foreach (var block in ast.Blocks)
            {
                switch (block)
                {
                    case HeadingBlock heading:
                        ApplyNextParagraph(elements, ref cursor, block, heading.Inlines, bibliography, definitions);
                        break;
                    case ParagraphBlock paragraph:
                        ApplyNextParagraph(elements, ref cursor, block, paragraph.Inlines, bibliography, definitions);
                        break;
                    case QuoteBlock quote:
                        ApplyNextParagraph(elements, ref cursor, block, quote.Inlines, bibliography, definitions);
                        break;
                    case ListItemBlock item:
                        ApplyNextParagraph(elements, ref cursor, block, item.Inlines, bibliography, definitions);
                        break;
                    case CodeBlock:
                    case DisplayMathBlock:
                    case ThematicBreakBlock:
                        ApplyNextParagraphFormattingOnly(elements, ref cursor, block.Formatting);
                        break;
                    case TableBlock table:
                        if (cursor < elements.Count && elements[cursor].Name == W + "tbl")
                        {
                            ApplyTableFormatting(elements[cursor], table, bibliography, definitions);
                            cursor++;
                        }
                        if (!string.IsNullOrWhiteSpace(table.Caption) && cursor < elements.Count && elements[cursor].Name == W + "p")
                            cursor++;
                        break;
                    case FigureBlock figure:
                        if (cursor < elements.Count && elements[cursor].Name == W + "p") cursor++;
                        if (!string.IsNullOrWhiteSpace(figure.Caption) && cursor < elements.Count && elements[cursor].Name == W + "p") cursor++;
                        break;
                    case FootnoteDefinitionBlock:
                    case BibliographyEntryBlock:
                        break;
                }
            }

            if (documentIndex < documents.Count - 1 && cursor < elements.Count && elements[cursor].Name == W + "p")
                cursor++;
        }

        ReplaceXml(archive, "word/document.xml", document);
    }

    private void ApplyNextParagraph(
        IReadOnlyList<XElement> elements,
        ref int cursor,
        AstBlock block,
        IReadOnlyList<AstInline> inlines,
        IReadOnlyDictionary<string, BibliographyEntry> bibliography,
        IReadOnlyDictionary<string, IReadOnlyList<AstInline>> definitions)
    {
        if (cursor >= elements.Count || elements[cursor].Name != W + "p") return;
        var paragraph = elements[cursor++];
        ApplyParagraphFormatting(paragraph, block.Formatting?.Paragraph);
        ApplyRunFormatting(paragraph, inlines, bibliography, definitions);
    }

    private void ApplyNextParagraphFormattingOnly(
        IReadOnlyList<XElement> elements,
        ref int cursor,
        RichBlockFormatting? formatting)
    {
        if (cursor >= elements.Count || elements[cursor].Name != W + "p") return;
        ApplyParagraphFormatting(elements[cursor], formatting?.Paragraph);
        cursor++;
    }

    private void ApplyTableFormatting(
        XElement wordTable,
        TableBlock source,
        IReadOnlyDictionary<string, BibliographyEntry> bibliography,
        IReadOnlyDictionary<string, IReadOnlyList<AstInline>> definitions)
    {
        var rows = wordTable.Elements(W + "tr").ToArray();
        var sourceRows = new[] { source.Header }.Concat(source.Rows).ToArray();
        for (var row = 0; row < Math.Min(rows.Length, sourceRows.Length); row++)
        {
            var cells = rows[row].Elements(W + "tc").ToArray();
            for (var column = 0; column < Math.Min(cells.Length, sourceRows[row].Count); column++)
            {
                var paragraph = cells[column].Elements(W + "p").FirstOrDefault();
                if (paragraph is null) continue;
                ApplyRunFormatting(paragraph, sourceRows[row][column].Inlines, bibliography, definitions);
            }
        }
    }

    private void ApplyParagraphFormatting(XElement paragraph, ParagraphFormatting? formatting)
    {
        if (formatting is null) return;
        var pPr = paragraph.Element(W + "pPr");
        if (pPr is null)
        {
            pPr = new XElement(W + "pPr");
            paragraph.AddFirst(pPr);
        }

        if (!string.IsNullOrWhiteSpace(formatting.StyleId))
            SetElement(pPr, "pStyle", formatting.StyleId!);

        if (formatting.Direction == TextDirectionMode.RightToLeft) SetOnOff(pPr, "bidi", true);
        else if (formatting.Direction == TextDirectionMode.LeftToRight) SetOnOff(pPr, "bidi", false);

        if (formatting.Alignment is { } alignment)
        {
            var value = alignment switch
            {
                TextAlignmentMode.Center => "center",
                TextAlignmentMode.Right => "right",
                TextAlignmentMode.Justified => "both",
                _ => "left"
            };
            SetElement(pPr, "jc", value);
        }

        if (formatting.FirstLineIndentPoints is not null || formatting.LeftIndentPoints is not null || formatting.RightIndentPoints is not null)
        {
            var ind = EnsureChild(pPr, "ind");
            SetTwipsAttribute(ind, "firstLine", formatting.FirstLineIndentPoints);
            SetTwipsAttribute(ind, "left", formatting.LeftIndentPoints);
            SetTwipsAttribute(ind, "right", formatting.RightIndentPoints);
        }

        if (formatting.SpaceBeforePoints is not null || formatting.SpaceAfterPoints is not null || formatting.LineSpacing is not null)
        {
            var spacing = EnsureChild(pPr, "spacing");
            SetTwipsAttribute(spacing, "before", formatting.SpaceBeforePoints);
            SetTwipsAttribute(spacing, "after", formatting.SpaceAfterPoints);
            if (formatting.LineSpacing is { } line)
            {
                spacing.SetAttributeValue(W + "line", Math.Max(1, (int)Math.Round(line * 240)).ToString(CultureInfo.InvariantCulture));
                spacing.SetAttributeValue(W + "lineRule", "auto");
            }
        }

        if (formatting.KeepWithNext is { } keepNext) SetOnOff(pPr, "keepNext", keepNext);
        if (formatting.KeepLinesTogether is { } keepLines) SetOnOff(pPr, "keepLines", keepLines);
        if (formatting.KeepFirstLines is not null || formatting.KeepLastLines is not null)
            Warn("WordprocessingML has no exact equivalent for TypeScribe's keep-first/keep-last line counts; paragraph keep settings were preserved where possible.");
        if (formatting.OpticalMarginAlignment == true)
            Warn("DOCX does not have a portable equivalent for optical margin alignment; the setting remains in canonical Markdown metadata.");

        if (formatting.Tabs is { Count: > 0 } tabs)
        {
            var wordTabs = new XElement(W + "tabs");
            foreach (var tab in tabs)
            {
                var tabAlignment = tab.Alignment switch
                {
                    TabStopAlignment.Center => "center",
                    TabStopAlignment.Right => "right",
                    TabStopAlignment.Decimal => "decimal",
                    _ => "left"
                };
                var item = new XElement(W + "tab",
                    new XAttribute(W + "val", tabAlignment),
                    new XAttribute(W + "pos", ToTwips(tab.PositionPoints).ToString(CultureInfo.InvariantCulture)));
                if (tab.Leader is not null)
                    item.SetAttributeValue(W + "leader", tab.Leader == '.' ? "dot" : tab.Leader == '-' ? "hyphen" : "middleDot");
                wordTabs.Add(item);
            }
            pPr.Element(W + "tabs")?.Remove();
            pPr.Add(wordTabs);
        }

        if (!string.IsNullOrWhiteSpace(formatting.Language) || formatting.CharacterDefaults is not null)
        {
            var defaults = formatting.CharacterDefaults ?? new CharacterFormatting();
            if (!string.IsNullOrWhiteSpace(formatting.Language) && string.IsNullOrWhiteSpace(defaults.Language))
                defaults = defaults with { Language = formatting.Language };
            var rPr = EnsureChild(pPr, "rPr");
            ApplyCharacterProperties(rPr, defaults, bold: false, italic: false, preserveExisting: true);
        }
    }

    private void ApplyRunFormatting(
        XElement paragraph,
        IReadOnlyList<AstInline> inlines,
        IReadOnlyDictionary<string, BibliographyEntry> bibliography,
        IReadOnlyDictionary<string, IReadOnlyList<AstInline>> definitions)
    {
        var expected = BuildExpectedRuns(inlines, bibliography, definitions).ToList();
        var runs = paragraph.Elements(W + "r").ToList();
        var runIndex = 0;

        foreach (var item in expected)
        {
            if (item.FootnoteReference)
            {
                while (runIndex < runs.Count)
                {
                    var candidate = runs[runIndex++];
                    if (candidate.Element(W + "footnoteReference") is not null) break;
                }
                continue;
            }

            while (runIndex < runs.Count)
            {
                var run = runs[runIndex++];
                var text = string.Concat(run.Elements(W + "t").Select(static value => value.Value));
                if (!string.Equals(text, item.BaseText, StringComparison.Ordinal))
                    continue;

                if (item.Replacements is { Count: > 0 } replacements)
                {
                    var generated = replacements.Select(CreateRun).ToArray();
                    run.ReplaceWith(generated);
                }
                else
                {
                    var rPr = EnsureRunProperties(run);
                    ApplyCharacterProperties(rPr, item.Formatting, item.Bold, item.Italic, preserveExisting: true, item.Style);
                }
                break;
            }
        }
    }

    private IEnumerable<ExpectedRun> BuildExpectedRuns(
        IEnumerable<AstInline> inlines,
        IReadOnlyDictionary<string, BibliographyEntry> bibliography,
        IReadOnlyDictionary<string, IReadOnlyList<AstInline>> definitions,
        bool bold = false,
        bool italic = false)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case RichSpanInline rich:
                {
                    var replacements = BuildReplacementRuns(rich.Children, rich.Formatting, bibliography, definitions, bold, italic).ToArray();
                    yield return new ExpectedRun(rich.Text, rich.Formatting, bold, italic, null, false, replacements);
                    break;
                }
                case TextInline text:
                    yield return new ExpectedRun(text.Text, null, bold, italic, null, false, null);
                    break;
                case StrongInline strong:
                    foreach (var child in BuildExpectedRuns(strong.Children, bibliography, definitions, true, italic)) yield return child;
                    break;
                case EmphasisInline emphasis:
                    foreach (var child in BuildExpectedRuns(emphasis.Children, bibliography, definitions, bold, true)) yield return child;
                    break;
                case CodeInline code:
                    yield return new ExpectedRun(code.Text, null, bold, italic, "CodeChar", false, null);
                    break;
                case MathInline math:
                    yield return new ExpectedRun("$" + math.Text + "$", null, bold, italic, null, false, null);
                    break;
                case LinkInline link:
                    foreach (var child in BuildExpectedRuns(link.Label, bibliography, definitions, bold, italic)) yield return child;
                    yield return new ExpectedRun(" (" + link.Url + ")", null, bold, italic, null, false, null);
                    break;
                case CitationInline citation:
                    yield return new ExpectedRun(FormatCitation(citation, bibliography), null, bold, italic, null, false, null);
                    break;
                case CrossReferenceInline reference:
                    yield return new ExpectedRun("[@ref:" + reference.Identifier + "]", null, bold, italic, null, false, null);
                    break;
                case FootnoteReferenceInline footnote when definitions.ContainsKey(footnote.Identifier):
                    yield return new ExpectedRun(string.Empty, null, bold, italic, null, true, null);
                    break;
                case FootnoteReferenceInline footnote:
                    yield return new ExpectedRun("[^" + footnote.Identifier + "]", null, bold, italic, null, false, null);
                    break;
            }
        }
    }

    private IEnumerable<RunReplacement> BuildReplacementRuns(
        IEnumerable<AstInline> inlines,
        CharacterFormatting formatting,
        IReadOnlyDictionary<string, BibliographyEntry> bibliography,
        IReadOnlyDictionary<string, IReadOnlyList<AstInline>> definitions,
        bool bold,
        bool italic)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case RichSpanInline nested:
                    foreach (var child in BuildReplacementRuns(nested.Children, Merge(formatting, nested.Formatting), bibliography, definitions, bold, italic)) yield return child;
                    break;
                case TextInline text:
                    yield return new RunReplacement(text.Text, formatting, bold, italic, null);
                    break;
                case StrongInline strong:
                    foreach (var child in BuildReplacementRuns(strong.Children, formatting, bibliography, definitions, true, italic)) yield return child;
                    break;
                case EmphasisInline emphasis:
                    foreach (var child in BuildReplacementRuns(emphasis.Children, formatting, bibliography, definitions, bold, true)) yield return child;
                    break;
                case CodeInline code:
                    yield return new RunReplacement(code.Text, formatting, bold, italic, "CodeChar");
                    break;
                case MathInline math:
                    yield return new RunReplacement(math.Text, formatting, bold, italic, null);
                    break;
                case LinkInline link:
                    foreach (var child in BuildReplacementRuns(link.Label, formatting, bibliography, definitions, bold, italic)) yield return child;
                    break;
                case CitationInline citation:
                    yield return new RunReplacement("[@" + citation.Key + (string.IsNullOrWhiteSpace(citation.Locator) ? string.Empty : ", " + citation.Locator) + "]", formatting, bold, italic, null);
                    break;
                case CrossReferenceInline reference:
                    yield return new RunReplacement("[@ref:" + reference.Identifier + "]", formatting, bold, italic, null);
                    break;
                case FootnoteReferenceInline footnote:
                    yield return new RunReplacement("[^" + footnote.Identifier + "]", formatting, bold, italic, null);
                    break;
            }
        }
    }

    private XElement CreateRun(RunReplacement replacement)
    {
        var run = new XElement(W + "r");
        var rPr = new XElement(W + "rPr");
        ApplyCharacterProperties(rPr, replacement.Formatting, replacement.Bold, replacement.Italic, preserveExisting: false, replacement.Style);
        if (rPr.HasElements) run.Add(rPr);
        run.Add(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), replacement.Text));
        return run;
    }

    private void ApplyCharacterProperties(
        XElement rPr,
        CharacterFormatting? formatting,
        bool bold,
        bool italic,
        bool preserveExisting,
        string? style = null)
    {
        formatting ??= new CharacterFormatting();
        if (!string.IsNullOrWhiteSpace(style)) SetElement(rPr, "rStyle", style!);

        var effectiveBold = formatting.Bold ?? bold;
        var effectiveItalic = formatting.Italic ?? italic;
        SetOnOff(rPr, "b", effectiveBold);
        SetOnOff(rPr, "i", effectiveItalic);
        if (formatting.Underline is { } underline)
            SetElement(rPr, "u", underline ? "single" : "none");
        if (formatting.SmallCaps is { } smallCaps) SetOnOff(rPr, "smallCaps", smallCaps);

        if (formatting.Font is { Family.Length: > 0 } font)
        {
            var fonts = EnsureChild(rPr, "rFonts");
            foreach (var name in new[] { "ascii", "hAnsi", "eastAsia", "cs" })
                fonts.SetAttributeValue(W + name, font.Family);
        }

        if (formatting.FontSizePoints is { } size && size > 0)
        {
            var halfPoints = Math.Max(2, (int)Math.Round(size * 2)).ToString(CultureInfo.InvariantCulture);
            SetElement(rPr, "sz", halfPoints);
            SetElement(rPr, "szCs", halfPoints);
        }

        if (!string.IsNullOrWhiteSpace(formatting.Language))
        {
            var lang = EnsureChild(rPr, "lang");
            lang.SetAttributeValue(W + "val", formatting.Language);
            if (formatting.Direction == TextDirectionMode.RightToLeft || UnicodeScriptClassifier.IsRtlLanguage(formatting.Language))
                lang.SetAttributeValue(W + "bidi", formatting.Language);
        }
        if (formatting.Direction == TextDirectionMode.RightToLeft) SetOnOff(rPr, "rtl", true);
        else if (formatting.Direction == TextDirectionMode.LeftToRight) SetOnOff(rPr, "rtl", false);

        if (formatting.TrackingEm is { } tracking)
        {
            var pointSize = formatting.FontSizePoints ?? 12;
            SetElement(rPr, "spacing", ((int)Math.Round(tracking * pointSize * 20)).ToString(CultureInfo.InvariantCulture));
        }
        if (formatting.Kerning is { } kerning)
        {
            SetElement(rPr, "kern", kerning ? "2" : "0");
            Warn("DOCX kerning stores a minimum-size threshold rather than TypeScribe's boolean kerning intent; the closest Word setting was written.");
        }
        if (formatting.BaselineShiftPoints is { } baseline)
            SetElement(rPr, "position", ((int)Math.Round(baseline * 2)).ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(formatting.ColorHex))
            SetElement(rPr, "color", formatting.ColorHex!.TrimStart('#'));

        if (formatting.Ligatures is not null || formatting.OpenTypeFeatures is { Count: > 0 })
            Warn("Arbitrary OpenType feature settings are not portable in baseline DOCX; Unicode text and the supported Word run properties were preserved.");
        if (formatting.VariableAxes is { Count: > 0 })
            Warn("DOCX does not portably preserve variable-font axis values; Unicode text and font family were preserved.");
    }

    private async Task<string> ImportRichMarkdownAsync(string sourcePath, string baseMarkdown, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var document = LoadXml(archive, "word/document.xml");
        var body = document?.Root?.Element(W + "body");
        if (body is null) return baseMarkdown;

        var numbering = LoadNumbering(archive);
        var baseLines = baseMarkdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var images = new Queue<string>(baseLines.Where(static line => line.StartsWith("![Image](", StringComparison.Ordinal)));
        var footnotes = baseLines.Where(static line => line.StartsWith("[^fn", StringComparison.Ordinal)).ToArray();
        var output = new StringBuilder();

        foreach (var element in body.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element.Name == W + "p")
            {
                var imageCount = element.Descendants(A + "blip").Count();
                for (var image = 0; image < imageCount && images.Count > 0; image++)
                    output.AppendLine(images.Dequeue());

                var text = ReadRichParagraphMarkup(element);
                var pPr = element.Element(W + "pPr");
                var style = (string?)pPr?.Element(W + "pStyle")?.Attribute(W + "val");
                var numIdText = (string?)pPr?.Element(W + "numPr")?.Element(W + "numId")?.Attribute(W + "val");
                var prefix = string.Empty;
                if (style?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true &&
                    int.TryParse(style[7..], NumberStyles.None, CultureInfo.InvariantCulture, out var level))
                    prefix = new string('#', Math.Clamp(level, 1, 6)) + " ";
                else if (int.TryParse(numIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var numId))
                    prefix = numbering.TryGetValue(numId, out var format) && string.Equals(format, "bullet", StringComparison.OrdinalIgnoreCase) ? "- " : "1. ";

                var paragraphFormatting = ReadParagraphFormatting(pPr);
                if (paragraphFormatting is not null && (text.Length > 0 || imageCount == 0))
                    output.AppendLine(RichMarkdownFormattingCodec.CreateBlockMetadata(new RichBlockFormatting(Paragraph: paragraphFormatting)));
                if (text.Length > 0 || imageCount == 0)
                    output.Append(prefix).AppendLine(text);
                if (element.Descendants(W + "br").Any(br => string.Equals((string?)br.Attribute(W + "type"), "page", StringComparison.OrdinalIgnoreCase)))
                    output.AppendLine("<!-- pagebreak -->");
            }
            else if (element.Name == W + "tbl")
            {
                AppendRichTable(output, element);
            }
        }

        foreach (var footnote in footnotes) output.AppendLine(footnote);
        return output.ToString().TrimEnd() + Environment.NewLine;
    }

    private string ReadRichParagraphMarkup(XElement paragraph)
    {
        var result = new StringBuilder();
        foreach (var run in paragraph.Elements(W + "r"))
        {
            var reference = run.Element(W + "footnoteReference");
            if (reference is not null && int.TryParse((string?)reference.Attribute(W + "id"), out var id))
            {
                result.Append("[^fn").Append(id).Append(']');
                continue;
            }

            var text = string.Concat(run.Elements(W + "t").Select(static item => item.Value));
            if (text.Length == 0) continue;
            var properties = run.Element(W + "rPr");
            var bold = IsOn(properties?.Element(W + "b"));
            var italic = IsOn(properties?.Element(W + "i"));
            var formatting = ReadCharacterFormatting(properties);

            var markup = EscapeMarkdownText(text);
            if (bold && italic) markup = "***" + markup + "***";
            else if (bold) markup = "**" + markup + "**";
            else if (italic) markup = "*" + markup + "*";

            if (formatting is not null)
                markup = RichMarkdownFormattingCodec.WrapInline(markup, formatting);
            result.Append(markup);
        }
        return result.ToString().TrimEnd();
    }

    private CharacterFormatting? ReadCharacterFormatting(XElement? rPr)
    {
        if (rPr is null) return null;
        var family = (string?)rPr.Element(W + "rFonts")?.Attribute(W + "cs")
            ?? (string?)rPr.Element(W + "rFonts")?.Attribute(W + "ascii")
            ?? (string?)rPr.Element(W + "rFonts")?.Attribute(W + "hAnsi");
        var sizeText = (string?)rPr.Element(W + "sz")?.Attribute(W + "val");
        var language = (string?)rPr.Element(W + "lang")?.Attribute(W + "bidi")
            ?? (string?)rPr.Element(W + "lang")?.Attribute(W + "val");
        var spacingText = (string?)rPr.Element(W + "spacing")?.Attribute(W + "val");
        var positionText = (string?)rPr.Element(W + "position")?.Attribute(W + "val");
        var color = (string?)rPr.Element(W + "color")?.Attribute(W + "val");
        var underline = rPr.Element(W + "u") is { } u && !string.Equals((string?)u.Attribute(W + "val"), "none", StringComparison.OrdinalIgnoreCase);
        var smallCaps = IsOn(rPr.Element(W + "smallCaps"));
        var rtl = IsOn(rPr.Element(W + "rtl"));

        double? size = double.TryParse(sizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var halfPoints) ? halfPoints / 2d : null;
        double? tracking = null;
        if (int.TryParse(spacingText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var spacing) && size is > 0)
            tracking = spacing / (size.Value * 20d);
        double? baseline = int.TryParse(positionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var position) ? position / 2d : null;

        var formatting = new CharacterFormatting(
            Font: string.IsNullOrWhiteSpace(family) ? null : new FontReference(family),
            FontSizePoints: size,
            Underline: underline ? true : null,
            SmallCaps: smallCaps ? true : null,
            TrackingEm: tracking,
            BaselineShiftPoints: baseline,
            ColorHex: string.IsNullOrWhiteSpace(color) || string.Equals(color, "auto", StringComparison.OrdinalIgnoreCase) ? null : "#" + color,
            Language: string.IsNullOrWhiteSpace(language) ? null : language,
            Direction: rtl ? TextDirectionMode.RightToLeft : null);

        return IsEmpty(formatting) ? null : formatting;
    }

    private ParagraphFormatting? ReadParagraphFormatting(XElement? pPr)
    {
        if (pPr is null) return null;
        var direction = IsOn(pPr.Element(W + "bidi")) ? TextDirectionMode.RightToLeft : (TextDirectionMode?)null;
        var alignment = ((string?)pPr.Element(W + "jc")?.Attribute(W + "val"))?.ToLowerInvariant() switch
        {
            "center" => TextAlignmentMode.Center,
            "right" => TextAlignmentMode.Right,
            "both" or "distribute" => TextAlignmentMode.Justified,
            "left" => TextAlignmentMode.Left,
            _ => (TextAlignmentMode?)null
        };
        var style = (string?)pPr.Element(W + "pStyle")?.Attribute(W + "val");
        var spacing = pPr.Element(W + "spacing");
        var ind = pPr.Element(W + "ind");
        var tabs = pPr.Element(W + "tabs")?.Elements(W + "tab").Select(ReadTab).Where(static tab => tab is not null).Cast<TabStop>().ToArray();
        var defaults = ReadCharacterFormatting(pPr.Element(W + "rPr"));
        var language = defaults?.Language;
        if (defaults is not null && language is not null) defaults = defaults with { Language = null };

        var paragraph = new ParagraphFormatting(
            StyleId: string.IsNullOrWhiteSpace(style) || style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) ? null : style,
            CharacterDefaults: defaults is not null && !IsEmpty(defaults) ? defaults : null,
            Alignment: alignment,
            Direction: direction,
            Language: language,
            LineSpacing: ParseLineSpacing(spacing),
            SpaceBeforePoints: ParseTwips(spacing, "before"),
            SpaceAfterPoints: ParseTwips(spacing, "after"),
            FirstLineIndentPoints: ParseTwips(ind, "firstLine"),
            LeftIndentPoints: ParseTwips(ind, "left"),
            RightIndentPoints: ParseTwips(ind, "right"),
            KeepWithNext: pPr.Element(W + "keepNext") is null ? null : IsOn(pPr.Element(W + "keepNext")),
            KeepLinesTogether: pPr.Element(W + "keepLines") is null ? null : IsOn(pPr.Element(W + "keepLines")),
            Tabs: tabs is { Length: > 0 } ? tabs : null);
        return IsEmpty(paragraph) ? null : paragraph;
    }

    private static TabStop? ReadTab(XElement element)
    {
        if (!int.TryParse((string?)element.Attribute(W + "pos"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var twips)) return null;
        var alignment = ((string?)element.Attribute(W + "val"))?.ToLowerInvariant() switch
        {
            "center" => TabStopAlignment.Center,
            "right" => TabStopAlignment.Right,
            "decimal" => TabStopAlignment.Decimal,
            _ => TabStopAlignment.Left
        };
        var leader = ((string?)element.Attribute(W + "leader"))?.ToLowerInvariant() switch
        {
            "dot" => '.',
            "hyphen" => '-',
            "middleDot" => '·',
            _ => (char?)null
        };
        return new TabStop(twips / 20d, alignment, leader);
    }

    private void AppendRichTable(StringBuilder output, XElement table)
    {
        var rows = table.Elements(W + "tr")
            .Select(row => row.Elements(W + "tc")
                .Select(cell =>
                {
                    var paragraph = cell.Elements(W + "p").FirstOrDefault();
                    return paragraph is null ? string.Empty : ReadRichParagraphMarkup(paragraph).Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
                })
                .ToArray())
            .ToArray();
        if (rows.Length == 0) return;
        var width = rows.Max(static row => row.Length);
        var header = rows[0].Concat(Enumerable.Repeat(string.Empty, width - rows[0].Length)).ToArray();
        output.Append("| ").Append(string.Join(" | ", header)).AppendLine(" |");
        output.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", width))).AppendLine(" |");
        foreach (var row in rows.Skip(1))
        {
            var cells = row.Concat(Enumerable.Repeat(string.Empty, width - row.Length));
            output.Append("| ").Append(string.Join(" | ", cells)).AppendLine(" |");
        }
    }

    private static CharacterFormatting Merge(CharacterFormatting outer, CharacterFormatting inner)
        => new(
            StyleId: inner.StyleId ?? outer.StyleId,
            Font: inner.Font ?? outer.Font,
            FontSizePoints: inner.FontSizePoints ?? outer.FontSizePoints,
            Bold: inner.Bold ?? outer.Bold,
            Italic: inner.Italic ?? outer.Italic,
            Underline: inner.Underline ?? outer.Underline,
            SmallCaps: inner.SmallCaps ?? outer.SmallCaps,
            Ligatures: inner.Ligatures ?? outer.Ligatures,
            Kerning: inner.Kerning ?? outer.Kerning,
            TrackingEm: inner.TrackingEm ?? outer.TrackingEm,
            BaselineShiftPoints: inner.BaselineShiftPoints ?? outer.BaselineShiftPoints,
            ColorHex: inner.ColorHex ?? outer.ColorHex,
            Language: inner.Language ?? outer.Language,
            Script: inner.Script ?? outer.Script,
            Direction: inner.Direction ?? outer.Direction,
            OpenTypeFeatures: inner.OpenTypeFeatures ?? outer.OpenTypeFeatures,
            VariableAxes: inner.VariableAxes ?? outer.VariableAxes);

    private static string FormatCitation(CitationInline citation, IReadOnlyDictionary<string, BibliographyEntry> entries)
    {
        if (!entries.TryGetValue(citation.Key, out var entry))
            return string.IsNullOrWhiteSpace(citation.Locator) ? $"[{citation.Key}]" : $"[{citation.Key}, {citation.Locator}]";
        var author = string.IsNullOrWhiteSpace(entry.Author) ? citation.Key : entry.Author.Split(" and ", 2, StringSplitOptions.TrimEntries)[0];
        var core = string.IsNullOrWhiteSpace(entry.Year) ? author : $"{author}, {entry.Year}";
        return string.IsNullOrWhiteSpace(citation.Locator) ? $"({core})" : $"({core}, {citation.Locator})";
    }

    private static XElement EnsureRunProperties(XElement run)
    {
        var rPr = run.Element(W + "rPr");
        if (rPr is not null) return rPr;
        rPr = new XElement(W + "rPr");
        run.AddFirst(rPr);
        return rPr;
    }

    private static XElement EnsureChild(XElement parent, string name)
    {
        var child = parent.Element(W + name);
        if (child is not null) return child;
        child = new XElement(W + name);
        parent.Add(child);
        return child;
    }

    private static void SetElement(XElement parent, string name, string value)
    {
        var element = EnsureChild(parent, name);
        element.SetAttributeValue(W + "val", value);
    }

    private static void SetOnOff(XElement parent, string name, bool value)
    {
        var element = EnsureChild(parent, name);
        element.SetAttributeValue(W + "val", value ? "1" : "0");
    }

    private static bool IsOn(XElement? element)
    {
        if (element is null) return false;
        var value = (string?)element.Attribute(W + "val");
        return value is null || value is "1" or "true" or "on";
    }

    private static void SetTwipsAttribute(XElement element, string name, double? points)
    {
        if (points is null) return;
        element.SetAttributeValue(W + name, ToTwips(points.Value).ToString(CultureInfo.InvariantCulture));
    }

    private static int ToTwips(double points) => (int)Math.Round(points * 20);

    private static double? ParseTwips(XElement? element, string name)
        => int.TryParse((string?)element?.Attribute(W + name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value / 20d : null;

    private static double? ParseLineSpacing(XElement? spacing)
        => int.TryParse((string?)spacing?.Attribute(W + "line"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value / 240d : null;

    private static bool IsEmpty(CharacterFormatting value)
        => value.StyleId is null && value.Font is null && value.FontSizePoints is null && value.Bold is null && value.Italic is null &&
           value.Underline is null && value.SmallCaps is null && value.Ligatures is null && value.Kerning is null && value.TrackingEm is null &&
           value.BaselineShiftPoints is null && value.ColorHex is null && value.Language is null && value.Script is null && value.Direction is null &&
           value.OpenTypeFeatures is null && value.VariableAxes is null;

    private static bool IsEmpty(ParagraphFormatting value)
        => value.StyleId is null && value.CharacterDefaults is null && value.Alignment is null && value.Direction is null && value.Language is null &&
           value.Script is null && value.LineSpacing is null && value.SpaceBeforePoints is null && value.SpaceAfterPoints is null && value.FirstLineIndentPoints is null &&
           value.LeftIndentPoints is null && value.RightIndentPoints is null && value.KeepWithNext is null && value.KeepLinesTogether is null &&
           value.KeepFirstLines is null && value.KeepLastLines is null && value.OpticalMarginAlignment is null && value.Hyphenation is null &&
           value.DropCap is null && value.Rules is null && value.Tabs is null;

    private static string EscapeMarkdownText(string value)
    {
        var output = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            if (ch is '\\' or '*' or '_' or '[' or ']') output.Append('\\');
            output.Append(ch);
        }
        return output.ToString();
    }

    private static XDocument? LoadXml(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path);
        if (entry is null) return null;
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static void ReplaceXml(ZipArchive archive, string path, XDocument document)
    {
        archive.GetEntry(path)?.Delete();
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        document.Save(stream, SaveOptions.DisableFormatting);
    }

    private static Dictionary<int, string> LoadNumbering(ZipArchive archive)
    {
        var document = LoadXml(archive, "word/numbering.xml");
        if (document?.Root is null) return [];
        var formats = new Dictionary<int, string>();
        foreach (var abstractNumbering in document.Root.Elements(W + "abstractNum"))
        {
            if (!int.TryParse((string?)abstractNumbering.Attribute(W + "abstractNumId"), out var id)) continue;
            formats[id] = (string?)abstractNumbering.Descendants(W + "numFmt").FirstOrDefault()?.Attribute(W + "val") ?? "decimal";
        }
        var result = new Dictionary<int, string>();
        foreach (var numbering in document.Root.Elements(W + "num"))
        {
            if (!int.TryParse((string?)numbering.Attribute(W + "numId"), out var numId)) continue;
            if (!int.TryParse((string?)numbering.Element(W + "abstractNumId")?.Attribute(W + "val"), out var abstractId)) continue;
            if (formats.TryGetValue(abstractId, out var format)) result[numId] = format;
        }
        return result;
    }

    private void Warn(string message)
    {
        if (!_warnings.Contains(message, StringComparer.Ordinal)) _warnings.Add(message);
    }

    private sealed record ExpectedRun(
        string BaseText,
        CharacterFormatting? Formatting,
        bool Bold,
        bool Italic,
        string? Style,
        bool FootnoteReference,
        IReadOnlyList<RunReplacement>? Replacements);

    private sealed record RunReplacement(
        string Text,
        CharacterFormatting Formatting,
        bool Bold,
        bool Italic,
        string? Style);
}
