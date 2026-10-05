using System.Text;
using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Decorates the normal Markdown parser with TypeScribe's optional rich-formatting metadata.
/// Metadata comments are removed before Markdown parsing, then hydrated back into semantic AST
/// nodes. This keeps one Markdown source of truth while giving every exporter the same rich model.
/// </summary>
public sealed class RichDocumentParser : IDocumentParser
{
    private const string OpenTokenPrefix = "TYPESCRIBERICHOPEN";
    private const string CloseTokenPrefix = "TYPESCRIBERICHCLOSE";
    private readonly IDocumentParser _inner;

    public RichDocumentParser(IDocumentParser inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public DocumentAst Parse(string source)
    {
        source ??= string.Empty;
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var blockFormatting = new Dictionary<int, RichBlockFormatting>();

        for (var index = 0; index < lines.Length; index++)
        {
            if (!RichMarkdownFormattingCodec.TryReadBlockMetadata(lines[index], out var formatting) || formatting is null)
                continue;

            var target = index + 1;
            while (target < lines.Length &&
                   (string.IsNullOrWhiteSpace(lines[target]) ||
                    lines[target].TrimStart().StartsWith(TableMarkupCodec.MetadataPrefix, StringComparison.Ordinal) ||
                    lines[target].TrimStart().StartsWith(FigureMarkupCodec.MetadataPrefix, StringComparison.Ordinal)))
            {
                target++;
            }

            if (target < lines.Length) blockFormatting[target + 1] = formatting;
            // Preserve line numbering for source maps and diagnostics.
            lines[index] = string.Empty;
        }

        var inlineFormatting = new Dictionary<string, CharacterFormatting>(StringComparer.Ordinal);
        var cleanSource = ReplaceInlineMetadata(string.Join("\n", lines), inlineFormatting);
        var parsed = _inner.Parse(cleanSource);
        var blocks = new List<AstBlock>(parsed.Blocks.Count);

        foreach (var original in parsed.Blocks)
        {
            var block = RewriteBlock(original, inlineFormatting);
            if (blockFormatting.TryGetValue(block.SourceLine, out var formatting))
                block = block with { Formatting = formatting };
            blocks.Add(block);
        }

        return new DocumentAst(blocks);
    }

    private static string ReplaceInlineMetadata(
        string source,
        IDictionary<string, CharacterFormatting> formatting)
    {
        if (source.Length == 0 || !source.Contains(RichMarkdownFormattingCodec.InlinePrefix, StringComparison.Ordinal))
            return source;

        var output = new StringBuilder(source.Length);
        var cursor = 0;
        var number = 0;
        while (cursor < source.Length)
        {
            var open = source.IndexOf(RichMarkdownFormattingCodec.InlinePrefix, cursor, StringComparison.Ordinal);
            if (open < 0)
            {
                output.Append(source, cursor, source.Length - cursor);
                break;
            }

            output.Append(source, cursor, open - cursor);
            var openEnd = source.IndexOf(" -->", open, StringComparison.Ordinal);
            if (openEnd < 0)
            {
                output.Append(source, open, source.Length - open);
                break;
            }
            openEnd += 4;

            var close = source.IndexOf(RichMarkdownFormattingCodec.InlineClose, openEnd, StringComparison.Ordinal);
            if (close < 0)
            {
                output.Append(source, open, source.Length - open);
                break;
            }

            var marker = source[open..openEnd];
            if (!RichMarkdownFormattingCodec.TryReadInlineOpen(marker, out var current) || current is null)
            {
                output.Append(source, open, openEnd - open);
                cursor = openEnd;
                continue;
            }

            var id = number.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            number++;
            formatting[id] = current;
            output.Append(OpenTokenPrefix).Append(id);
            output.Append(source, openEnd, close - openEnd);
            output.Append(CloseTokenPrefix).Append(id);
            cursor = close + RichMarkdownFormattingCodec.InlineClose.Length;
        }

        return output.ToString();
    }

    private static AstBlock RewriteBlock(
        AstBlock block,
        IReadOnlyDictionary<string, CharacterFormatting> formatting)
        => block switch
        {
            HeadingBlock heading => heading with { Inlines = RewriteInlines(heading.Inlines, formatting) },
            ParagraphBlock paragraph => paragraph with { Inlines = RewriteInlines(paragraph.Inlines, formatting) },
            QuoteBlock quote => quote with { Inlines = RewriteInlines(quote.Inlines, formatting) },
            ListItemBlock item => item with { Inlines = RewriteInlines(item.Inlines, formatting) },
            FootnoteDefinitionBlock note => note with { Inlines = RewriteInlines(note.Inlines, formatting) },
            TableBlock table => table with
            {
                Header = table.Header.Select(cell => RewriteCell(cell, formatting)).ToArray(),
                Rows = table.Rows.Select(row => (IReadOnlyList<TableCell>)row.Select(cell => RewriteCell(cell, formatting)).ToArray()).ToArray()
            },
            _ => block
        };

    private static TableCell RewriteCell(
        TableCell cell,
        IReadOnlyDictionary<string, CharacterFormatting> formatting)
        => cell with { Inlines = RewriteInlines(cell.Inlines, formatting) };

    private static IReadOnlyList<AstInline> RewriteInlines(
        IReadOnlyList<AstInline> source,
        IReadOnlyDictionary<string, CharacterFormatting> formatting)
    {
        var expanded = new List<AstInline>();
        foreach (var inline in source)
        {
            switch (inline)
            {
                // RichSpanInline derives from TextInline as a fail-safe for older exporters, so it
                // must be matched before the general TextInline case in rich-aware code.
                case RichSpanInline rich:
                    expanded.Add(rich with { Children = RewriteInlines(rich.Children, formatting) });
                    break;
                case TextInline text:
                    SplitMarkers(text.Text, expanded);
                    break;
                case StrongInline strong:
                    expanded.Add(strong with { Children = RewriteInlines(strong.Children, formatting) });
                    break;
                case EmphasisInline emphasis:
                    expanded.Add(emphasis with { Children = RewriteInlines(emphasis.Children, formatting) });
                    break;
                case LinkInline link:
                    expanded.Add(link with { Label = RewriteInlines(link.Label, formatting) });
                    break;
                default:
                    expanded.Add(inline);
                    break;
            }
        }

        var result = new List<AstInline>();
        var stack = new Stack<RichFrame>();
        foreach (var inline in expanded)
        {
            if (inline is RichMarkerInline { Open: true } open && formatting.TryGetValue(open.Id, out var richFormatting))
            {
                stack.Push(new RichFrame(open.Id, richFormatting));
                continue;
            }

            if (inline is RichMarkerInline { Open: false } close)
            {
                if (stack.Count > 0 && string.Equals(stack.Peek().Id, close.Id, StringComparison.Ordinal))
                {
                    var frame = stack.Pop();
                    var rich = new RichSpanInline(frame.Children.ToArray(), frame.Formatting);
                    Append(result, stack, rich);
                }
                continue;
            }

            Append(result, stack, inline);
        }

        // Malformed/unclosed metadata should never eat authored text. Preserve collected content
        // without formatting if a closing token was missing.
        while (stack.Count > 0)
        {
            var frame = stack.Pop();
            foreach (var child in frame.Children)
                Append(result, stack, child);
        }
        return result;
    }

    private static void Append(List<AstInline> root, Stack<RichFrame> stack, AstInline inline)
    {
        if (stack.Count == 0) root.Add(inline);
        else stack.Peek().Children.Add(inline);
    }

    private static void SplitMarkers(string text, ICollection<AstInline> output)
    {
        var cursor = 0;
        while (cursor < text.Length)
        {
            var open = text.IndexOf(OpenTokenPrefix, cursor, StringComparison.Ordinal);
            var close = text.IndexOf(CloseTokenPrefix, cursor, StringComparison.Ordinal);
            var next = MinPositive(open, close);
            if (next < 0)
            {
                if (cursor < text.Length) output.Add(new TextInline(text[cursor..]));
                break;
            }

            if (next > cursor) output.Add(new TextInline(text[cursor..next]));
            var isOpen = next == open;
            var prefix = isOpen ? OpenTokenPrefix : CloseTokenPrefix;
            var idStart = next + prefix.Length;
            if (idStart + 6 > text.Length)
            {
                output.Add(new TextInline(text[next..]));
                break;
            }

            var id = text.Substring(idStart, 6);
            if (!id.All(char.IsDigit))
            {
                output.Add(new TextInline(text[next].ToString()));
                cursor = next + 1;
                continue;
            }

            output.Add(new RichMarkerInline(id, isOpen));
            cursor = idStart + 6;
        }

        if (text.Length == 0) output.Add(new TextInline(string.Empty));
    }

    private static int MinPositive(int left, int right)
    {
        if (left < 0) return right;
        if (right < 0) return left;
        return Math.Min(left, right);
    }

    private sealed record RichMarkerInline(string Id, bool Open) : AstInline;

    private sealed class RichFrame(string id, CharacterFormatting formatting)
    {
        public string Id { get; } = id;
        public CharacterFormatting Formatting { get; } = formatting;
        public List<AstInline> Children { get; } = [];
    }
}