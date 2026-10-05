namespace Typescribe.Domain.Models;

public sealed record DocumentAst(IReadOnlyList<AstBlock> Blocks);

public abstract record AstBlock(int SourceLine)
{
    /// <summary>
    /// Optional Markdown-first rich formatting attached to this semantic block. The source stays
    /// ordinary Markdown; RichDocumentParser hydrates this value from a preceding
    /// typescribe:block64 metadata comment.
    /// </summary>
    public RichBlockFormatting? Formatting { get; init; }
}

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

/// <summary>
/// A single semantic table grid cell. Merged regions are represented by one anchor cell
/// carrying RowSpan/ColumnSpan and continuation cells marked IsSpanContinuation.
/// StyleId and Properties are instance-level semantic formatting and survive canonical-text editing.
/// </summary>
public sealed record TableCell(
    IReadOnlyList<AstInline> Inlines,
    int RowSpan = 1,
    int ColumnSpan = 1,
    bool IsSpanContinuation = false,
    string? StyleId = null,
    TableCellProperties? Properties = null);

public enum TableAlignment
{
    Default,
    Left,
    Center,
    Right
}

/// <summary>
/// Markdown-compatible semantic table. Pipe rows remain the canonical editable text while
/// richer layout/style information is carried in Typescribe table metadata.
/// </summary>
public sealed record TableBlock(
    int SourceLine,
    IReadOnlyList<TableCell> Header,
    IReadOnlyList<IReadOnlyList<TableCell>> Rows,
    IReadOnlyList<TableAlignment>? Alignments = null,
    string? Caption = null,
    string? Identifier = null,
    string? StyleId = null,
    IReadOnlyList<TableRowProperties>? RowProperties = null,
    int RepeatHeaderRows = 1,
    TableProperties? Properties = null) : AstBlock(SourceLine);

/// <summary>
/// Semantic manuscript figure. Source/caption/id remain Markdown-friendly while accessibility,
/// reusable style, source kind, credit and layout are preserved as Typescribe metadata.
/// </summary>
public sealed record FigureBlock(
    int SourceLine,
    string Source,
    string Caption,
    string? Identifier,
    string? AltText = null,
    string? Credit = null,
    string? StyleId = null,
    FigureSourceKind SourceKind = FigureSourceKind.ProjectRelative,
    FigureLayout? Layout = null) : AstBlock(SourceLine);

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

/// <summary>
/// Plain semantic text. This record is intentionally non-sealed so richer text ranges can remain
/// backward-compatible with exporters that only understand TextInline while rich-aware exporters
/// can still inspect their additional semantics.
/// </summary>
public record TextInline(string Text) : AstInline;

public sealed record StrongInline(IReadOnlyList<AstInline> Children) : AstInline;
public sealed record EmphasisInline(IReadOnlyList<AstInline> Children) : AstInline;
public sealed record CodeInline(string Text) : AstInline;
public sealed record LinkInline(IReadOnlyList<AstInline> Label, string Url) : AstInline;
public sealed record MathInline(string Text) : AstInline;
public sealed record FootnoteReferenceInline(string Identifier) : AstInline;
public sealed record CitationInline(string Key, string? Locator = null) : AstInline;
public sealed record CrossReferenceInline(string Identifier) : AstInline;

/// <summary>
/// A character-formatted inline range decoded from TypeScribe's paired Markdown metadata
/// comments. Children retain normal Markdown semantics (strong/emphasis/link/etc.) so rich-aware
/// exporters can apply typography without flattening authored structure. It also derives from
/// TextInline using the plain Unicode text as a compatibility fallback, so older exporters never
/// drop the content merely because they do not know the richer node yet.
/// </summary>
public sealed record RichSpanInline(
    IReadOnlyList<AstInline> Children,
    CharacterFormatting Formatting) : TextInline(Children.ToPlainText());

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
                case RichSpanInline rich:
                    AppendPlainText(writer, rich.Children);
                    break;
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