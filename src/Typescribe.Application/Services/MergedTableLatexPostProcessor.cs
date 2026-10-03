using System.Text;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Rewrites generated LaTeX table environments only when semantic merged cells are present.
/// Normal tables remain byte-for-byte on the established rendering path.
/// </summary>
internal static class MergedTableLatexPostProcessor
{
    public static string Apply(string latex, DocumentAst document)
    {
        var tables = document.Blocks.OfType<TableBlock>().ToArray();
        if (tables.All(static table => TableMarkupCodec.ExtractSpans(table).Count == 0)) return latex;

        if (!latex.Contains("\\usepackage{multirow}", StringComparison.Ordinal))
        {
            var marker = "\\usepackage{tabularx}";
            var index = latex.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
                latex = latex.Insert(index + marker.Length, Environment.NewLine + "\\usepackage{multirow}");
            else
                latex = "\\usepackage{multirow}" + Environment.NewLine + latex;
        }

        var search = 0;
        foreach (var table in tables)
        {
            var start = latex.IndexOf("\\begin{table}", search, StringComparison.Ordinal);
            if (start < 0) break;
            var endToken = "\\end{table}";
            var end = latex.IndexOf(endToken, start, StringComparison.Ordinal);
            if (end < 0) break;
            end += endToken.Length;

            if (TableMarkupCodec.ExtractSpans(table).Count > 0)
            {
                var replacement = Render(table);
                latex = latex.Remove(start, end - start).Insert(start, replacement);
                search = start + replacement.Length;
            }
            else
            {
                search = end;
            }
        }
        return latex;
    }

    private static string Render(TableBlock table)
    {
        var rows = new List<IReadOnlyList<TableCell>> { table.Header };
        rows.AddRange(table.Rows);
        var columns = Math.Max(1, rows.Max(static row => row.Count));
        var output = new StringBuilder();
        output.AppendLine("\\begin{table}[htbp]");
        output.AppendLine("\\centering");
        output.Append("\\begin{tabularx}{\\linewidth}{");
        for (var column = 0; column < columns; column++) output.Append(ColumnSpec(table, column));
        output.AppendLine("}");
        output.AppendLine("\\toprule");

        for (var row = 0; row < rows.Count; row++)
        {
            var rendered = new List<string>();
            var column = 0;
            while (column < columns)
            {
                var cell = column < rows[row].Count ? rows[row][column] : EmptyCell();
                if (!cell.IsSpanContinuation)
                {
                    var text = EscapeLatex(cell.Inlines.ToPlainText());
                    if (row == 0) text = "\\textbf{" + text + "}";
                    if (cell.RowSpan > 1) text = $"\\multirow{{{cell.RowSpan}}}{{*}}{{{text}}}";
                    if (cell.ColumnSpan > 1)
                    {
                        var spec = ColumnSpec(table, column);
                        text = $"\\multicolumn{{{cell.ColumnSpan}}}{{{spec}}}{{{text}}}";
                    }
                    rendered.Add(text);
                    column += Math.Max(1, cell.ColumnSpan);
                    continue;
                }

                var anchor = FindCoveringAnchor(rows, row, column);
                if (anchor is not null && anchor.Value.Row < row && anchor.Value.Column == column)
                {
                    var width = Math.Max(1, anchor.Value.Cell.ColumnSpan);
                    rendered.Add(width > 1 ? $"\\multicolumn{{{width}}}{{{ColumnSpec(table, column)}}}{{}}" : string.Empty);
                    column += width;
                }
                else
                {
                    column++;
                }
            }

            output.AppendLine(string.Join(" & ", rendered) + " \\\\");
            if (row == 0) output.AppendLine("\\midrule");
        }

        output.AppendLine("\\bottomrule");
        output.AppendLine("\\end{tabularx}");
        if (!string.IsNullOrWhiteSpace(table.Caption))
            output.Append("\\caption{").Append(EscapeLatex(table.Caption!)).AppendLine("}");
        if (!string.IsNullOrWhiteSpace(table.Identifier))
            output.Append("\\label{").Append(SanitizeLabel(table.Identifier!)).AppendLine("}");
        output.Append("\\end{table}");
        return output.ToString();
    }

    private static (int Row, int Column, TableCell Cell)? FindCoveringAnchor(
        IReadOnlyList<IReadOnlyList<TableCell>> rows,
        int targetRow,
        int targetColumn)
    {
        for (var row = 0; row <= targetRow; row++)
        {
            for (var column = 0; column < rows[row].Count; column++)
            {
                var cell = rows[row][column];
                if (cell.IsSpanContinuation || (cell.RowSpan <= 1 && cell.ColumnSpan <= 1)) continue;
                if (targetRow >= row && targetRow < row + cell.RowSpan &&
                    targetColumn >= column && targetColumn < column + cell.ColumnSpan)
                    return (row, column, cell);
            }
        }
        return null;
    }

    private static string ColumnSpec(TableBlock table, int column)
    {
        var alignment = table.Alignments is not null && column < table.Alignments.Count
            ? table.Alignments[column]
            : TableAlignment.Default;
        return alignment switch
        {
            TableAlignment.Center => ">{\\centering\\arraybackslash}X",
            TableAlignment.Right => ">{\\raggedleft\\arraybackslash}X",
            _ => ">{\\raggedright\\arraybackslash}X"
        };
    }

    private static TableCell EmptyCell() => new([new TextInline(string.Empty)]);

    private static string EscapeLatex(string value)
        => value.Replace("\\", "\\textbackslash{}", StringComparison.Ordinal)
            .Replace("{", "\\{", StringComparison.Ordinal)
            .Replace("}", "\\}", StringComparison.Ordinal)
            .Replace("#", "\\#", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("&", "\\&", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("^", "\\^{}", StringComparison.Ordinal)
            .Replace("~", "\\~{}", StringComparison.Ordinal);

    private static string SanitizeLabel(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.' ? character : '-');
        return builder.Length == 0 ? "table" : builder.ToString();
    }
}
