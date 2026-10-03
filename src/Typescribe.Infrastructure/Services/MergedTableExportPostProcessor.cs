using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Typescribe.Application.Abstractions;
using Typescribe.Application.Services;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

/// <summary>
/// Applies semantic table merge geometry to package formats after their established exporters
/// have produced structurally valid output. This keeps the normal export path unchanged while
/// adding native rowspan/colspan and Word grid/vMerge semantics for merged cells.
/// </summary>
public static class MergedTableExportPostProcessor
{
    private static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static async Task ApplyEpubAsync(
        string path,
        IEnumerable<string> manuscriptSources,
        IDocumentParser parser,
        CancellationToken cancellationToken = default)
    {
        var tables = CollectTables(manuscriptSources, parser);
        if (!tables.Any(static table => TableMarkupCodec.ExtractSpans(table).Count > 0)) return;

        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var entryNames = archive.Entries
            .Where(static entry => entry.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase))
            .Select(static entry => entry.FullName)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        var tableIndex = 0;

        foreach (var entryName in entryNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.GetEntry(entryName);
            if (entry is null) continue;
            XDocument document;
            await using (var input = entry.Open())
                document = await XDocument.LoadAsync(input, LoadOptions.PreserveWhitespace, cancellationToken);

            var xhtml = document.Root?.Name.Namespace ?? XNamespace.Get("http://www.w3.org/1999/xhtml");
            var htmlTables = document.Descendants(xhtml + "table").ToArray();
            var changed = false;
            foreach (var htmlTable in htmlTables)
            {
                if (tableIndex >= tables.Count) break;
                var semantic = tables[tableIndex++];
                var spans = TableMarkupCodec.ExtractSpans(semantic);
                if (spans.Count == 0) continue;
                ApplyHtmlSpans(htmlTable, xhtml, spans);
                changed = true;
            }

            if (!changed) continue;
            var content = SerializeXml(document);
            entry.Delete();
            var replacement = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            await using var output = replacement.Open();
            await output.WriteAsync(content, cancellationToken);
        }
    }

    public static async Task ApplyDocxAsync(
        string path,
        IEnumerable<string> manuscriptSources,
        IDocumentParser parser,
        CancellationToken cancellationToken = default)
    {
        var tables = CollectTables(manuscriptSources, parser);
        if (!tables.Any(static table => TableMarkupCodec.ExtractSpans(table).Count > 0)) return;

        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = archive.GetEntry("word/document.xml");
        if (entry is null) return;
        XDocument document;
        await using (var input = entry.Open())
            document = await XDocument.LoadAsync(input, LoadOptions.PreserveWhitespace, cancellationToken);

        var wordTables = document.Descendants(Word + "tbl").ToArray();
        var count = Math.Min(wordTables.Length, tables.Count);
        var changed = false;
        for (var index = 0; index < count; index++)
        {
            var spans = TableMarkupCodec.ExtractSpans(tables[index]);
            if (spans.Count == 0) continue;
            ApplyWordSpans(wordTables[index], spans);
            changed = true;
        }
        if (!changed) return;

        var content = SerializeXml(document);
        entry.Delete();
        var replacement = archive.CreateEntry("word/document.xml", CompressionLevel.Optimal);
        await using var output = replacement.Open();
        await output.WriteAsync(content, cancellationToken);
    }

    private static List<TableBlock> CollectTables(IEnumerable<string> sources, IDocumentParser parser)
    {
        var result = new List<TableBlock>();
        foreach (var source in sources)
            result.AddRange(parser.Parse(source ?? string.Empty).Blocks.OfType<TableBlock>());
        return result;
    }

    private static void ApplyHtmlSpans(XElement table, XNamespace xhtml, IReadOnlyList<TableMergeSpan> spans)
    {
        var rows = table.Descendants(xhtml + "tr").ToArray();
        var map = rows.Select(row => row.Elements().Where(cell => cell.Name == xhtml + "th" || cell.Name == xhtml + "td").ToArray()).ToArray();
        foreach (var span in spans)
        {
            if (span.Row < 0 || span.Row >= map.Length || span.Column < 0 || span.Column >= map[span.Row].Length) continue;
            var anchor = map[span.Row][span.Column];
            if (span.RowSpan > 1) anchor.SetAttributeValue("rowspan", span.RowSpan);
            if (span.ColumnSpan > 1) anchor.SetAttributeValue("colspan", span.ColumnSpan);

            for (var row = span.Row; row < Math.Min(map.Length, span.Row + span.RowSpan); row++)
            {
                for (var column = span.Column; column < span.Column + span.ColumnSpan; column++)
                {
                    if (row == span.Row && column == span.Column) continue;
                    if (column >= 0 && column < map[row].Length) map[row][column].Remove();
                }
            }
        }
    }

    private static void ApplyWordSpans(XElement table, IReadOnlyList<TableMergeSpan> spans)
    {
        var rows = table.Elements(Word + "tr").ToArray();
        var map = rows.Select(row => row.Elements(Word + "tc").ToArray()).ToArray();
        foreach (var span in spans)
        {
            if (span.Row < 0 || span.Row >= map.Length || span.Column < 0 || span.Column >= map[span.Row].Length) continue;
            var anchor = map[span.Row][span.Column];
            var anchorProperties = EnsureCellProperties(anchor);
            if (span.ColumnSpan > 1)
                SetSingle(anchorProperties, Word + "gridSpan", new XAttribute(Word + "val", span.ColumnSpan));
            if (span.RowSpan > 1)
                SetSingle(anchorProperties, Word + "vMerge", new XAttribute(Word + "val", "restart"));

            for (var row = span.Row; row < Math.Min(map.Length, span.Row + span.RowSpan); row++)
            {
                if (row > span.Row && span.Column < map[row].Length)
                {
                    var continuation = map[row][span.Column];
                    var properties = EnsureCellProperties(continuation);
                    if (span.ColumnSpan > 1)
                        SetSingle(properties, Word + "gridSpan", new XAttribute(Word + "val", span.ColumnSpan));
                    SetSingle(properties, Word + "vMerge");
                    foreach (var content in continuation.Elements().Where(element => element.Name != Word + "tcPr").ToArray()) content.Remove();
                    continuation.Add(new XElement(Word + "p"));
                }

                var removeFrom = row == span.Row ? span.Column + 1 : span.Column + 1;
                for (var column = removeFrom; column < span.Column + span.ColumnSpan; column++)
                    if (column >= 0 && column < map[row].Length) map[row][column].Remove();
            }
        }
    }

    private static XElement EnsureCellProperties(XElement cell)
    {
        var properties = cell.Element(Word + "tcPr");
        if (properties is not null) return properties;
        properties = new XElement(Word + "tcPr");
        cell.AddFirst(properties);
        return properties;
    }

    private static void SetSingle(XElement parent, XName name, params object[] content)
    {
        parent.Elements(name).Remove();
        parent.Add(new XElement(name, content));
    }

    private static byte[] SerializeXml(XDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true))
            document.Save(writer, SaveOptions.DisableFormatting);
        return stream.ToArray();
    }
}
