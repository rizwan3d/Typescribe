using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

/// <summary>
/// Editorial DOCX interchange. It intentionally maps semantic manuscript structure rather than
/// attempting arbitrary Word page-layout fidelity.
/// </summary>
public sealed class DocxInterchangeService(IDocumentParser parser, BibTeXDatabase bibliography)
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";

    public async Task ExportAsync(
        BookProject project,
        IReadOnlyList<(ProjectNode Node, string Content)> documents,
        string destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var bib = (await bibliography.LoadAsync(project.RootPath, cancellationToken))
            .ToDictionary(static entry => entry.CitationKey, StringComparer.OrdinalIgnoreCase);
        var footnotes = new List<(int Id, IReadOnlyList<AstInline> Content)>();
        var media = new List<MediaPart>();
        var body = new XElement(W + "body");
        var imageNumber = 1;
        var footnoteId = 1;

        for (var documentIndex = 0; documentIndex < documents.Count; documentIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ast = parser.Parse(documents[documentIndex].Content);
            var definitions = ast.Blocks.OfType<FootnoteDefinitionBlock>()
                .GroupBy(static item => item.Identifier, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.Last().Inlines, StringComparer.OrdinalIgnoreCase);

            foreach (var block in ast.Blocks)
            {
                switch (block)
                {
                    case HeadingBlock heading:
                        body.Add(Paragraph(heading.Inlines, bib, definitions, footnotes, ref footnoteId, style: $"Heading{Math.Clamp(heading.Level, 1, 6)}"));
                        break;
                    case ParagraphBlock paragraph:
                        body.Add(Paragraph(paragraph.Inlines, bib, definitions, footnotes, ref footnoteId));
                        break;
                    case QuoteBlock quote:
                        body.Add(Paragraph(quote.Inlines, bib, definitions, footnotes, ref footnoteId, style: "Quote"));
                        break;
                    case ListItemBlock item:
                        body.Add(ListParagraph(item, bib, definitions, footnotes, ref footnoteId));
                        break;
                    case DisplayMathBlock equation:
                        body.Add(TextParagraph(equation.Text, style: "Equation"));
                        break;
                    case CodeBlock code:
                        body.Add(TextParagraph(code.Text, style: "Code"));
                        break;
                    case ThematicBreakBlock:
                        body.Add(TextParagraph("***"));
                        break;
                    case TableBlock table:
                        body.Add(CreateTable(table, bib, definitions, footnotes, ref footnoteId));
                        if (!string.IsNullOrWhiteSpace(table.Caption)) body.Add(TextParagraph(table.Caption!, style: "Caption"));
                        break;
                    case FigureBlock figure:
                    {
                        var imagePath = ResolveProjectPath(project.RootPath, figure.Source);
                        if (imagePath is not null && File.Exists(imagePath))
                        {
                            var extension = Path.GetExtension(imagePath).ToLowerInvariant();
                            var mediaName = $"image{imageNumber++}{extension}";
                            var relationshipId = $"rIdImage{media.Count + 10}";
                            media.Add(new MediaPart(imagePath, mediaName, relationshipId, ContentTypeForImage(extension)));
                            body.Add(ImageParagraph(relationshipId, figure.Caption));
                        }
                        else
                        {
                            body.Add(TextParagraph($"[Missing image: {figure.Source}]"));
                        }
                        if (!string.IsNullOrWhiteSpace(figure.Caption)) body.Add(TextParagraph(figure.Caption, style: "Caption"));
                        break;
                    }
                    case FootnoteDefinitionBlock:
                    case BibliographyEntryBlock:
                        break;
                }
            }

            if (documentIndex < documents.Count - 1)
                body.Add(new XElement(W + "p", new XElement(W + "r", new XElement(W + "br", new XAttribute(W + "type", "page")))));
        }

        body.Add(new XElement(W + "sectPr"));
        var documentXml = new XDocument(new XElement(W + "document",
            new XAttribute(XNamespace.Xmlns + "w", W),
            new XAttribute(XNamespace.Xmlns + "r", R),
            new XAttribute(XNamespace.Xmlns + "wp", Wp),
            new XAttribute(XNamespace.Xmlns + "a", A),
            new XAttribute(XNamespace.Xmlns + "pic", Pic),
            body));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                WriteXml(archive, "[Content_Types].xml", ContentTypes(media));
                WriteXml(archive, "_rels/.rels", PackageRelationships());
                WriteXml(archive, "word/document.xml", documentXml);
                WriteXml(archive, "word/styles.xml", Styles());
                WriteXml(archive, "word/numbering.xml", Numbering());
                WriteXml(archive, "word/_rels/document.xml.rels", DocumentRelationships(media, footnotes.Count > 0));
                if (footnotes.Count > 0) WriteXml(archive, "word/footnotes.xml", Footnotes(footnotes, bib));
                foreach (var part in media)
                {
                    var entry = archive.CreateEntry("word/media/" + part.Name, CompressionLevel.Optimal);
                    await using var target = entry.Open();
                    await using var source = new FileStream(part.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await source.CopyToAsync(target, cancellationToken);
                }
            }
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public async Task<string> ImportAsync(BookProject project, string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        await using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var document = LoadXml(archive, "word/document.xml") ?? throw new InvalidOperationException("DOCX does not contain word/document.xml.");
        var relationships = LoadRelationships(archive);
        var numbering = LoadNumbering(archive);
        var footnoteTexts = LoadFootnotes(archive);
        var extractedMedia = await ExtractMediaAsync(project, archive, relationships, cancellationToken);
        var output = new StringBuilder();
        var usedFootnotes = new HashSet<int>();

        var body = document.Root?.Element(W + "body");
        if (body is null) return string.Empty;
        foreach (var element in body.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element.Name == W + "p")
            {
                var drawingIds = element.Descendants(A + "blip")
                    .Select(blip => (string?)blip.Attribute(R + "embed"))
                    .Where(static id => !string.IsNullOrWhiteSpace(id))
                    .ToArray();
                foreach (var id in drawingIds)
                {
                    if (id is not null && extractedMedia.TryGetValue(id, out var image))
                        output.Append("![Image](").Append(image).AppendLine(")");
                }

                var style = (string?)element.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val");
                var numIdText = (string?)element.Element(W + "pPr")?.Element(W + "numPr")?.Element(W + "numId")?.Attribute(W + "val");
                var prefix = string.Empty;
                if (style?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true && int.TryParse(style[7..], out var level))
                    prefix = new string('#', Math.Clamp(level, 1, 6)) + " ";
                else if (int.TryParse(numIdText, out var numId))
                    prefix = numbering.TryGetValue(numId, out var format) && format == "bullet" ? "- " : "1. ";

                var text = ReadParagraphMarkup(element, usedFootnotes);
                if (drawingIds.Length == 0 || text.Length > 0)
                    output.Append(prefix).AppendLine(text);
                if (element.Descendants(W + "br").Any(br => string.Equals((string?)br.Attribute(W + "type"), "page", StringComparison.OrdinalIgnoreCase)))
                    output.AppendLine("<!-- pagebreak -->");
            }
            else if (element.Name == W + "tbl")
            {
                AppendImportedTable(output, element);
            }
        }

        foreach (var id in usedFootnotes.Order())
        {
            if (footnoteTexts.TryGetValue(id, out var text))
                output.Append("[^fn").Append(id).Append("]: ").AppendLine(text);
        }
        return output.ToString().TrimEnd() + Environment.NewLine;
    }

    private static XElement Paragraph(
        IReadOnlyList<AstInline> inlines,
        IReadOnlyDictionary<string, BibliographyEntry> bibliography,
        IReadOnlyDictionary<string, IReadOnlyList<AstInline>> definitions,
        List<(int Id, IReadOnlyList<AstInline> Content)> footnotes,
        ref int footnoteId,
        string? style = null)
    {
        var paragraph = new XElement(W + "p");
        if (!string.IsNullOrWhiteSpace(style)) paragraph.Add(new XElement(W + "pPr", new XElement(W + "pStyle", new XAttribute(W + "val", style))));
        AppendRuns(paragraph, inlines, bibliography, definitions, footnotes, ref footnoteId, bold: false, italic: false);
        return paragraph;
    }

    private static XElement ListParagraph(
        ListItemBlock item,
        IReadOnlyDictionary<string, BibliographyEntry> bibliography,
        IReadOnlyDictionary<string, IReadOnlyList<AstInline>> definitions,
        List<(int Id, IReadOnlyList<AstInline> Content)> footnotes,
        ref int footnoteId)
    {
        var paragraph = new XElement(W + "p",
            new XElement(W + "pPr",
                new XElement(W + "numPr",
                    new XElement(W + "ilvl", new XAttribute(W + "val", "0")),
                    new XElement(W + "numId", new XAttribute(W + "val", item.Ordered ? "2" : "1")))));
        AppendRuns(paragraph, item.Inlines, bibliography, definitions, footnotes, ref footnoteId, bold: false, italic: false);
        return paragraph;
    }

    private static XElement TextParagraph(string text, string? style = null)
    {
        var paragraph = new XElement(W + "p");
        if (!string.IsNullOrWhiteSpace(style)) paragraph.Add(new XElement(W + "pPr", new XElement(W + "pStyle", new XAttribute(W + "val", style))));
        paragraph.Add(Run(text));
        return paragraph;
    }

    private static void AppendRuns(
        XElement parent,
        IEnumerable<AstInline> inlines,
        IReadOnlyDictionary<string, BibliographyEntry> bibliography,
        IReadOnlyDictionary<string, IReadOnlyList<AstInline>> definitions,
        List<(int Id, IReadOnlyList<AstInline> Content)> footnotes,
        ref int footnoteId,
        bool bold,
        bool italic)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextInline text:
                    parent.Add(Run(text.Text, bold, italic));
                    break;
                case StrongInline strong:
                    AppendRuns(parent, strong.Children, bibliography, definitions, footnotes, ref footnoteId, bold: true, italic);
                    break;
                case EmphasisInline emphasis:
                    AppendRuns(parent, emphasis.Children, bibliography, definitions, footnotes, ref footnoteId, bold, italic: true);
                    break;
                case CodeInline code:
                    parent.Add(Run(code.Text, bold, italic, style: "CodeChar"));
                    break;
                case MathInline math:
                    parent.Add(Run("$" + math.Text + "$", bold, italic));
                    break;
                case LinkInline link:
                    AppendRuns(parent, link.Label, bibliography, definitions, footnotes, ref footnoteId, bold, italic);
                    parent.Add(Run(" (" + link.Url + ")", bold, italic));
                    break;
                case CitationInline citation:
                    parent.Add(Run(FormatCitation(citation, bibliography), bold, italic));
                    break;
                case CrossReferenceInline reference:
                    parent.Add(Run("[@ref:" + reference.Identifier + "]", bold, italic));
                    break;
                case FootnoteReferenceInline note:
                    if (definitions.TryGetValue(note.Identifier, out var content))
                    {
                        var id = footnoteId++;
                        footnotes.Add((id, content));
                        parent.Add(new XElement(W + "r",
                            new XElement(W + "rPr", new XElement(W + "vertAlign", new XAttribute(W + "val", "superscript"))),
                            new XElement(W + "footnoteReference", new XAttribute(W + "id", id))));
                    }
                    else parent.Add(Run("[^" + note.Identifier + "]", bold, italic));
                    break;
            }
        }
    }

    private static XElement Run(string text, bool bold = false, bool italic = false, string? style = null)
    {
        var properties = new XElement(W + "rPr");
        if (bold) properties.Add(new XElement(W + "b"));
        if (italic) properties.Add(new XElement(W + "i"));
        if (!string.IsNullOrWhiteSpace(style)) properties.Add(new XElement(W + "rStyle", new XAttribute(W + "val", style)));
        return new XElement(W + "r", properties.HasElements ? properties : null,
            new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text));
    }

    private static XElement CreateTable(
        TableBlock table,
        IReadOnlyDictionary<string, BibliographyEntry> bibliography,
        IReadOnlyDictionary<string, IReadOnlyList<AstInline>> definitions,
        List<(int Id, IReadOnlyList<AstInline> Content)> footnotes,
        ref int footnoteId)
    {
        var element = new XElement(W + "tbl",
            new XElement(W + "tblPr", new XElement(W + "tblStyle", new XAttribute(W + "val", "TableGrid"))));
        element.Add(CreateTableRow(table.Header, bibliography, definitions, footnotes, ref footnoteId, header: true));
        foreach (var row in table.Rows)
            element.Add(CreateTableRow(row, bibliography, definitions, footnotes, ref footnoteId, header: false));
        return element;
    }

    private static XElement CreateTableRow(
        IReadOnlyList<TableCell> cells,
        IReadOnlyDictionary<string, BibliographyEntry> bibliography,
        IReadOnlyDictionary<string, IReadOnlyList<AstInline>> definitions,
        List<(int Id, IReadOnlyList<AstInline> Content)> footnotes,
        ref int footnoteId,
        bool header)
    {
        var row = new XElement(W + "tr");
        foreach (var cell in cells)
        {
            var paragraph = new XElement(W + "p");
            AppendRuns(paragraph, cell.Inlines, bibliography, definitions, footnotes, ref footnoteId, bold: header, italic: false);
            row.Add(new XElement(W + "tc", paragraph));
        }
        return row;
    }

    private static XElement ImageParagraph(string relationshipId, string caption)
    {
        var drawing = new XElement(W + "drawing",
            new XElement(Wp + "inline",
                new XElement(Wp + "extent", new XAttribute("cx", "5486400"), new XAttribute("cy", "3657600")),
                new XElement(Wp + "docPr", new XAttribute("id", "1"), new XAttribute("name", string.IsNullOrWhiteSpace(caption) ? "Figure" : caption)),
                new XElement(A + "graphic",
                    new XElement(A + "graphicData", new XAttribute("uri", "http://schemas.openxmlformats.org/drawingml/2006/picture"),
                        new XElement(Pic + "pic",
                            new XElement(Pic + "nvPicPr", new XElement(Pic + "cNvPr", new XAttribute("id", "0"), new XAttribute("name", caption)), new XElement(Pic + "cNvPicPr")),
                            new XElement(Pic + "blipFill", new XElement(A + "blip", new XAttribute(R + "embed", relationshipId)), new XElement(A + "stretch", new XElement(A + "fillRect"))),
                            new XElement(Pic + "spPr", new XElement(A + "xfrm", new XElement(A + "off", new XAttribute("x", "0"), new XAttribute("y", "0")), new XElement(A + "ext", new XAttribute("cx", "5486400"), new XAttribute("cy", "3657600"))), new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst")))))))));
        return new XElement(W + "p", new XElement(W + "r", drawing));
    }

    private static XDocument ContentTypes(IEnumerable<MediaPart> media)
    {
        XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
        var root = new XElement(ct + "Types",
            new XElement(ct + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(ct + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
            new XElement(ct + "Override", new XAttribute("PartName", "/word/document.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml")),
            new XElement(ct + "Override", new XAttribute("PartName", "/word/styles.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml")),
            new XElement(ct + "Override", new XAttribute("PartName", "/word/numbering.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml")),
            new XElement(ct + "Override", new XAttribute("PartName", "/word/footnotes.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml")));
        foreach (var item in media.Select(static part => (Extension: Path.GetExtension(part.Name).TrimStart('.'), part.ContentType)).Distinct())
            root.Add(new XElement(ct + "Default", new XAttribute("Extension", item.Extension), new XAttribute("ContentType", item.ContentType)));
        return new XDocument(root);
    }

    private static XDocument PackageRelationships()
        => new(new XElement(Rel + "Relationships",
            new XElement(Rel + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"), new XAttribute("Target", "word/document.xml"))));

    private static XDocument DocumentRelationships(IEnumerable<MediaPart> media, bool hasFootnotes)
    {
        var root = new XElement(Rel + "Relationships",
            new XElement(Rel + "Relationship", new XAttribute("Id", "rIdStyles"), new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"), new XAttribute("Target", "styles.xml")),
            new XElement(Rel + "Relationship", new XAttribute("Id", "rIdNumbering"), new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering"), new XAttribute("Target", "numbering.xml")));
        if (hasFootnotes)
            root.Add(new XElement(Rel + "Relationship", new XAttribute("Id", "rIdFootnotes"), new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/footnotes"), new XAttribute("Target", "footnotes.xml")));
        foreach (var part in media)
            root.Add(new XElement(Rel + "Relationship", new XAttribute("Id", part.RelationshipId), new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image"), new XAttribute("Target", "media/" + part.Name)));
        return new XDocument(root);
    }

    private static XDocument Styles()
    {
        var root = new XElement(W + "styles", new XAttribute(XNamespace.Xmlns + "w", W));
        root.Add(new XElement(W + "style", new XAttribute(W + "type", "paragraph"), new XAttribute(W + "styleId", "Normal"), new XElement(W + "name", new XAttribute(W + "val", "Normal"))));
        for (var level = 1; level <= 6; level++)
            root.Add(new XElement(W + "style", new XAttribute(W + "type", "paragraph"), new XAttribute(W + "styleId", $"Heading{level}"), new XElement(W + "name", new XAttribute(W + "val", $"heading {level}")), new XElement(W + "basedOn", new XAttribute(W + "val", "Normal")), new XElement(W + "qFormat")));
        foreach (var style in new[] { "Quote", "Caption", "Equation", "Code" })
            root.Add(new XElement(W + "style", new XAttribute(W + "type", "paragraph"), new XAttribute(W + "styleId", style), new XElement(W + "name", new XAttribute(W + "val", style)), new XElement(W + "basedOn", new XAttribute(W + "val", "Normal"))));
        root.Add(new XElement(W + "style", new XAttribute(W + "type", "character"), new XAttribute(W + "styleId", "CodeChar"), new XElement(W + "name", new XAttribute(W + "val", "Code Character"))));
        return new XDocument(root);
    }

    private static XDocument Numbering()
    {
        XElement Abstract(int id, string format, string text) => new(W + "abstractNum", new XAttribute(W + "abstractNumId", id),
            new XElement(W + "lvl", new XAttribute(W + "ilvl", "0"), new XElement(W + "numFmt", new XAttribute(W + "val", format)), new XElement(W + "lvlText", new XAttribute(W + "val", text))));
        return new XDocument(new XElement(W + "numbering", new XAttribute(XNamespace.Xmlns + "w", W),
            Abstract(1, "bullet", "•"), Abstract(2, "decimal", "%1."),
            new XElement(W + "num", new XAttribute(W + "numId", "1"), new XElement(W + "abstractNumId", new XAttribute(W + "val", "1"))),
            new XElement(W + "num", new XAttribute(W + "numId", "2"), new XElement(W + "abstractNumId", new XAttribute(W + "val", "2")))));
    }

    private static XDocument Footnotes(IEnumerable<(int Id, IReadOnlyList<AstInline> Content)> footnotes, IReadOnlyDictionary<string, BibliographyEntry> bibliography)
    {
        var root = new XElement(W + "footnotes", new XAttribute(XNamespace.Xmlns + "w", W));
        root.Add(new XElement(W + "footnote", new XAttribute(W + "id", "-1"), new XAttribute(W + "type", "separator"), new XElement(W + "p", new XElement(W + "r", new XElement(W + "separator")))));
        root.Add(new XElement(W + "footnote", new XAttribute(W + "id", "0"), new XAttribute(W + "type", "continuationSeparator"), new XElement(W + "p", new XElement(W + "r", new XElement(W + "continuationSeparator")))));
        foreach (var footnote in footnotes)
        {
            var paragraph = new XElement(W + "p");
            var dummyDefinitions = new Dictionary<string, IReadOnlyList<AstInline>>(StringComparer.OrdinalIgnoreCase);
            var nested = new List<(int Id, IReadOnlyList<AstInline> Content)>();
            var id = int.MaxValue / 2;
            AppendRuns(paragraph, footnote.Content, bibliography, dummyDefinitions, nested, ref id, bold: false, italic: false);
            root.Add(new XElement(W + "footnote", new XAttribute(W + "id", footnote.Id), paragraph));
        }
        return new XDocument(root);
    }

    private static string FormatCitation(CitationInline citation, IReadOnlyDictionary<string, BibliographyEntry> bibliography)
    {
        if (!bibliography.TryGetValue(citation.Key, out var entry))
            return string.IsNullOrWhiteSpace(citation.Locator) ? $"[{citation.Key}]" : $"[{citation.Key}, {citation.Locator}]";
        var author = string.IsNullOrWhiteSpace(entry.Author) ? citation.Key : entry.Author.Split(" and ", 2, StringSplitOptions.TrimEntries)[0];
        var value = string.IsNullOrWhiteSpace(entry.Year) ? author : $"{author}, {entry.Year}";
        return string.IsNullOrWhiteSpace(citation.Locator) ? $"({value})" : $"({value}, {citation.Locator})";
    }

    private static void WriteXml(ZipArchive archive, string path, XDocument document)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        document.Save(stream, SaveOptions.DisableFormatting);
    }

    private static XDocument? LoadXml(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path);
        if (entry is null) return null;
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static Dictionary<string, string> LoadRelationships(ZipArchive archive)
    {
        var document = LoadXml(archive, "word/_rels/document.xml.rels");
        return document?.Root?.Elements(Rel + "Relationship")
            .Where(static element => (string?)element.Attribute("Id") is not null && (string?)element.Attribute("Target") is not null)
            .ToDictionary(element => (string)element.Attribute("Id")!, element => (string)element.Attribute("Target")!, StringComparer.Ordinal) ?? [];
    }

    private static Dictionary<int, string> LoadNumbering(ZipArchive archive)
    {
        var document = LoadXml(archive, "word/numbering.xml");
        if (document?.Root is null) return [];
        var abstractFormats = document.Root.Elements(W + "abstractNum").ToDictionary(
            element => int.TryParse((string?)element.Attribute(W + "abstractNumId"), out var id) ? id : -1,
            element => (string?)element.Descendants(W + "numFmt").FirstOrDefault()?.Attribute(W + "val") ?? "decimal");
        var result = new Dictionary<int, string>();
        foreach (var num in document.Root.Elements(W + "num"))
        {
            if (!int.TryParse((string?)num.Attribute(W + "numId"), out var id)) continue;
            if (!int.TryParse((string?)num.Element(W + "abstractNumId")?.Attribute(W + "val"), out var abstractId)) continue;
            result[id] = abstractFormats.TryGetValue(abstractId, out var format) ? format : "decimal";
        }
        return result;
    }

    private static Dictionary<int, string> LoadFootnotes(ZipArchive archive)
    {
        var document = LoadXml(archive, "word/footnotes.xml");
        if (document?.Root is null) return [];
        var result = new Dictionary<int, string>();
        foreach (var footnote in document.Root.Elements(W + "footnote"))
        {
            if (!int.TryParse((string?)footnote.Attribute(W + "id"), out var id) || id <= 0) continue;
            result[id] = string.Concat(footnote.Descendants(W + "t").Select(static text => text.Value)).Trim();
        }
        return result;
    }

    private static async Task<Dictionary<string, string>> ExtractMediaAsync(
        BookProject project,
        ZipArchive archive,
        IReadOnlyDictionary<string, string> relationships,
        CancellationToken cancellationToken)
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        var assetsRoot = Path.Combine(project.RootPath, AssetManagerService.AssetsDirectoryName);
        Directory.CreateDirectory(assetsRoot);
        foreach (var pair in relationships)
        {
            if (!pair.Value.StartsWith("media/", StringComparison.OrdinalIgnoreCase)) continue;
            var entry = archive.GetEntry("word/" + pair.Value.Replace('\\', '/'));
            if (entry is null) continue;
            var fileName = Path.GetFileName(pair.Value);
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            var destination = Path.Combine(assetsRoot, fileName);
            var number = 2;
            while (File.Exists(destination)) destination = Path.Combine(assetsRoot, $"{stem}-{number++}{extension}");
            await using var source = entry.Open();
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
            await source.CopyToAsync(target, cancellationToken);
            output[pair.Key] = AssetManagerService.NormalizeRelativePath(Path.GetRelativePath(project.RootPath, destination));
        }
        return output;
    }

    private static string ReadParagraphMarkup(XElement paragraph, ISet<int> usedFootnotes)
    {
        var output = new StringBuilder();
        foreach (var run in paragraph.Elements(W + "r"))
        {
            var footnote = run.Element(W + "footnoteReference");
            if (footnote is not null && int.TryParse((string?)footnote.Attribute(W + "id"), out var id))
            {
                usedFootnotes.Add(id);
                output.Append("[^fn").Append(id).Append(']');
                continue;
            }
            var text = string.Concat(run.Elements(W + "t").Select(static item => item.Value));
            if (text.Length == 0) continue;
            var properties = run.Element(W + "rPr");
            var bold = properties?.Element(W + "b") is not null;
            var italic = properties?.Element(W + "i") is not null;
            if (bold && italic) output.Append("***").Append(text).Append("***");
            else if (bold) output.Append("**").Append(text).Append("**");
            else if (italic) output.Append('*').Append(text).Append('*');
            else output.Append(text);
        }
        return output.ToString().TrimEnd();
    }

    private static void AppendImportedTable(StringBuilder output, XElement table)
    {
        var rows = table.Elements(W + "tr").Select(row => row.Elements(W + "tc")
            .Select(cell => string.Concat(cell.Descendants(W + "t").Select(static text => text.Value)).Replace("|", "\\|", StringComparison.Ordinal).Trim())
            .ToArray()).ToArray();
        if (rows.Length == 0) return;
        output.Append("| ").Append(string.Join(" | ", rows[0])).AppendLine(" |");
        output.Append("| ").Append(string.Join(" | ", rows[0].Select(static _ => "---"))).AppendLine(" |");
        foreach (var row in rows.Skip(1)) output.Append("| ").Append(string.Join(" | ", row)).AppendLine(" |");
    }

    private static string? ResolveProjectPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
        try
        {
            var projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var candidate = Path.GetFullPath(Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            var rel = Path.GetRelativePath(projectRoot, candidate);
            if (Path.IsPathRooted(rel) || rel.Equals("..", StringComparison.Ordinal) || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
            return candidate;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string ContentTypeForImage(string extension)
        => extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };

    private sealed record MediaPart(string SourcePath, string Name, string RelationshipId, string ContentType);
}
