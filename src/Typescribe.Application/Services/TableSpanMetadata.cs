using System.Globalization;
using System.Text;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Stores merged-cell geometry in the existing inert table metadata comment. The visible
/// pipe table remains rectangular and human-readable; span data is semantic metadata.
/// AdvancedDocumentParser intentionally ignores unknown metadata tokens, then this helper
/// restores the span model after parsing.
/// </summary>
public static class TableSpanMetadata
{
    private const string MetadataPrefix = "<!-- typescribe:table ";
    private const string SpanTokenPrefix = "spans64:";

    public static DocumentAst Apply(DocumentAst document, string source)
    {
        ArgumentNullException.ThrowIfNull(document);
        source ??= string.Empty;

        var spansByHeaderLine = Scan(source);
        if (spansByHeaderLine.Count == 0) return document;

        var changed = false;
        var blocks = new AstBlock[document.Blocks.Count];
        for (var index = 0; index < document.Blocks.Count; index++)
        {
            var block = document.Blocks[index];
            if (block is TableBlock table &&
                spansByHeaderLine.TryGetValue(table.SourceLine, out var spans))
            {
                var normalized = TableSpanLayout.Normalize(spans, TableSpanLayout.RowCount(table), TableSpanLayout.ColumnCount(table));
                blocks[index] = table with { Spans = normalized };
                changed = true;
            }
            else
            {
                blocks[index] = block;
            }
        }

        return changed ? new DocumentAst(blocks) : document;
    }

    public static string CreateTableMetadata(
        string? identifier,
        string? caption,
        IEnumerable<TableCellSpan>? spans)
    {
        var baseMetadata = AdvancedDocumentParser.CreateTableMetadata(identifier, caption);
        var normalized = spans?
            .Where(static span => span.RowSpan > 1 || span.ColumnSpan > 1)
            .OrderBy(static span => span.Row)
            .ThenBy(static span => span.Column)
            .ToArray() ?? [];
        if (normalized.Length == 0) return baseMetadata;

        var payload = string.Join(";", normalized.Select(static span => string.Create(
            CultureInfo.InvariantCulture,
            $"{span.Row},{span.Column},{span.RowSpan},{span.ColumnSpan}")));
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        return baseMetadata.Replace(" -->", $" {SpanTokenPrefix}{encoded} -->", StringComparison.Ordinal);
    }

    public static IReadOnlyList<TableCellSpan> ParseMetadataLine(string line)
    {
        var trimmed = (line ?? string.Empty).Trim();
        if (!trimmed.StartsWith(MetadataPrefix, StringComparison.Ordinal) ||
            !trimmed.EndsWith("-->", StringComparison.Ordinal))
            return [];

        var body = trimmed[MetadataPrefix.Length..^3].Trim();
        var token = body.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static part => part.StartsWith(SpanTokenPrefix, StringComparison.Ordinal));
        if (token is null) return [];

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token[SpanTokenPrefix.Length..]));
            var spans = new List<TableCellSpan>();
            foreach (var item in decoded.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var pieces = item.Split(',', StringSplitOptions.TrimEntries);
                if (pieces.Length != 4 ||
                    !int.TryParse(pieces[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var row) ||
                    !int.TryParse(pieces[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var column) ||
                    !int.TryParse(pieces[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowSpan) ||
                    !int.TryParse(pieces[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var columnSpan))
                    continue;
                if (row < 0 || column < 0 || rowSpan < 1 || columnSpan < 1) continue;
                if (rowSpan == 1 && columnSpan == 1) continue;
                spans.Add(new TableCellSpan(row, column, rowSpan, columnSpan));
            }
            return spans;
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private static Dictionary<int, IReadOnlyList<TableCellSpan>> Scan(string source)
    {
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var result = new Dictionary<int, IReadOnlyList<TableCellSpan>>();
        for (var index = 0; index + 1 < lines.Length; index++)
        {
            var spans = ParseMetadataLine(lines[index]);
            if (spans.Count == 0) continue;
            // AdvancedDocumentParser reports the pipe-header line as TableBlock.SourceLine.
            result[index + 2] = spans;
        }
        return result;
    }
}
