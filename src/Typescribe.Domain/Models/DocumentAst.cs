namespace Typescribe.Domain.Models;

public sealed record DocumentAst(IReadOnlyList<AstBlock> Blocks);

public abstract record AstBlock(int SourceLine);

public sealed record HeadingBlock(
    int SourceLine,
    int Level,
    IReadOnlyList<AstInline> Inlines) : AstBlock(SourceLine);

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
public sealed record DisplayMathBlock(int SourceLine, string Text) : AstBlock(SourceLine);
public sealed record ThematicBreakBlock(int SourceLine) : AstBlock(SourceLine);

public abstract record AstInline;
public sealed record TextInline(string Text) : AstInline;
public sealed record StrongInline(IReadOnlyList<AstInline> Children) : AstInline;
public sealed record EmphasisInline(IReadOnlyList<AstInline> Children) : AstInline;
public sealed record CodeInline(string Text) : AstInline;
public sealed record LinkInline(IReadOnlyList<AstInline> Label, string Url) : AstInline;
public sealed record MathInline(string Text) : AstInline;

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
            }
        }
    }
}
