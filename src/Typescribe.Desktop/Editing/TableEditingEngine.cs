using Typescribe.Application.Abstractions;
using Typescribe.Application.Services;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop.Editing;

internal sealed record TableEditingContext(
    int Offset,
    int Length,
    string Text,
    int FirstTableLine,
    TableEditResult Edit,
    int Row,
    int Column);

/// <summary>
/// Single source of truth for locating, reading and structurally editing canonical Markdown tables.
/// All table surfaces should route row/column/merge/alignment operations through this engine so
/// metadata and merged-cell geometry cannot drift between UI entry points.
/// </summary>
internal static class TableEditingEngine
{
    public static TableEditingContext? FindCurrent(ManuscriptEditor editor, IDocumentParser parser)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(parser);
        if (editor.Document.TextLength == 0) return null;

        var caret = Math.Clamp(editor.CaretIndex, 0, editor.Document.TextLength);
        if (caret == editor.Document.TextLength && caret > 0) caret--;
        var line = editor.Document.GetLineByOffset(caret);
        var lineNumber = line.LineNumber;

        bool PipeLine(int number)
        {
            if (number < 1 || number > editor.Document.LineCount) return false;
            var candidate = editor.Document.GetLineByNumber(number);
            return editor.Document.GetText(candidate.Offset, candidate.Length).Contains('|');
        }

        if (!PipeLine(lineNumber))
        {
            var current = editor.Document.GetText(line.Offset, line.Length).Trim();
            if (current.StartsWith(TableMarkupCodec.MetadataPrefix, StringComparison.Ordinal) && PipeLine(lineNumber + 1))
                lineNumber++;
            else
                return null;
        }

        var firstTableLine = lineNumber;
        var lastTableLine = lineNumber;
        while (PipeLine(firstTableLine - 1)) firstTableLine--;
        while (PipeLine(lastTableLine + 1)) lastTableLine++;

        var firstSourceLine = firstTableLine;
        if (firstTableLine > 1)
        {
            var metadata = editor.Document.GetLineByNumber(firstTableLine - 1);
            var metadataText = editor.Document.GetText(metadata.Offset, metadata.Length).Trim();
            if (metadataText.StartsWith(TableMarkupCodec.MetadataPrefix, StringComparison.Ordinal))
                firstSourceLine--;
        }

        var first = editor.Document.GetLineByNumber(firstSourceLine);
        var last = editor.Document.GetLineByNumber(lastTableLine);
        var length = last.EndOffset - first.Offset;
        var text = editor.Document.GetText(first.Offset, length);
        var table = parser.Parse(text).Blocks.OfType<TableBlock>().FirstOrDefault();
        if (table is null) return null;

        var edit = Normalize(TableEditCodec.FromTable(table));
        var coordinate = GetCoordinate(editor, firstTableLine, edit);
        return new TableEditingContext(
            first.Offset,
            length,
            text,
            firstTableLine,
            edit,
            coordinate.Row,
            coordinate.Column);
    }

    /// <summary>
    /// Finds every canonical Markdown table in the manuscript so editor rendering can replace
    /// pipe syntax with a real visual grid even when the caret is outside the table.
    /// </summary>
    public static IReadOnlyList<TableEditingContext> FindAll(ManuscriptEditor editor, IDocumentParser parser)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(parser);
        if (editor.Document.TextLength == 0 || editor.Document.LineCount == 0) return [];

        bool PipeLine(int number)
        {
            if (number < 1 || number > editor.Document.LineCount) return false;
            var candidate = editor.Document.GetLineByNumber(number);
            return editor.Document.GetText(candidate.Offset, candidate.Length).Contains('|');
        }

        bool MetadataLine(int number)
        {
            if (number < 1 || number > editor.Document.LineCount) return false;
            var candidate = editor.Document.GetLineByNumber(number);
            var text = editor.Document.GetText(candidate.Offset, candidate.Length).Trim();
            return text.StartsWith(TableMarkupCodec.MetadataPrefix, StringComparison.Ordinal);
        }

        var result = new List<TableEditingContext>();
        var lineNumber = 1;
        while (lineNumber <= editor.Document.LineCount)
        {
            var firstTableLine = lineNumber;
            if (MetadataLine(lineNumber) && PipeLine(lineNumber + 1))
            {
                firstTableLine = lineNumber + 1;
            }
            else if (PipeLine(lineNumber))
            {
                // A table is handled from the first line in its pipe-row group only.
                if (PipeLine(lineNumber - 1))
                {
                    lineNumber++;
                    continue;
                }
            }
            else
            {
                lineNumber++;
                continue;
            }

            var lastTableLine = firstTableLine;
            while (PipeLine(lastTableLine + 1)) lastTableLine++;

            var firstSourceLine = firstTableLine;
            if (firstTableLine > 1 && MetadataLine(firstTableLine - 1)) firstSourceLine--;

            var first = editor.Document.GetLineByNumber(firstSourceLine);
            var last = editor.Document.GetLineByNumber(lastTableLine);
            var length = last.EndOffset - first.Offset;
            if (length <= 0)
            {
                lineNumber = Math.Max(lineNumber + 1, lastTableLine + 1);
                continue;
            }

            var text = editor.Document.GetText(first.Offset, length);
            var table = parser.Parse(text).Blocks.OfType<TableBlock>().FirstOrDefault();
            if (table is not null)
            {
                result.Add(new TableEditingContext(
                    first.Offset,
                    length,
                    text,
                    firstTableLine,
                    Normalize(TableEditCodec.FromTable(table)),
                    0,
                    0));
                lineNumber = lastTableLine + 1;
                continue;
            }

            lineNumber = Math.Max(lineNumber + 1, firstTableLine + 1);
        }

        return result;
    }

    public static TableEditResult Normalize(TableEditResult edit)
    {
        var cells = NormalizeCells(edit.Cells);
        var columns = cells[0].Count;
        var alignments = edit.Alignments.ToList();
        while (alignments.Count < columns) alignments.Add(TableAlignment.Default);
        while (alignments.Count > columns) alignments.RemoveAt(alignments.Count - 1);
        var merges = TableEditCodec.NormalizeMerges(edit.Merges ?? [], cells.Count, columns);
        return edit with
        {
            Cells = Snapshot(cells),
            HeaderRow = true,
            Alignments = alignments,
            Merges = merges
        };
    }

    public static TableEditResult InsertRow(TableEditResult edit, int index)
    {
        edit = Normalize(edit);
        var cells = NormalizeCells(edit.Cells);
        index = Math.Clamp(index, 0, cells.Count);
        cells.Insert(index, Enumerable.Repeat(string.Empty, cells[0].Count).ToList());
        var merges = (edit.Merges ?? []).Select(span =>
        {
            if (span.Row >= index) return span with { Row = span.Row + 1 };
            if (span.Row < index && span.Row + span.RowSpan > index) return span with { RowSpan = span.RowSpan + 1 };
            return span;
        });
        return Normalize(edit with { Cells = Snapshot(cells), Merges = merges.ToArray() });
    }

    public static TableEditResult DeleteRow(TableEditResult edit, int index)
    {
        edit = Normalize(edit);
        var cells = NormalizeCells(edit.Cells);
        if (cells.Count <= 1) return edit;
        index = Math.Clamp(index, 0, cells.Count - 1);
        cells.RemoveAt(index);
        var merges = new List<TableMergeSpan>();
        foreach (var span in edit.Merges ?? [])
        {
            if (index < span.Row) merges.Add(span with { Row = span.Row - 1 });
            else if (index >= span.Row + span.RowSpan) merges.Add(span);
            else if (span.RowSpan > 1) merges.Add(span with { RowSpan = span.RowSpan - 1 });
        }
        return Normalize(edit with { Cells = Snapshot(cells), Merges = merges });
    }

    public static TableEditResult InsertColumn(TableEditResult edit, int index)
    {
        edit = Normalize(edit);
        var cells = NormalizeCells(edit.Cells);
        index = Math.Clamp(index, 0, cells[0].Count);
        foreach (var row in cells) row.Insert(index, string.Empty);

        var merges = (edit.Merges ?? []).Select(span =>
        {
            if (span.Column >= index) return span with { Column = span.Column + 1 };
            if (span.Column < index && span.Column + span.ColumnSpan > index) return span with { ColumnSpan = span.ColumnSpan + 1 };
            return span;
        }).ToArray();
        var alignments = edit.Alignments.ToList();
        alignments.Insert(Math.Min(index, alignments.Count), TableAlignment.Default);
        return Normalize(edit with { Cells = Snapshot(cells), Alignments = alignments, Merges = merges });
    }

    public static TableEditResult DeleteColumn(TableEditResult edit, int index)
    {
        edit = Normalize(edit);
        var cells = NormalizeCells(edit.Cells);
        if (cells[0].Count <= 1) return edit;
        index = Math.Clamp(index, 0, cells[0].Count - 1);
        foreach (var row in cells) row.RemoveAt(index);

        var merges = new List<TableMergeSpan>();
        foreach (var span in edit.Merges ?? [])
        {
            if (index < span.Column) merges.Add(span with { Column = span.Column - 1 });
            else if (index >= span.Column + span.ColumnSpan) merges.Add(span);
            else if (span.ColumnSpan > 1) merges.Add(span with { ColumnSpan = span.ColumnSpan - 1 });
        }
        var alignments = edit.Alignments.ToList();
        if (index < alignments.Count) alignments.RemoveAt(index);
        return Normalize(edit with { Cells = Snapshot(cells), Alignments = alignments, Merges = merges });
    }

    public static TableEditResult SetAlignment(TableEditResult edit, int column, TableAlignment alignment)
    {
        edit = Normalize(edit);
        var alignments = edit.Alignments.ToList();
        column = Math.Clamp(column, 0, alignments.Count - 1);
        alignments[column] = alignment;
        return edit with { Alignments = alignments };
    }

    public static TableEditResult Merge(TableEditResult edit, int row, int column, int rowSpan, int columnSpan)
    {
        edit = Normalize(edit);
        var cells = NormalizeCells(edit.Cells);
        var merges = (edit.Merges ?? []).ToList();
        row = Math.Clamp(row, 0, cells.Count - 1);
        column = Math.Clamp(column, 0, cells[0].Count - 1);
        if (row + rowSpan > cells.Count || column + columnSpan > cells[0].Count) return edit;
        if (TableEditCodec.CoveringSpan(merges, row, column) is not null) return edit;

        for (var currentRow = row; currentRow < row + rowSpan; currentRow++)
            for (var currentColumn = column; currentColumn < column + columnSpan; currentColumn++)
                if (TableEditCodec.CoveringSpan(merges, currentRow, currentColumn) is not null)
                    return edit;

        var combined = new List<string>();
        for (var currentRow = row; currentRow < row + rowSpan; currentRow++)
        {
            for (var currentColumn = column; currentColumn < column + columnSpan; currentColumn++)
            {
                var value = cells[currentRow][currentColumn].Trim();
                if (value.Length > 0) combined.Add(value);
                if (currentRow != row || currentColumn != column)
                    cells[currentRow][currentColumn] = string.Empty;
            }
        }
        cells[row][column] = string.Join(" ", combined);
        merges.Add(new TableMergeSpan(row, column, rowSpan, columnSpan));
        return Normalize(edit with { Cells = Snapshot(cells), Merges = merges });
    }

    public static TableEditResult Unmerge(TableEditResult edit, int row, int column)
    {
        edit = Normalize(edit);
        var merges = (edit.Merges ?? []).ToList();
        var covering = TableEditCodec.CoveringSpan(merges, row, column);
        if (covering is null) return edit;
        merges.Remove(covering);
        return Normalize(edit with { Merges = merges });
    }

    public static string Serialize(TableEditResult edit)
        => TableEditCodec.Serialize(Normalize(edit));

    public static TableEditingContext Replace(
        ManuscriptEditor editor,
        TableEditingContext context,
        TableEditResult edit)
    {
        edit = Normalize(edit);
        var markup = Serialize(edit);
        if (!string.Equals(markup, context.Text, StringComparison.Ordinal))
            editor.Document.Replace(context.Offset, context.Length, markup);
        return context with { Length = markup.Length, Text = markup, Edit = edit };
    }

    public static List<List<string>> NormalizeCells(IReadOnlyList<IReadOnlyList<string>> source)
    {
        var rows = source.Select(static row => row.ToList()).ToList();
        if (rows.Count == 0) rows.Add([string.Empty]);
        var columns = Math.Max(1, rows.Max(static row => row.Count));
        foreach (var row in rows)
            while (row.Count < columns) row.Add(string.Empty);
        return rows;
    }

    private static (int Row, int Column) GetCoordinate(ManuscriptEditor editor, int firstTableLine, TableEditResult edit)
    {
        var cells = NormalizeCells(edit.Cells);
        var caret = Math.Clamp(editor.CaretIndex, 0, editor.Document.TextLength);
        if (caret == editor.Document.TextLength && caret > 0) caret--;
        var line = editor.Document.GetLineByOffset(caret);
        var separatorLine = firstTableLine + 1;
        var row = line.LineNumber <= separatorLine ? 0 : line.LineNumber - separatorLine;
        row = Math.Clamp(row, 0, cells.Count - 1);

        var lineText = editor.Document.GetText(line.Offset, line.Length);
        var relativeCaret = Math.Clamp(editor.CaretIndex - line.Offset, 0, lineText.Length);
        var pipes = 0;
        for (var index = 0; index < relativeCaret; index++)
            if (lineText[index] == '|' && (index == 0 || lineText[index - 1] != '\\')) pipes++;
        var column = Math.Clamp(Math.Max(0, pipes - 1), 0, cells[0].Count - 1);
        var covering = TableEditCodec.CoveringSpan(edit.Merges ?? [], row, column);
        return covering is null ? (row, column) : (covering.Row, covering.Column);
    }

    private static IReadOnlyList<IReadOnlyList<string>> Snapshot(List<List<string>> cells)
        => cells.Select(static row => (IReadOnlyList<string>)row.ToArray()).ToArray();
}