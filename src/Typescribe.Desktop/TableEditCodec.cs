using Typescribe.Application.Services;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

internal static class TableEditCodec
{
    public static TableEditResult FromTable(TableBlock table)
    {
        var rows = new List<IReadOnlyList<string>>
        {
            table.Header.Select(static cell => cell.Inlines.ToPlainText()).ToArray()
        };
        rows.AddRange(table.Rows.Select(static row =>
            (IReadOnlyList<string>)row.Select(static cell => cell.Inlines.ToPlainText()).ToArray()));
        return new TableEditResult(
            rows,
            HeaderRow: true,
            table.Alignments ?? [],
            table.Caption ?? string.Empty,
            table.Identifier ?? string.Empty,
            TableMarkupCodec.ExtractSpans(table));
    }

    public static TableBlock ToTable(TableEditResult edit)
    {
        var rows = NormalizeCells(edit.Cells);
        var columns = Math.Max(1, rows[0].Count);
        var header = edit.HeaderRow
            ? rows[0].Select(Cell).ToArray()
            : Enumerable.Repeat(Cell(string.Empty), columns).ToArray();
        var bodySource = edit.HeaderRow ? rows.Skip(1) : rows;
        var body = bodySource.Select(static row =>
            (IReadOnlyList<TableCell>)row.Select(Cell).ToArray()).ToArray();
        var alignments = edit.Alignments.Concat(Enumerable.Repeat(TableAlignment.Default, columns))
            .Take(columns)
            .ToArray();
        var table = new TableBlock(1, header, body, alignments,
            string.IsNullOrWhiteSpace(edit.Caption) ? null : edit.Caption.Trim(),
            string.IsNullOrWhiteSpace(edit.Identifier) ? null : edit.Identifier.Trim());
        return TableMarkupCodec.ApplySpans(table, NormalizeMerges(edit.Merges ?? [], rows.Count, columns));
    }

    public static string Serialize(TableEditResult edit)
        => TableMarkupCodec.Serialize(ToTable(edit));

    public static IReadOnlyList<TableMergeSpan> NormalizeMerges(
        IEnumerable<TableMergeSpan> source,
        int rows,
        int columns)
    {
        rows = Math.Max(1, rows);
        columns = Math.Max(1, columns);
        var occupied = new bool[rows, columns];
        var result = new List<TableMergeSpan>();
        foreach (var span in source.Where(static item => item.IsMerged)
                     .OrderBy(static item => item.Row)
                     .ThenBy(static item => item.Column))
        {
            if (span.Row < 0 || span.Column < 0 || span.Row >= rows || span.Column >= columns) continue;
            var rowSpan = Math.Clamp(span.RowSpan, 1, rows - span.Row);
            var columnSpan = Math.Clamp(span.ColumnSpan, 1, columns - span.Column);
            if (rowSpan == 1 && columnSpan == 1) continue;
            var overlaps = false;
            for (var row = span.Row; row < span.Row + rowSpan && !overlaps; row++)
                for (var column = span.Column; column < span.Column + columnSpan; column++)
                    if (occupied[row, column]) { overlaps = true; break; }
            if (overlaps) continue;
            for (var row = span.Row; row < span.Row + rowSpan; row++)
                for (var column = span.Column; column < span.Column + columnSpan; column++)
                    occupied[row, column] = true;
            result.Add(new TableMergeSpan(span.Row, span.Column, rowSpan, columnSpan));
        }
        return result;
    }

    public static TableMergeSpan? CoveringSpan(IEnumerable<TableMergeSpan> spans, int row, int column)
        => spans.FirstOrDefault(span => row >= span.Row && row < span.Row + span.RowSpan &&
                                        column >= span.Column && column < span.Column + span.ColumnSpan);

    public static bool IsContinuation(IEnumerable<TableMergeSpan> spans, int row, int column)
    {
        var covering = CoveringSpan(spans, row, column);
        return covering is not null && (covering.Row != row || covering.Column != column);
    }

    private static List<List<string>> NormalizeCells(IReadOnlyList<IReadOnlyList<string>> source)
    {
        var rows = source.Select(static row => row.ToList()).ToList();
        if (rows.Count == 0) rows.Add([string.Empty]);
        var columns = Math.Max(1, rows.Max(static row => row.Count));
        foreach (var row in rows)
            while (row.Count < columns) row.Add(string.Empty);
        return rows;
    }

    private static TableCell Cell(string value)
        => new([new TextInline(value ?? string.Empty)]);
}
