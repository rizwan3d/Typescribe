using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

/// <summary>
/// Enriches the proven EPUB 3 package writer with TypeScribe's language, direction and character
/// formatting metadata. XHTML remains semantic; rich typography is expressed as lang/dir and CSS.
/// Project fonts are embedded only when explicitly allow-listed by the project owner.
/// </summary>
public sealed class RichEpub3ExportService
{
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private readonly IDocumentParser _parser;
    private readonly Epub3ExportService _inner;
    private readonly List<string> _warnings = [];

    public RichEpub3ExportService(IDocumentParser parser)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _inner = new Epub3ExportService(parser);
    }

    public IReadOnlyList<string> LastWarnings => _warnings;

    public async Task ExportAsync(
        BookProject project,
        IReadOnlyList<EpubDocumentSource> documents,
        string destination,
        CancellationToken cancellationToken = default)
    {
        _warnings.Clear();
        await _inner.ExportAsync(project, documents, destination, cancellationToken);
        await EnrichAsync(project, documents, destination, cancellationToken);
    }

    private async Task EnrichAsync(
        BookProject project,
        IReadOnlyList<EpubDocumentSource> documents,
        string destination,
        CancellationToken cancellationToken)
    {
        var parsed = documents.Select(document => _parser.Parse(document.Content ?? string.Empty)).ToArray();
        var fontReferences = CollectProjectFonts(parsed);
        var permissions = LoadEmbeddingPermissions(project.RootPath);

        await using var stream = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true);

        for (var index = 0; index < parsed.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = $"chapters/chapter-{index + 1:000}.xhtml";
            var chapter = LoadXml(archive, path);
            if (chapter?.Root is null) continue;
            ApplyChapterFormatting(chapter, parsed[index]);
            ReplaceXml(archive, path, chapter);
        }

        var embedded = new List<EmbeddedFont>();
        foreach (var font in fontReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (font.Source != FontSourceKind.Project || string.IsNullOrWhiteSpace(font.ProjectPath)) continue;
            var relative = NormalizeRelative(font.ProjectPath!);
            if (!permissions.Contains(relative))
            {
                Warn($"Project font '{font.Family}' was not embedded in EPUB because '{relative}' is not listed in fonts/embedding-permissions.txt. Text and the CSS font-family reference were preserved.");
                continue;
            }

            var fullPath = ResolveProjectFile(project.RootPath, relative);
            if (fullPath is null || !File.Exists(fullPath))
            {
                Warn($"Project font '{font.Family}' could not be embedded because '{relative}' was not found inside the project.");
                continue;
            }

            var mediaType = FontMediaType(fullPath);
            if (mediaType is null)
            {
                Warn($"Project font '{font.Family}' uses an unsupported EPUB font format: {Path.GetExtension(fullPath)}.");
                continue;
            }

            var fileName = UniqueFontName(Path.GetFileName(fullPath), embedded.Select(static item => item.PackagePath));
            var packagePath = "fonts/" + fileName;
            var entry = archive.CreateEntry(packagePath, CompressionLevel.Optimal);
            await using (var target = entry.Open())
            await using (var source = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await source.CopyToAsync(target, cancellationToken);
            embedded.Add(new EmbeddedFont(font.Family, packagePath, mediaType));
        }

        if (embedded.Count > 0)
        {
            AddFontsToManifest(archive, embedded);
            AppendFontCss(archive, embedded);
        }
    }

    private void ApplyChapterFormatting(XDocument chapter, DocumentAst ast)
    {
        var section = chapter.Descendants(Xhtml + "section")
            .FirstOrDefault(element => HasClass(element, "chapter"));
        if (section is null) return;

        var children = section.Elements().ToList();
        var childIndex = 0;
        for (var blockIndex = 0; blockIndex < ast.Blocks.Count; blockIndex++)
        {
            var block = ast.Blocks[blockIndex];
            if (block is FootnoteDefinitionBlock or BibliographyEntryBlock) continue;
            if (childIndex >= children.Count) break;

            if (block is ListItemBlock listItem)
            {
                var list = children[childIndex];
                if (list.Name != Xhtml + (listItem.Ordered ? "ol" : "ul"))
                {
                    childIndex++;
                    continue;
                }
                var items = list.Elements(Xhtml + "li").ToArray();
                var itemIndex = 0;
                while (blockIndex < ast.Blocks.Count && ast.Blocks[blockIndex] is ListItemBlock item && item.Ordered == listItem.Ordered && itemIndex < items.Length)
                {
                    ApplyBlockAttributes(items[itemIndex], item.Formatting?.Paragraph);
                    ApplyRichSpans(items[itemIndex], item.Inlines);
                    itemIndex++;
                    blockIndex++;
                }
                blockIndex--;
                childIndex++;
                continue;
            }

            var element = children[childIndex++];
            switch (block)
            {
                case HeadingBlock heading:
                    ApplyBlockAttributes(element, heading.Formatting?.Paragraph);
                    ApplyRichSpans(element, heading.Inlines);
                    break;
                case ParagraphBlock paragraph:
                    ApplyBlockAttributes(element, paragraph.Formatting?.Paragraph);
                    ApplyRichSpans(element, paragraph.Inlines);
                    break;
                case QuoteBlock quote:
                    ApplyBlockAttributes(element, quote.Formatting?.Paragraph);
                    ApplyRichSpans(element.Descendants(Xhtml + "p").FirstOrDefault() ?? element, quote.Inlines);
                    break;
                case CodeBlock:
                case DisplayMathBlock:
                case ThematicBreakBlock:
                case FigureBlock:
                    ApplyBlockAttributes(element, block.Formatting?.Paragraph);
                    break;
                case TableBlock table:
                    ApplyBlockAttributes(element, table.Formatting?.Paragraph);
                    ApplyTableSpans(element, table);
                    break;
            }
        }

        var notes = section.Elements(Xhtml + "section").FirstOrDefault(element => HasClass(element, "footnotes"));
        if (notes is null) return;
        foreach (var definition in ast.Blocks.OfType<FootnoteDefinitionBlock>())
        {
            var id = "fn-" + HtmlId(definition.Identifier);
            var aside = notes.Descendants(Xhtml + "aside").FirstOrDefault(element => string.Equals((string?)element.Attribute("id"), id, StringComparison.Ordinal));
            var paragraph = aside?.Element(Xhtml + "p");
            if (paragraph is not null) ApplyRichSpans(paragraph, definition.Inlines);
        }
    }

    private void ApplyTableSpans(XElement tableElement, TableBlock table)
    {
        var rows = tableElement.Descendants(Xhtml + "tr").ToArray();
        var sourceRows = new[] { table.Header }.Concat(table.Rows).ToArray();
        for (var row = 0; row < Math.Min(rows.Length, sourceRows.Length); row++)
        {
            var cells = rows[row].Elements().Where(element => element.Name is { LocalName: "td" or "th" }).ToArray();
            for (var column = 0; column < Math.Min(cells.Length, sourceRows[row].Count); column++)
                ApplyRichSpans(cells[column], sourceRows[row][column].Inlines);
        }
    }

    private void ApplyRichSpans(XElement container, IReadOnlyList<AstInline> inlines)
    {
        foreach (var rich in EnumerateRichSpans(inlines))
        {
            if (rich.Text.Length == 0) continue;
            if (!WrapFirstTextOccurrence(container, rich.Text, rich.Formatting))
                Warn($"EPUB rich formatting could not be attached to one text run ('{Preview(rich.Text)}'); the Unicode text was still preserved.");
        }
    }

    private static IEnumerable<RichSpanInline> EnumerateRichSpans(IEnumerable<AstInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case RichSpanInline rich:
                    yield return rich;
                    // Nested rich spans are already semantically contained by the outer range.
                    break;
                case StrongInline strong:
                    foreach (var child in EnumerateRichSpans(strong.Children)) yield return child;
                    break;
                case EmphasisInline emphasis:
                    foreach (var child in EnumerateRichSpans(emphasis.Children)) yield return child;
                    break;
                case LinkInline link:
                    foreach (var child in EnumerateRichSpans(link.Label)) yield return child;
                    break;
            }
        }
    }

    private bool WrapFirstTextOccurrence(XElement container, string value, CharacterFormatting formatting)
    {
        var textNodes = container.DescendantNodes().OfType<XText>().ToArray();
        foreach (var node in textNodes)
        {
            var index = node.Value.IndexOf(value, StringComparison.Ordinal);
            if (index < 0) continue;
            var before = node.Value[..index];
            var after = node.Value[(index + value.Length)..];
            var span = new XElement(Xhtml + "span", value);
            ApplyCharacterAttributes(span, formatting);
            var replacements = new List<object>();
            if (before.Length > 0) replacements.Add(new XText(before));
            replacements.Add(span);
            if (after.Length > 0) replacements.Add(new XText(after));
            node.ReplaceWith(replacements);
            return true;
        }
        return false;
    }

    private void ApplyBlockAttributes(XElement element, ParagraphFormatting? formatting)
    {
        if (formatting is null) return;
        if (!string.IsNullOrWhiteSpace(formatting.Language))
        {
            element.SetAttributeValue("lang", formatting.Language);
            element.SetAttributeValue(XNamespace.Xml + "lang", formatting.Language);
        }
        if (formatting.Direction == TextDirectionMode.RightToLeft) element.SetAttributeValue("dir", "rtl");
        else if (formatting.Direction == TextDirectionMode.LeftToRight) element.SetAttributeValue("dir", "ltr");

        var css = new List<string>();
        if (formatting.CharacterDefaults is { } defaults)
        {
            var characterCss = CharacterCss(defaults);
            if (characterCss.Length > 0) css.Add(characterCss);
        }
        if (formatting.Alignment is { } alignment)
            css.Add("text-align:" + (alignment == TextAlignmentMode.Justified ? "justify" : alignment.ToString().ToLowerInvariant()));
        if (formatting.LineSpacing is { } line) css.Add("line-height:" + line.ToString("0.###", CultureInfo.InvariantCulture));
        if (formatting.SpaceBeforePoints is { } before) css.Add("margin-top:" + Pt(before));
        if (formatting.SpaceAfterPoints is { } after) css.Add("margin-bottom:" + Pt(after));
        if (formatting.FirstLineIndentPoints is { } first) css.Add("text-indent:" + Pt(first));
        if (formatting.LeftIndentPoints is { } left) css.Add("margin-left:" + Pt(left));
        if (formatting.RightIndentPoints is { } right) css.Add("margin-right:" + Pt(right));
        if (formatting.KeepWithNext == true || formatting.KeepLinesTogether == true) css.Add("break-inside:avoid");
        if (formatting.Hyphenation is { } hyphenation) css.Add("hyphens:" + (hyphenation ? "auto" : "none"));
        if (formatting.OpticalMarginAlignment == true)
            Warn("EPUB readers do not consistently support optical margin alignment; the canonical Markdown metadata remains authoritative.");
        if (formatting.Tabs is { Count: > 0 })
            Warn("EPUB reflow layout cannot portably preserve paragraph tab-stop positions; tabs remain in canonical Markdown metadata.");

        if (css.Count > 0) MergeStyle(element, string.Join(';', css));
    }

    private static void ApplyCharacterAttributes(XElement span, CharacterFormatting formatting)
    {
        if (!string.IsNullOrWhiteSpace(formatting.Language))
        {
            span.SetAttributeValue("lang", formatting.Language);
            span.SetAttributeValue(XNamespace.Xml + "lang", formatting.Language);
        }
        if (formatting.Direction == TextDirectionMode.RightToLeft) span.SetAttributeValue("dir", "rtl");
        else if (formatting.Direction == TextDirectionMode.LeftToRight) span.SetAttributeValue("dir", "ltr");
        var css = CharacterCss(formatting);
        if (css.Length > 0) span.SetAttributeValue("style", css);
    }

    private static string CharacterCss(CharacterFormatting formatting)
    {
        var css = new List<string>();
        if (formatting.Font is { Family.Length: > 0 } font) css.Add("font-family:" + CssQuoted(font.Family));
        if (formatting.FontSizePoints is { } size && size > 0) css.Add("font-size:" + Pt(size));
        if (formatting.Bold == true) css.Add("font-weight:700");
        else if (formatting.Bold == false) css.Add("font-weight:400");
        if (formatting.Italic == true) css.Add("font-style:italic");
        else if (formatting.Italic == false) css.Add("font-style:normal");
        if (formatting.Underline == true) css.Add("text-decoration:underline");
        else if (formatting.Underline == false) css.Add("text-decoration:none");
        if (formatting.SmallCaps == true) css.Add("font-variant-caps:small-caps");
        if (formatting.TrackingEm is { } tracking) css.Add("letter-spacing:" + tracking.ToString("0.###", CultureInfo.InvariantCulture) + "em");
        if (formatting.Kerning is { } kerning) css.Add("font-kerning:" + (kerning ? "normal" : "none"));
        if (formatting.Ligatures is { } ligatures) css.Add("font-variant-ligatures:" + (ligatures ? "common-ligatures" : "none"));
        if (formatting.OpenTypeFeatures is { Count: > 0 } features)
            css.Add("font-feature-settings:" + string.Join(',', features.Select(static feature => $"\"{feature.Tag}\" {feature.Value}")));
        if (formatting.VariableAxes is { Count: > 0 } axes)
            css.Add("font-variation-settings:" + string.Join(',', axes.Select(static axis => $"\"{axis.Tag}\" {axis.Value.ToString(CultureInfo.InvariantCulture)}")));
        if (formatting.BaselineShiftPoints is { } shift) css.Add("position:relative;top:" + Pt(-shift));
        if (!string.IsNullOrWhiteSpace(formatting.ColorHex)) css.Add("color:" + formatting.ColorHex);
        return string.Join(';', css);
    }

    private static HashSet<FontReference> CollectProjectFonts(IEnumerable<DocumentAst> documents)
    {
        var result = new HashSet<FontReference>();
        foreach (var document in documents)
        {
            foreach (var block in document.Blocks)
            {
                if (block.Formatting?.Paragraph?.CharacterDefaults?.Font is { Source: FontSourceKind.Project } paragraphFont)
                    result.Add(paragraphFont);
                foreach (var inline in BlockInlines(block)) CollectFonts(inline, result);
            }
        }
        return result;
    }

    private static IEnumerable<AstInline> BlockInlines(AstBlock block)
        => block switch
        {
            HeadingBlock heading => heading.Inlines,
            ParagraphBlock paragraph => paragraph.Inlines,
            QuoteBlock quote => quote.Inlines,
            ListItemBlock item => item.Inlines,
            FootnoteDefinitionBlock note => note.Inlines,
            TableBlock table => table.Header.Concat(table.Rows.SelectMany(static row => row)).SelectMany(static cell => cell.Inlines),
            _ => []
        };

    private static void CollectFonts(AstInline inline, ISet<FontReference> result)
    {
        switch (inline)
        {
            case RichSpanInline rich:
                if (rich.Formatting.Font is { Source: FontSourceKind.Project } font) result.Add(font);
                foreach (var child in rich.Children) CollectFonts(child, result);
                break;
            case StrongInline strong:
                foreach (var child in strong.Children) CollectFonts(child, result);
                break;
            case EmphasisInline emphasis:
                foreach (var child in emphasis.Children) CollectFonts(child, result);
                break;
            case LinkInline link:
                foreach (var child in link.Label) CollectFonts(child, result);
                break;
        }
    }

    private static HashSet<string> LoadEmbeddingPermissions(string projectRoot)
    {
        var path = Path.Combine(projectRoot, "fonts", "embedding-permissions.txt");
        if (!File.Exists(path)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return File.ReadLines(path)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0 && !line.StartsWith('#'))
            .Select(NormalizeRelative)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string? ResolveProjectFile(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) return null;
        try
        {
            var projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var candidate = Path.GetFullPath(Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            var back = Path.GetRelativePath(projectRoot, candidate);
            if (Path.IsPathRooted(back) || back == ".." || back.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
            return candidate;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string NormalizeRelative(string path)
        => path.Replace('\\', '/').TrimStart('/').Trim();

    private static string? FontMediaType(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".otf" or ".ttf" or ".ttc" => "application/vnd.ms-opentype",
            ".woff" => "application/font-woff",
            ".woff2" => "font/woff2",
            _ => null
        };

    private static string UniqueFontName(string fileName, IEnumerable<string> existingPackagePaths)
    {
        var existing = existingPackagePaths.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(fileName)) return fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var suffix = 2;
        while (existing.Contains($"{stem}-{suffix}{extension}")) suffix++;
        return $"{stem}-{suffix}{extension}";
    }

    private static void AddFontsToManifest(ZipArchive archive, IReadOnlyList<EmbeddedFont> fonts)
    {
        var document = LoadXml(archive, "package.opf");
        var manifest = document?.Root?.Element(Opf + "manifest");
        if (document is null || manifest is null) return;
        for (var index = 0; index < fonts.Count; index++)
        {
            manifest.Add(new XElement(Opf + "item",
                new XAttribute("id", $"typescribe-font-{index + 1}"),
                new XAttribute("href", fonts[index].PackagePath),
                new XAttribute("media-type", fonts[index].MediaType)));
        }
        ReplaceXml(archive, "package.opf", document);
    }

    private static void AppendFontCss(ZipArchive archive, IReadOnlyList<EmbeddedFont> fonts)
    {
        var entry = archive.GetEntry("styles/book.css");
        var existing = string.Empty;
        if (entry is not null)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            existing = reader.ReadToEnd();
            entry.Delete();
        }
        var css = new StringBuilder(existing.TrimEnd()).AppendLine().AppendLine();
        foreach (var font in fonts)
        {
            css.Append("@font-face{font-family:").Append(CssQuoted(font.Family))
                .Append(";src:url('../").Append(font.PackagePath.Replace("'", "%27", StringComparison.Ordinal))
                .AppendLine("');font-display:swap;}");
        }
        var replacement = archive.CreateEntry("styles/book.css", CompressionLevel.Optimal);
        using var stream = replacement.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(css.ToString());
    }

    private static void MergeStyle(XElement element, string added)
    {
        var current = (string?)element.Attribute("style");
        element.SetAttributeValue("style", string.IsNullOrWhiteSpace(current) ? added : current.TrimEnd(';') + ";" + added);
    }

    private static bool HasClass(XElement element, string value)
        => ((string?)element.Attribute("class"))?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(value, StringComparer.Ordinal) == true;

    private static string CssQuoted(string value) => "'" + value.Replace("'", "\\'", StringComparison.Ordinal) + "'";
    private static string Pt(double value) => value.ToString("0.###", CultureInfo.InvariantCulture) + "pt";
    private static string Preview(string text) => text.Length <= 28 ? text : text[..28] + "…";

    private static string HtmlId(string value)
    {
        var builder = new StringBuilder(value.Length + 4);
        foreach (var character in value.Trim()) builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or ':' ? character : '-');
        return builder.Length == 0 ? "section" : builder.ToString();
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

    private void Warn(string message)
    {
        if (!_warnings.Contains(message, StringComparer.Ordinal)) _warnings.Add(message);
    }

    private sealed record EmbeddedFont(string Family, string PackagePath, string MediaType);
}