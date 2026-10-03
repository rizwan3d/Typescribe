using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Expands a compact, deterministic set of Markdown-style emoji shortcodes in text inlines.
/// Canonical manuscript source remains unchanged; expansion happens only in the semantic AST.
/// This outer semantic layer also reapplies Typescribe table merge geometry stored in table metadata.
/// </summary>
public sealed class EmojiDocumentParser : IDocumentParser
{
    private readonly IDocumentParser _inner;

    public EmojiDocumentParser(IDocumentParser inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public DocumentAst Parse(string source)
    {
        source ??= string.Empty;
        var parsed = _inner.Parse(source);
        var spansByHeaderLine = ReadSpanMetadata(source);
        var blocks = new AstBlock[parsed.Blocks.Count];
        for (var index = 0; index < parsed.Blocks.Count; index++)
        {
            var rewritten = RewriteBlock(parsed.Blocks[index]);
            if (rewritten is TableBlock table &&
                spansByHeaderLine.TryGetValue(table.SourceLine, out var spans) &&
                spans.Count > 0)
            {
                rewritten = TableMarkupCodec.ApplySpans(table, spans);
            }
            blocks[index] = rewritten;
        }
        return new DocumentAst(blocks);
    }

    private static Dictionary<int, IReadOnlyList<TableMergeSpan>> ReadSpanMetadata(string source)
    {
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var result = new Dictionary<int, IReadOnlyList<TableMergeSpan>>();
        for (var index = 0; index < lines.Length - 1; index++)
        {
            if (!TableMarkupCodec.TryReadSpans(lines[index], out var spans) || spans.Count == 0) continue;
            var next = index + 1;
            while (next < lines.Length && string.IsNullOrWhiteSpace(lines[next])) next++;
            if (next >= lines.Length || !lines[next].Contains('|')) continue;
            result[next + 1] = spans;
        }
        return result;
    }

    private static AstBlock RewriteBlock(AstBlock block)
        => block switch
        {
            HeadingBlock heading => heading with { Inlines = RewriteInlines(heading.Inlines) },
            ParagraphBlock paragraph => paragraph with { Inlines = RewriteInlines(paragraph.Inlines) },
            QuoteBlock quote => quote with { Inlines = RewriteInlines(quote.Inlines) },
            ListItemBlock item => item with { Inlines = RewriteInlines(item.Inlines) },
            FootnoteDefinitionBlock note => note with { Inlines = RewriteInlines(note.Inlines) },
            TableBlock table => table with
            {
                Header = table.Header.Select(RewriteCell).ToArray(),
                Rows = table.Rows.Select(row => (IReadOnlyList<TableCell>)row.Select(RewriteCell).ToArray()).ToArray()
            },
            FigureBlock figure => figure with { Caption = Expand(figure.Caption) },
            _ => block
        };

    private static TableCell RewriteCell(TableCell cell)
        => cell with { Inlines = RewriteInlines(cell.Inlines) };

    private static IReadOnlyList<AstInline> RewriteInlines(IReadOnlyList<AstInline> inlines)
        => inlines.Select(RewriteInline).ToArray();

    private static AstInline RewriteInline(AstInline inline)
        => inline switch
        {
            TextInline text => text with { Text = Expand(text.Text) },
            StrongInline strong => strong with { Children = RewriteInlines(strong.Children) },
            EmphasisInline emphasis => emphasis with { Children = RewriteInlines(emphasis.Children) },
            LinkInline link => link with { Label = RewriteInlines(link.Label) },
            _ => inline
        };

    private static string Expand(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains(':', StringComparison.Ordinal)) return text;
        foreach (var pair in Shortcodes)
            text = text.Replace($":{pair.Key}:", pair.Value, StringComparison.Ordinal);
        return text;
    }

    private static readonly IReadOnlyDictionary<string, string> Shortcodes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["grinning"] = "😀",
        ["smiley"] = "😃",
        ["smile"] = "😄",
        ["joy"] = "😂",
        ["blush"] = "😊",
        ["heart_eyes"] = "😍",
        ["thinking"] = "🤔",
        ["thumbsup"] = "👍",
        ["thumbsdown"] = "👎",
        ["clap"] = "👏",
        ["pray"] = "🙏",
        ["heart"] = "❤️",
        ["bulb"] = "💡",
        ["white_check_mark"] = "✅",
        ["x"] = "❌",
        ["warning"] = "⚠️",
        ["star"] = "⭐",
        ["fire"] = "🔥",
        ["musical_note"] = "🎵",
        ["tada"] = "🎉",
        ["rocket"] = "🚀"
    };
}
