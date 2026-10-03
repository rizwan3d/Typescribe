namespace Typescribe.Domain.Models;

public sealed record DocumentAst(IReadOnlyList<AstBlock> Blocks);

public abstract record AstBlock(int SourceLine);

public sealed record HeadingBlock(
    int SourceLine,
    int Level,
    IReadOnlyList<AstInline> Inlines,
    string? Identifier = null) : AstBlock(SourceLine);

public sealed record ParagraphBlock(
    int SourceLine,
    IReadOnlyList<AstInline> Inlines) : AstBlock(SourceLine);

public sealed record QuoteBlock(
    int SourceLine,
    IReadOnlyList<AstInline> Inlines) : AstBlock(SourceLine);

public sealed record ListItemBlock(
    int SourceLine,
    bool Ordered,
    int? Number,
    IReadOnlyList<AstInline> Inlines) : AstBlock(SourceLine);

public sealed record CodeBlock(int SourceLine, string Language, string Text) : AstBlock(SourceLine);
public sealed record DisplayMathBlock(int SourceLine, string Text, string? Identifier = null) : AstBlock(SourceLine);
public sealed record ThematicBreakBlock(int SourceLine) : AstBlock(SourceLine);

/// <summary>A single semantic table cell. Cell content uses the normal inline model.</summary>
public sealed record TableCell(IReadOnlyList<AstInline> Inlines);

public enum TableAlignment
{
    Default,
    Left,
    Center,
    Right
}

/// <summary>
/// A rectangular merged-cell region. Row zero is the table header and body rows start at one.
/// Column and row spans are always at least one; only spans larger than one are persisted.
/// </summary>
public sealed record TableCellSpan(
    int Row,
    int Column,
    int RowSpan = 1,
    int ColumnSpan = 1)
{
    public bool IsMerged => RowSpan > 1 || ColumnSpan > 1;
}

/// <summary>GitHub-flavoured Markdown style table, preserved as a first-class document structure.</summary>
public sealed record TableBlock(
    int SourceLine,
    IReadOnlyList<TableCell> Header,
    IReadOnlyList<IReadOnlyList<TableCell>> Rows,
    IReadOnlyList<TableAlignment>? Alignments = null,
    string? Caption = null,
    string? Identifier = null,
    IReadOnlyList<TableCellSpan>? Spans = null) : AstBlock(SourceLine);

/// <summary>Helpers for reasoning about merged cells without changing the rectangular storage grid.</summary>
public static class TableSpanLayout
{
    public static int RowCount(TableBlock table) => 1 + table.Rows.Count;

    public static int ColumnCount(TableBlock table)
    {
        var columns = table.Header.Count;
        foreach (var row in table.Rows) columns = Math.Max(columns, row.Count);
        return Math.Max(1, columns);
    }

    public static IReadOnlyList<TableCellSpan> Normalize(TableBlock table)
        => Normalize(table.Spans, RowCount(table), ColumnCount(table));

    public static IReadOnlyList<TableCellSpan> Normalize(
        IEnumerable<TableCellSpan>? spans,
        int rowCount,
        int columnCount)
    {
        rowCount = Math.Max(1, rowCount);
        columnCount = Math.Max(1, columnCount);
        if (spans is null) return [];

        var accepted = new List<TableCellSpan>();
        foreach (var raw in spans)
        {
            var row = Math.Clamp(raw.Row, 0, rowCount - 1);
            var column = Math.Clamp(raw.Column, 0, columnCount - 1);
            var rowSpan = Math.Clamp(raw.RowSpan, 1, rowCount - row);
            var columnSpan = Math.Clamp(raw.ColumnSpan, 1, columnCount - column);
            if (rowSpan == 1 && columnSpan == 1) continue;

            var candidate = new TableCellSpan(row, column, rowSpan, columnSpan);
            if (accepted.Any(existing => Overlaps(existing, candidate))) continue;
            accepted.Add(candidate);
        }

        return accepted
            .OrderBy(static span => span.Row)
            .ThenBy(static span => span.Column)
            .ToArray();
    }

    public static TableCellSpan? AnchorAt(TableBlock table, int row, int column)
        => Normalize(table).FirstOrDefault(span => span.Row == row && span.Column == column);

    public static TableCellSpan? Covering(TableBlock table, int row, int column)
        => Normalize(table).FirstOrDefault(span => Contains(span, row, column));

    public static bool IsCovered(TableBlock table, int row, int column)
    {
        var span = Covering(table, row, column);
        return span is not null && (span.Row != row || span.Column != column);
    }

    public static bool Contains(TableCellSpan span, int row, int column)
        => row >= span.Row && row < span.Row + span.RowSpan &&
           column >= span.Column && column < span.Column + span.ColumnSpan;

    public static bool Overlaps(TableCellSpan left, TableCellSpan right)
        => left.Row < right.Row + right.RowSpan &&
           right.Row < left.Row + left.RowSpan &&
           left.Column < right.Column + right.ColumnSpan &&
           right.Column < left.Column + left.ColumnSpan;
}

/// <summary>A manuscript figure with an optional stable label for cross-reference workflows.</summary>
public sealed record FigureBlock(
    int SourceLine,
    string Source,
    string Caption,
    string? Identifier) : AstBlock(SourceLine);

/// <summary>Definition for a Markdown footnote reference such as [^note1].</summary>
public sealed record FootnoteDefinitionBlock(
    int SourceLine,
    string Identifier,
    IReadOnlyList<AstInline> Inlines) : AstBlock(SourceLine);

/// <summary>
/// A bibliography entry injected into compilation source from the project bibliography database.
/// It is never rendered as manuscript body content; the renderer gathers all entries and emits a bibliography.
/// </summary>
public sealed record BibliographyEntryBlock(int SourceLine, BibliographyEntry Entry) : AstBlock(SourceLine);

public abstract record AstInline;
public sealed record TextInline(string Text) : AstInline;
public sealed record StrongInline(IReadOnlyList<AstInline> Children) : AstInline;
public sealed record EmphasisInline(IReadOnlyList<AstInline> Children) : AstInline;
public sealed record CodeInline(string Text) : AstInline;
public sealed record LinkInline(IReadOnlyList<AstInline> Label, string Url) : AstInline;
public sealed record MathInline(string Text) : AstInline;
public sealed record FootnoteReferenceInline(string Identifier) : AstInline;
public sealed record CitationInline(string Key, string? Locator = null) : AstInline;
public sealed record CrossReferenceInline(string Identifier) : AstInline;

public static class AstInlineText
{
    public static string ToPlainText(this IReadOnlyList<AstInline> inlines)
    {
        var writer = new System.Text.StringBuilder();
        AppendPlainText(writer, inlines);
        return writer.ToString();
    }

    private static void AppendPlainText(System.Text.StringBuilder writer, IEnumerable<AstInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextInline text:
                    writer.Append(text.Text);
                    break;
                case StrongInline strong:
                    AppendPlainText(writer, strong.Children);
                    break;
                case EmphasisInline emphasis:
                    AppendPlainText(writer, emphasis.Children);
                    break;
                case CodeInline code:
                    writer.Append(code.Text);
                    break;
                case LinkInline link:
                    AppendPlainText(writer, link.Label);
                    break;
                case MathInline math:
                    writer.Append(math.Text);
                    break;
                case FootnoteReferenceInline footnote:
                    writer.Append("[^").Append(footnote.Identifier).Append(']');
                    break;
                case CitationInline citation:
                    writer.Append("[@").Append(citation.Key);
                    if (!string.IsNullOrWhiteSpace(citation.Locator))
                        writer.Append(", ").Append(citation.Locator);
                    writer.Append(']');
                    break;
                case CrossReferenceInline reference:
                    writer.Append("[@ref:").Append(reference.Identifier).Append(']');
                    break;
            }
        }
    }
}
