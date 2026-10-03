using System.Globalization;
using System.Text;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Canonical table text codec. Pipe rows remain normal Markdown; Typescribe-only merge
/// geometry is stored in the existing table metadata comment as a compact base64 token.
/// This keeps manuscripts readable in ordinary editors while preserving merged cells.
/// </summary>
public static class TableMarkupCodec
{
    public const string MetadataPrefix = "<!-- typescribe:table ";

    public static string CreateMetadata(
        string? identifier,
        string? caption,
        IEnumerable<TableMergeSpan>? spans = null)
    {
        var baseMetadata = AdvancedDocumentParser.CreateTableMetadata(identifier, caption);
        var merged = spans?.Where(static span => span.IsMerged).ToArray() ?? [];
        if (merged.Length == 0) return baseMetadata;

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(EncodeSpans(merged)));
        return baseMetadata.Replace(" -->", $" spans64:{encoded} -->", StringComparison.Ordinal);
    }

    public static string Serialize(TableBlock table, string? newline = null)
    {
        newline ??= Environment.NewLine;
        var rows = GridRows(table);
        var columns = Math.Max(1, rows.Count == 0 ? table.Header.Count : rows.Max(static row => row.Count));
        var spans = ExtractSpans(table);

        string Row(IReadOnlyList<TableCell> cells)
        {
            var values = new string[columns];
            for (var column = 0; column < columns; column++)
            {
                var cell = column < cells.Count ? cells[column] : EmptyCell();
                values[column] = cell.IsSpanContinuation ? string.Empty : EscapeCell(cell.Inlines.ToPlainText());
            }
            return "| " + string.Join(" | ", values) + " |";
        }

        var alignments = table.Alignments ?? [];
        var separator = Enumerable.Range(0, columns)
            .Select(index => index < alignments.Count ? alignments[index] switch
            {
                TableAlignment.Left => ":---",
                TableAlignment.Center => ":---:",
                TableAlignment.Right => "---:",
                _ => "---"
            } : "---")
            .ToArray();

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(table.Caption) ||
            !string.IsNullOrWhiteSpace(table.Identifier) ||
            spans.Count > 0)
        {
            lines.Add(CreateMetadata(table.Identifier, table.Caption, spans));
        }

        lines.Add(Row(table.Header));
        lines.Add("| " + string.Join(" | ", separator) + " |");
        lines.AddRange(table.Rows.Select(Row));
        return string.Join(newline, lines);
    }

    public static IReadOnlyList<TableMergeSpan> ExtractSpans(TableBlock table)
    {
        var result = new List<TableMergeSpan>();
        var rows = GridRows(table);
        for (var row = 0; row < rows.Count; row++)
        {
            for (var column = 0; column < rows[row].Count; column++)
            {
                var cell = rows[row][column];
                if (cell.IsSpanContinuation || (cell.RowSpan <= 1 && cell.ColumnSpan <= 1)) continue;
                result.Add(new TableMergeSpan(row, column, Math.Max(1, cell.RowSpan), Math.Max(1, cell.ColumnSpan)));
            }
        }
        return result;
    }

    public static TableBlock ApplySpans(TableBlock table, IEnumerable<TableMergeSpan> spans)
    {
        var allRows = GridRows(table).Select(static row => row.ToList()).ToList();
        if (allRows.Count == 0) return table;

        var columns = Math.Max(1, allRows.Max(static row => row.Count));
        foreach (var row in allRows)
            while (row.Count < columns) row.Add(EmptyCell());

        foreach (var row in allRows)
        {
            for (var column = 0; column < row.Count; column++)
            {
                var cell = row[column];
                if (cell.RowSpan != 1 || cell.ColumnSpan != 1 || cell.IsSpanContinuation)
                    row[column] = cell with { RowSpan = 1, ColumnSpan = 1, IsSpanContinuation = false };
            }
        }

        foreach (var requested in spans.Where(static span => span.IsMerged)
                     .OrderBy(static span => span.Row)
                     .ThenBy(static span => span.Column))
        {
            if (requested.Row < 0 || requested.Column < 0 ||
                requested.Row >= allRows.Count || requested.Column >= columns)
                continue;

            var rowSpan = Math.Clamp(requested.RowSpan, 1, allRows.Count - requested.Row);
            var columnSpan = Math.Clamp(requested.ColumnSpan, 1, columns - requested.Column);
            if (rowSpan == 1 && columnSpan == 1) continue;

            var overlaps = false;
            for (var row = requested.Row; row < requested.Row + rowSpan && !overlaps; row++)
            {
                for (var column = requested.Column; column < requested.Column + columnSpan; column++)
                {
                    if (row == requested.Row && column == requested.Column) continue;
                    if (allRows[row][column].IsSpanContinuation ||
                        allRows[row][column].RowSpan > 1 ||
                        allRows[row][column].ColumnSpan > 1)
                    {
                        overlaps = true;
                        break;
                    }
                }
            }
            if (overlaps) continue;

            allRows[requested.Row][requested.Column] = allRows[requested.Row][requested.Column] with
            {
                RowSpan = rowSpan,
                ColumnSpan = columnSpan,
                IsSpanContinuation = false
            };

            for (var row = requested.Row; row < requested.Row + rowSpan; row++)
            {
                for (var column = requested.Column; column < requested.Column + columnSpan; column++)
                {
                    if (row == requested.Row && column == requested.Column) continue;
                    allRows[row][column] = EmptyCell() with { IsSpanContinuation = true };
                }
            }
        }

        var header = allRows[0].ToArray();
        var body = allRows.Skip(1).Select(static row => (IReadOnlyList<TableCell>)row.ToArray()).ToArray();
        return table with { Header = header, Rows = body };
    }

    public static bool TryReadSpans(string metadataLine, out IReadOnlyList<TableMergeSpan> spans)
    {
        spans = [];
        var trimmed = metadataLine.Trim();
        if (!trimmed.StartsWith(MetadataPrefix, StringComparison.Ordinal) || !trimmed.EndsWith("-->", StringComparison.Ordinal))
            return false;

        var body = trimmed[MetadataPrefix.Length..^3].Trim();
        var token = body.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static value => value.StartsWith("spans64:", StringComparison.Ordinal));
        if (token is null) return true;

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token[8..]));
            spans = DecodeSpans(decoded);
            return true;
        }
        catch (FormatException)
        {
            spans = [];
            return false;
        }
    }

    private static IReadOnlyList<IReadOnlyList<TableCell>> GridRows(TableBlock table)
    {
        var rows = new List<IReadOnlyList<TableCell>>(table.Rows.Count + 1) { table.Header };
        rows.AddRange(table.Rows);
        return rows;
    }

    private static string EncodeSpans(IEnumerable<TableMergeSpan> spans)
        => string.Join(';', spans.Select(static span => string.Join(',',
            span.Row.ToString(CultureInfo.InvariantCulture),
            span.Column.ToString(CultureInfo.InvariantCulture),
            span.RowSpan.ToString(CultureInfo.InvariantCulture),
            span.ColumnSpan.ToString(CultureInfo.InvariantCulture))));

    private static IReadOnlyList<TableMergeSpan> DecodeSpans(string value)
    {
        var result = new List<TableMergeSpan>();
        foreach (var item in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 4) continue;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var row) ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var column) ||
                !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowSpan) ||
                !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var columnSpan))
                continue;
            if (row < 0 || column < 0 || rowSpan < 1 || columnSpan < 1) continue;
            result.Add(new TableMergeSpan(row, column, rowSpan, columnSpan));
        }
        return result;
    }

    private static TableCell EmptyCell() => new([new TextInline(string.Empty)]);

    private static string EscapeCell(string value)
        => (value ?? string.Empty)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
}
