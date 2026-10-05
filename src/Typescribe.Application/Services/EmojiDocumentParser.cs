using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Expands a compact, deterministic set of Markdown-style emoji shortcodes in text inlines.
/// Canonical manuscript source remains unchanged; expansion happens only in the semantic AST.
/// This outer semantic layer also reapplies Typescribe table/figure metadata stored beside
/// human-readable Markdown.
/// </summary>
public sealed class EmojiDocumentParser : IDocumentParser
{
    private readonly IDocumentParser _inner;

    public EmojiDocumentParser(IDocumentParser inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        // Rich metadata is a semantic extension of Markdown, so every standard Emoji+Advanced
        // parser composition gets it automatically without requiring dozens of desktop call sites
        // to opt into a second document model.
        _inner = inner is RichDocumentParser ? inner : new RichDocumentParser(inner);
    }

    public DocumentAst Parse(string source)
    {
        source ??= string.Empty;
        var parsed = _inner.Parse(source);
        var metadata = ReadMetadata(source);
        var blocks = new List<AstBlock>(parsed.Blocks.Count);
        for (var index = 0; index < parsed.Blocks.Count; index++)
        {
            var original = parsed.Blocks[index];
            if (metadata.MetadataCommentLines.Contains(original.SourceLine) && original is ParagraphBlock)
                continue;

            var rewritten = RewriteBlock(original);
            if (rewritten is TableBlock table)
            {
                if (metadata.TableMetadataByHeaderLine.TryGetValue(table.SourceLine, out var tableLine))
                {
                    table = TableMarkupCodec.ApplyMetadata(tableLine, table);
                    if (TableMarkupCodec.TryReadSpans(tableLine, out var spans) && spans.Count > 0)
                        table = TableMarkupCodec.ApplySpans(table, spans);
                }
                rewritten = table;
            }
            else if (rewritten is FigureBlock figure &&
                     metadata.FigureMetadataByImageLine.TryGetValue(figure.SourceLine, out var figureLine) &&
                     FigureMarkupCodec.TryApplyMetadata(figureLine, figure, out var enriched))
            {
                rewritten = enriched;
            }

            blocks.Add(rewritten);
        }
        return new DocumentAst(blocks);
    }

    private static SemanticMetadata ReadMetadata(string source)
    {
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var tables = new Dictionary<int, string>();
        var figures = new Dictionary<int, string>();
        var commentLines = new HashSet<int>();

        for (var index = 0; index < lines.Length - 1; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (trimmed.StartsWith(TableMarkupCodec.MetadataPrefix, StringComparison.Ordinal))
            {
                var next = index + 1;
                while (next < lines.Length && string.IsNullOrWhiteSpace(lines[next])) next++;
                if (next < lines.Length && lines[next].Contains('|')) tables[next + 1] = line;
                continue;
            }

            if (!trimmed.StartsWith(FigureMarkupCodec.MetadataPrefix, StringComparison.Ordinal)) continue;
            var imageLine = index + 1;
            while (imageLine < lines.Length && string.IsNullOrWhiteSpace(lines[imageLine])) imageLine++;
            if (imageLine >= lines.Length || !lines[imageLine].TrimStart().StartsWith("![", StringComparison.Ordinal)) continue;
            figures[imageLine + 1] = line;
            commentLines.Add(index + 1);
        }

        return new SemanticMetadata(tables, figures, commentLines);
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
            RichSpanInline rich => rich with { Children = RewriteInlines(rich.Children) },
            _ => inline
        };

    private static string Expand(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains(':', StringComparison.Ordinal)) return text;
        foreach (var pair in Shortcodes)
            text = text.Replace($":{pair.Key}:", pair.Value, StringComparison.Ordinal);
        return text;
    }

    private sealed record SemanticMetadata(
        IReadOnlyDictionary<int, string> TableMetadataByHeaderLine,
        IReadOnlyDictionary<int, string> FigureMetadataByImageLine,
        IReadOnlySet<int> MetadataCommentLines);

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