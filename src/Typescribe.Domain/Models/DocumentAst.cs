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

/// <summary>GitHub-flavoured Markdown style table, preserved as a first-class document structure.</summary>
public sealed record TableBlock(
    int SourceLine,
    IReadOnlyList<TableCell> Header,
    IReadOnlyList<IReadOnlyList<TableCell>> Rows,
    IReadOnlyList<TableAlignment>? Alignments = null,
    string? Caption = null,
    string? Identifier = null) : AstBlock(SourceLine);

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
