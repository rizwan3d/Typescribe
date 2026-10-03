using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

/// <summary>
/// Semantic DOCX interchange for editorial round-tripping. The implementation deliberately
/// preserves manuscript structure rather than arbitrary Word page layout.
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
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var bibliographyEntries = (await bibliography.LoadAsync(project.RootPath, cancellationToken))
            .ToDictionary(static entry => entry.CitationKey, StringComparer.OrdinalIgnoreCase);
        var body = new XElement(W + "body");
        var media = new List<MediaPart>();
        var footnotes = new List<FootnotePart>();
        var nextFootnoteId = 1;
        var nextImageId = 1;

        for (var documentIndex = 0; documentIndex < documents.Count; documentIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ast = parser.Parse(documents[documentIndex].Content);
            var definitions = ast.Blocks.OfType<FootnoteDefinitionBlock>()
                .GroupBy(static note => note.Identifier, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.Last().Inlines, StringComparer.OrdinalIgnoreCase);

            foreach (var block in ast.Blocks)
            {
                switch (block)
                {
                    case HeadingBlock heading:
                        body.Add(CreateParagraph(heading.Inlines, $"Heading{Math.Clamp(heading.Level, 1, 6)}", bibliographyEntries, definitions, footnotes, ref nextFootnoteId));
                        break;
                    case ParagraphBlock paragraph:
                        body.Add(CreateParagraph(paragraph.Inlines, null, bibliographyEntries, definitions, footnotes, ref nextFootnoteId));
                        break;
                    case QuoteBlock quote:
                        body.Add(CreateParagraph(quote.Inlines, "Quote", bibliographyEntries, definitions, footnotes, ref nextFootnoteId));
                        break;
                    case ListItemBlock item:
                        body.Add(CreateListParagraph(item, bibliographyEntries, definitions, footnotes, ref nextFootnoteId));
                        break;
                    case CodeBlock code:
                        body.Add(CreateTextParagraph(code.Text, "Code"));
                        break;
                    case DisplayMathBlock equation:
                        body.Add(CreateTextParagraph(equation.Text, "Equation"));
                        break;
                    case ThematicBreakBlock:
                        body.Add(CreateTextParagraph("***", null));
                        break;
                    case TableBlock table:
                        body.Add(CreateTable(table, bibliographyEntries, definitions, footnotes, ref nextFootnoteId));
                        if (!string.IsNullOrWhiteSpace(table.Caption))
                            body.Add(CreateTextParagraph(table.Caption!, "Caption"));
                        break;
                    case FigureBlock figure:
                    {
                        var imagePath = ResolveProjectPath(project.RootPath, figure.Source);
                        if (imagePath is not null && File.Exists(imagePath))
                        {
                            var extension = Path.GetExtension(imagePath).ToLowerInvariant();
                            var mediaName = $"image{nextImageId}{extension}";
                            var relationshipId = $"rIdImage{nextImageId}";
                            media.Add(new MediaPart(imagePath, mediaName, relationshipId, ContentTypeForImage(extension)));
                            body.Add(CreateImageParagraph(relationshipId, nextImageId, figure.Caption));
                            nextImageId++;
                        }
                        else
                        {
                            body.Add(CreateTextParagraph($"[Missing image: {figure.Source}]", null));
                        }
                        if (!string.IsNullOrWhiteSpace(figure.Caption))
                            body.Add(CreateTextParagraph(figure.Caption, "Caption"));
                        break;
                    }
                    case FootnoteDefinitionBlock:
                    case BibliographyEntryBlock:
                        break;
                }
            }

            if (documentIndex < documents.Count - 1)
                body.Add(CreatePageBreakParagraph());
        }

        body.Add(new XElement(W + "sectPr"));
        var documentXml = new XDocument(
            new XElement(W + "document",
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
                WriteXml(archive, "[Content_Types].xml", CreateContentTypes(media, footnotes.Count > 0));
                WriteXml(archive, "_rels/.rels", CreatePackageRelationships());
                WriteXml(archive, "word/document.xml", documentXml);
                WriteXml(archive, "word/styles.xml", CreateStyles());
                WriteXml(archive, "word/numbering.xml", CreateNumbering());
                WriteXml(archive, "word/_rels/document.xml.rels", CreateDocumentRelationships(media, footnotes.Count > 0));
                if (footnotes.Count > 0)
                    WriteXml(archive, "word/footnotes.xml", CreateFootnotes(footnotes, bibliographyEntries));

                foreach (var part in media)
                {
                    cancellationToken.ThrowIfCancellationRequested();
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
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        await using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var document = LoadXml(archive, "word/document.xml")
            ?? throw new InvalidOperationException("DOCX does not contain word/document.xml.");
        var relationships = LoadRelationships(archive);
        var numbering = LoadNumbering(archive);
        var footnoteTexts = LoadFootnotes(archive);
        var extractedMedia = await ExtractMediaAsync(project, archive, relationships, cancellationToken);
        var usedFootnotes = new HashSet<int>();
        var output = new StringBuilder();

        var body = document.Root?.Element(W + "body");
        if (body is null) return string.Empty;

        foreach (var element in body.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element.Name == W + "p")
            {
                var imageIds = element.Descendants(A + "blip")
                    .Select(blip => (string?)blip.Attribute(R + "embed"))
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .ToArray();
                foreach (var relationshipId in imageIds)
                {
                    if (extractedMedia.TryGetValue(relationshipId, out var relativePath))
                        output.Append("![Image](").Append(relativePath).AppendLine(")");
                }

                var style = (string?)element.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val");
                var numIdText = (string?)element.Element(W + "pPr")?.Element(W + "numPr")?.Element(W + "numId")?.Attribute(W + "val");
                var prefix = string.Empty;
                if (style?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true &&
                    int.TryParse(style[7..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var level))
                {
                    prefix = new string('#', Math.Clamp(level, 1, 6)) + " ";
                }
                else if (int.TryParse(numIdText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var numId))
                {
                    prefix = numbering.TryGetValue(numId, out var format) && string.Equals(format, "bullet", StringComparison.OrdinalIgnoreCase)
                        ? "- "
                        : "1. ";
                }

                var text = ReadParagraphMarkup(element, usedFootnotes);
                if (imageIds.Length == 0 || text.Length > 0)
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

    private static XElement CreateParagraph(
        IReadOnlyList<AstInline> inlines,
        string? style,
        IReadOnlyDictionary<string, BibliographyEntry> bibliographyEntries,
        IReadOnlyDictionary<string, IReadOnlyList<AstInline>> definitions,
        List<FootnotePart> footnotes,
        ref int nextFootnoteId)
    {
        var paragraph = new XElement(W + "p");
        if (!string.IsNullOrWhiteSpace(style))
            paragraph.Add(new XElement(W + "pPr", new XElement(W + "pStyle", new XAttribute(W + "val", style))));
        AppendRuns(paragraph, inlines, bibliographyEntries, definitions, footnotes, ref nextFootnoteId, bold: false, italic: false);
        return paragraph;
    }

    private static XElement CreateTextParagraph(string text, string? style)
    {
        var paragraph = new XElement(W + "p");
        if (!string.IsNullOrWhiteSpace(style))
            paragraph.Add(new XElement(W + "pPr", new XElement(W + "pStyle", new XAttribute(W + "val", style))));
        paragraph.Add(CreateRun(text, bold: false, italic: false));
        return paragraph;
    }

    private static XElement CreateListParagraph(
        ListItemBlock item,
        IReadOnlyDictionary<string, BibliographyEntry> bibliographyEntries,
        IReadOnlyDictionary<string, IReadOnlyList<AstInline>> definitions,
        List<FootnotePart> footnotes,
        ref int nextFootnoteId)
    {
        var properties = new XElement(W + "pPr",
            new XElement(W + "numPr",
                new XElement(W + "ilvl", new XAttribute(W + "val", "0")),
                new XElement(W + "numId", new XAttribute(W + "val", item.Ordered ? "2" : "1"))));
        var paragraph = new XElement(W + "p", properties);
        AppendRuns(paragraph, item.Inlines, bibliographyEntries, definitions, footnotes, ref nextFootnoteId, bold: false, italic: false);
        return paragraph;
    }

    private static XElement CreateTable(
        TableBlock table,
        IReadOnlyDictionary<string, BibliographyEntry> bibliographyEntries,
        IReadOnlyDictionary<string, IReadOnlyList<AstInline>> definitions,
        List<FootnotePart> footnotes,
        ref int nextFootnoteId)
    {
        var result = new XElement(W + "tbl",
            new XElement(W + "tblPr",
                new XElement(W + "tblStyle", new XAttribute(W + "val", "TableGrid"))));
        result.Add(CreateTableRow(table.Header, header: true, bibliographyEntries, definitions, footnotes, ref nextFootnoteId));
        foreach (var row in table.Rows)
            result.Add(CreateTableRow(row, header: false, bibliographyEntries, definitions, footnotes, ref nextFootnoteId));
        return result;
    }

    private static XElement CreateTableRow(
        IReadOnlyList<TableCell> cells,
        bool header,
        IReadOnlyDictionary<string, BibliographyEntry> bibliographyEntries,
        IReadOnlyDictionary<string, IReadOnlyList<AstInline>> definitions,
        List<FootnotePart> footnotes,
        ref int nextFootnoteId)
    {
        var row = new XElement(W + "tr");
        foreach (var cell in cells)
        {
            var paragraph = new XElement(W + "p");
            AppendRuns(paragraph, cell.Inlines, bibliographyEntries, definitions, footnotes, ref nextFootnoteId, bold: header, italic: false);
            row.Add(new XElement(W + "tc", paragraph));
        }
        return row;
    }

    private static void AppendRuns(
        XElement parent,
        IEnumerable<AstInline> inlines,
        IReadOnlyDictionary<string, BibliographyEntry> bibliographyEntries,
        IReadOnlyDictionary<string, IReadOnlyList<AstInline>> definitions,
        List<FootnotePart> footnotes,
        ref int nextFootnoteId,
        bool bold,
        bool italic)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextInline text:
                    parent.Add(CreateRun(text.Text, bold, italic));
                    break;
                case StrongInline strong:
                    AppendRuns(parent, strong.Children, bibliographyEntries, definitions, footnotes, ref nextFootnoteId, bold: true, italic);
                    break;
                case EmphasisInline emphasis:
                    AppendRuns(parent, emphasis.Children, bibliographyEntries, definitions, footnotes, ref nextFootnoteId, bold, italic: true);
                    break;
                case CodeInline code:
                    parent.Add(CreateRun(code.Text, bold, italic, "CodeChar"));
                    break;
                case MathInline math:
                    parent.Add(CreateRun("$" + math.Text + "$", bold, italic));
                    break;
                case LinkInline link:
                    AppendRuns(parent, link.Label, bibliographyEntries, definitions, footnotes, ref nextFootnoteId, bold, italic);
                    parent.Add(CreateRun(" (" + link.Url + ")", bold, italic));
                    break;
                case CitationInline citation:
                    parent.Add(CreateRun(FormatCitation(citation, bibliographyEntries), bold, italic));
                    break;
                case CrossReferenceInline reference:
                    parent.Add(CreateRun("[@ref:" + reference.Identifier + "]", bold, italic));
                    break;
                case FootnoteReferenceInline footnote:
                    if (definitions.TryGetValue(footnote.Identifier, out var definition))
                    {
                        var id = nextFootnoteId++;
                        footnotes.Add(new FootnotePart(id, definition));
                        parent.Add(new XElement(W + "r",
                            new XElement(W + "rPr", new XElement(W + "vertAlign", new XAttribute(W + "val", "superscript"))),
                            new XElement(W + "footnoteReference", new XAttribute(W + "id", id))));
                    }
                    else
                    {
                        parent.Add(CreateRun("[^" + footnote.Identifier + "]", bold, italic));
                    }
                    break;
            }
        }
    }

    private static XElement CreateRun(string text, bool bold, bool italic, string? style = null)
    {
        var properties = new XElement(W + "rPr");
        if (bold) properties.Add(new XElement(W + "b"));
        if (italic) properties.Add(new XElement(W + "i"));
        if (!string.IsNullOrWhiteSpace(style))
            properties.Add(new XElement(W + "rStyle", new XAttribute(W + "val", style)));

        return new XElement(W + "r",
            properties.HasElements ? properties : null,
            new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text));
    }

    private static XElement CreateImageParagraph(string relationshipId, int imageId, string caption)
    {
        var xfrm = new XElement(A + "xfrm",
            new XElement(A + "off", new XAttribute("x", "0"), new XAttribute("y", "0")),
            new XElement(A + "ext", new XAttribute("cx", "5486400"), new XAttribute("cy", "3657600")));
        var shapeProperties = new XElement(Pic + "spPr",
            xfrm,
            new XElement(A + "prstGeom",
                new XAttribute("prst", "rect"),
                new XElement(A + "avLst")));
        var picture = new XElement(Pic + "pic",
            new XElement(Pic + "nvPicPr",
                new XElement(Pic + "cNvPr",
                    new XAttribute("id", imageId),
                    new XAttribute("name", string.IsNullOrWhiteSpace(caption) ? $"Image {imageId}" : caption)),
                new XElement(Pic + "cNvPicPr")),
            new XElement(Pic + "blipFill",
                new XElement(A + "blip", new XAttribute(R + "embed", relationshipId)),
                new XElement(A + "stretch", new XElement(A + "fillRect"))),
            shapeProperties);
        var graphic = new XElement(A + "graphic",
            new XElement(A + "graphicData",
                new XAttribute("uri", "http://schemas.openxmlformats.org/drawingml/2006/picture"),
                picture));
        var inline = new XElement(Wp + "inline",
            new XElement(Wp + "extent", new XAttribute("cx", "5486400"), new XAttribute("cy", "3657600")),
            new XElement(Wp + "docPr",
                new XAttribute("id", imageId),
                new XAttribute("name", string.IsNullOrWhiteSpace(caption) ? $"Figure {imageId}" : caption)),
            graphic);
        return new XElement(W + "p",
            new XElement(W + "r",
                new XElement(W + "drawing", inline)));
    }

    private static XElement CreatePageBreakParagraph()
        => new(W + "p",
            new XElement(W + "r",
                new XElement(W + "br", new XAttribute(W + "type", "page"))));

    private static XDocument CreateContentTypes(IEnumerable<MediaPart> media, bool hasFootnotes)
    {
        XNamespace content = "http://schemas.openxmlformats.org/package/2006/content-types";
        var root = new XElement(content + "Types",
            new XElement(content + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(content + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
            new XElement(content + "Override", new XAttribute("PartName", "/word/document.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml")),
            new XElement(content + "Override", new XAttribute("PartName", "/word/styles.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml")),
            new XElement(content + "Override", new XAttribute("PartName", "/word/numbering.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml")));
        if (hasFootnotes)
        {
            root.Add(new XElement(content + "Override",
                new XAttribute("PartName", "/word/footnotes.xml"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml")));
        }
        foreach (var part in media.GroupBy(static item => Path.GetExtension(item.Name).TrimStart('.'), StringComparer.OrdinalIgnoreCase).Select(static group => group.First()))
        {
            root.Add(new XElement(content + "Default",
                new XAttribute("Extension", Path.GetExtension(part.Name).TrimStart('.')),
                new XAttribute("ContentType", part.ContentType)));
        }
        return new XDocument(root);
    }

    private static XDocument CreatePackageRelationships()
        => new(new XElement(Rel + "Relationships",
            new XElement(Rel + "Relationship",
                new XAttribute("Id", "rId1"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                new XAttribute("Target", "word/document.xml"))));

    private static XDocument CreateDocumentRelationships(IEnumerable<MediaPart> media, bool hasFootnotes)
    {
        var root = new XElement(Rel + "Relationships",
            new XElement(Rel + "Relationship", new XAttribute("Id", "rIdStyles"), new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"), new XAttribute("Target", "styles.xml")),
            new XElement(Rel + "Relationship", new XAttribute("Id", "rIdNumbering"), new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering"), new XAttribute("Target", "numbering.xml")));
        if (hasFootnotes)
        {
            root.Add(new XElement(Rel + "Relationship",
                new XAttribute("Id", "rIdFootnotes"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/footnotes"),
                new XAttribute("Target", "footnotes.xml")));
        }
        foreach (var part in media)
        {
            root.Add(new XElement(Rel + "Relationship",
                new XAttribute("Id", part.RelationshipId),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image"),
                new XAttribute("Target", "media/" + part.Name)));
        }
        return new XDocument(root);
    }

    private static XDocument CreateStyles()
    {
        var root = new XElement(W + "styles", new XAttribute(XNamespace.Xmlns + "w", W));
        root.Add(new XElement(W + "style",
            new XAttribute(W + "type", "paragraph"),
            new XAttribute(W + "styleId", "Normal"),
            new XElement(W + "name", new XAttribute(W + "val", "Normal"))));
        for (var level = 1; level <= 6; level++)
        {
            root.Add(new XElement(W + "style",
                new XAttribute(W + "type", "paragraph"),
                new XAttribute(W + "styleId", $"Heading{level}"),
                new XElement(W + "name", new XAttribute(W + "val", $"heading {level}")),
                new XElement(W + "basedOn", new XAttribute(W + "val", "Normal")),
                new XElement(W + "qFormat")));
        }
        foreach (var style in new[] { "Quote", "Caption", "Equation", "Code" })
        {
            root.Add(new XElement(W + "style",
                new XAttribute(W + "type", "paragraph"),
                new XAttribute(W + "styleId", style),
                new XElement(W + "name", new XAttribute(W + "val", style)),
                new XElement(W + "basedOn", new XAttribute(W + "val", "Normal"))));
        }
        root.Add(new XElement(W + "style",
            new XAttribute(W + "type", "character"),
            new XAttribute(W + "styleId", "CodeChar"),
            new XElement(W + "name", new XAttribute(W + "val", "Code Character"))));
        return new XDocument(root);
    }

    private static XDocument CreateNumbering()
    {
        static XElement Abstract(int id, string format, string text)
            => new(W + "abstractNum",
                new XAttribute(W + "abstractNumId", id),
                new XElement(W + "lvl",
                    new XAttribute(W + "ilvl", "0"),
                    new XElement(W + "numFmt", new XAttribute(W + "val", format)),
                    new XElement(W + "lvlText", new XAttribute(W + "val", text))));

        return new XDocument(new XElement(W + "numbering",
            new XAttribute(XNamespace.Xmlns + "w", W),
            Abstract(1, "bullet", "•"),
            Abstract(2, "decimal", "%1."),
            new XElement(W + "num", new XAttribute(W + "numId", "1"), new XElement(W + "abstractNumId", new XAttribute(W + "val", "1"))),
            new XElement(W + "num", new XAttribute(W + "numId", "2"), new XElement(W + "abstractNumId", new XAttribute(W + "val", "2")))));
    }

    private static XDocument CreateFootnotes(
        IEnumerable<FootnotePart> footnotes,
        IReadOnlyDictionary<string, BibliographyEntry> bibliographyEntries)
    {
        var root = new XElement(W + "footnotes", new XAttribute(XNamespace.Xmlns + "w", W));
        root.Add(new XElement(W + "footnote",
            new XAttribute(W + "id", "-1"),
            new XAttribute(W + "type", "separator"),
            new XElement(W + "p", new XElement(W + "r", new XElement(W + "separator")))));
        root.Add(new XElement(W + "footnote",
            new XAttribute(W + "id", "0"),
            new XAttribute(W + "type", "continuationSeparator"),
            new XElement(W + "p", new XElement(W + "r", new XElement(W + "continuationSeparator")))));

        foreach (var footnote in footnotes)
        {
            var paragraph = new XElement(W + "p");
            var definitions = new Dictionary<string, IReadOnlyList<AstInline>>(StringComparer.OrdinalIgnoreCase);
            var nested = new List<FootnotePart>();
            var unusedId = int.MaxValue / 2;
            AppendRuns(paragraph, footnote.Content, bibliographyEntries, definitions, nested, ref unusedId, bold: false, italic: false);
            root.Add(new XElement(W + "footnote", new XAttribute(W + "id", footnote.Id), paragraph));
        }
        return new XDocument(root);
    }

    private static string FormatCitation(CitationInline citation, IReadOnlyDictionary<string, BibliographyEntry> entries)
    {
        if (!entries.TryGetValue(citation.Key, out var entry))
            return string.IsNullOrWhiteSpace(citation.Locator) ? $"[{citation.Key}]" : $"[{citation.Key}, {citation.Locator}]";
        var author = string.IsNullOrWhiteSpace(entry.Author)
            ? citation.Key
            : entry.Author.Split(" and ", 2, StringSplitOptions.TrimEntries)[0];
        var core = string.IsNullOrWhiteSpace(entry.Year) ? author : $"{author}, {entry.Year}";
        return string.IsNullOrWhiteSpace(citation.Locator) ? $"({core})" : $"({core}, {citation.Locator})";
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
        if (document?.Root is null) return [];
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relationship in document.Root.Elements(Rel + "Relationship"))
        {
            var id = (string?)relationship.Attribute("Id");
            var target = (string?)relationship.Attribute("Target");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(target)) result[id] = target;
        }
        return result;
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
            if (!int.TryParse((string?)numbering.Attribute(W + "numId"), out var id)) continue;
            if (!int.TryParse((string?)numbering.Element(W + "abstractNumId")?.Attribute(W + "val"), out var abstractId)) continue;
            result[id] = formats.TryGetValue(abstractId, out var format) ? format : "decimal";
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
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var assetsRoot = Path.Combine(project.RootPath, AssetManagerService.AssetsDirectoryName);
        Directory.CreateDirectory(assetsRoot);

        foreach (var pair in relationships)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!pair.Value.StartsWith("media/", StringComparison.OrdinalIgnoreCase)) continue;
            var entry = archive.GetEntry("word/" + pair.Value.Replace('\\', '/'));
            if (entry is null) continue;

            var fileName = Path.GetFileName(pair.Value);
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            var destination = Path.Combine(assetsRoot, fileName);
            var number = 2;
            while (File.Exists(destination))
                destination = Path.Combine(assetsRoot, $"{stem}-{number++}{extension}");

            await using var source = entry.Open();
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
            await source.CopyToAsync(target, cancellationToken);
            result[pair.Key] = AssetManagerService.NormalizeRelativePath(Path.GetRelativePath(project.RootPath, destination));
        }
        return result;
    }

    private static string ReadParagraphMarkup(XElement paragraph, ISet<int> usedFootnotes)
    {
        var result = new StringBuilder();
        foreach (var run in paragraph.Elements(W + "r"))
        {
            var reference = run.Element(W + "footnoteReference");
            if (reference is not null && int.TryParse((string?)reference.Attribute(W + "id"), out var id))
            {
                usedFootnotes.Add(id);
                result.Append("[^fn").Append(id).Append(']');
                continue;
            }

            var text = string.Concat(run.Elements(W + "t").Select(static item => item.Value));
            if (text.Length == 0) continue;
            var properties = run.Element(W + "rPr");
            var bold = properties?.Element(W + "b") is not null;
            var italic = properties?.Element(W + "i") is not null;
            if (bold && italic) result.Append("***").Append(text).Append("***");
            else if (bold) result.Append("**").Append(text).Append("**");
            else if (italic) result.Append('*').Append(text).Append('*');
            else result.Append(text);
        }
        return result.ToString().TrimEnd();
    }

    private static void AppendImportedTable(StringBuilder output, XElement table)
    {
        var rows = table.Elements(W + "tr")
            .Select(row => row.Elements(W + "tc")
                .Select(cell => string.Concat(cell.Descendants(W + "t").Select(static text => text.Value))
                    .Replace("|", "\\|", StringComparison.Ordinal)
                    .Trim())
                .ToArray())
            .ToArray();
        if (rows.Length == 0) return;
        output.Append("| ").Append(string.Join(" | ", rows[0])).AppendLine(" |");
        output.Append("| ").Append(string.Join(" | ", rows[0].Select(static _ => "---"))).AppendLine(" |");
        foreach (var row in rows.Skip(1))
            output.Append("| ").Append(string.Join(" | ", row)).AppendLine(" |");
    }

    private static string? ResolveProjectPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
        try
        {
            var projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var candidate = Path.GetFullPath(Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            var relativeToRoot = Path.GetRelativePath(projectRoot, candidate);
            if (Path.IsPathRooted(relativeToRoot) ||
                relativeToRoot.Equals("..", StringComparison.Ordinal) ||
                relativeToRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return null;
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
    private sealed record FootnotePart(int Id, IReadOnlyList<AstInline> Content);
}
