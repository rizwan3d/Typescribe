using System.Text.Json;
using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Extends the compact core parser with manuscript structures that authors expect in
/// long-form work while leaving the existing Markdown/parser contract intact.
/// </summary>
public sealed class AdvancedDocumentParser : IDocumentParser
{
    private const string PlaceholderPrefix = "TYPESCRIBEADVANCEDBLOCK";
    private const string TableMetadataPrefix = "<!-- typescribe:table ";
    private const string BibliographyPrefix = "<!-- typescribe:bib64:";
    private readonly DocumentParser _inner = new();

    public DocumentAst Parse(string source)
    {
        source ??= string.Empty;
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var advancedBlocks = new Dictionary<string, AstBlock>(StringComparer.Ordinal);
        var tokenNumber = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var sourceLine = index + 1;

            if (TryParseBibliographyDirective(line, sourceLine, out var bibliography))
            {
                ReplaceWithPlaceholder(lines, index, index, bibliography, advancedBlocks, ref tokenNumber);
                continue;
            }

            if (TryParseFootnoteDefinition(line, sourceLine, out var footnote))
            {
                ReplaceWithPlaceholder(lines, index, index, footnote, advancedBlocks, ref tokenNumber);
                continue;
            }

            if (TryParseFigure(line, sourceLine, out var figure))
            {
                ReplaceWithPlaceholder(lines, index, index, figure, advancedBlocks, ref tokenNumber);
                continue;
            }

            if (TryParseLabeledMath(line, sourceLine, out var equation))
            {
                ReplaceWithPlaceholder(lines, index, index, equation, advancedBlocks, ref tokenNumber);
                continue;
            }

            if (TryParseTableMetadata(line, out var metadata) &&
                index + 2 < lines.Length &&
                LooksLikeTableRow(lines[index + 1]) &&
                IsTableSeparator(lines[index + 2]))
            {
                var headerIndex = index + 1;
                var end = index + 2;
                while (end + 1 < lines.Length && LooksLikeTableRow(lines[end + 1]) && !string.IsNullOrWhiteSpace(lines[end + 1]))
                    end++;

                var table = ParseTable(lines, headerIndex, end, headerIndex + 1) with
                {
                    Caption = metadata.Caption,
                    Identifier = metadata.Identifier
                };
                ReplaceWithPlaceholder(lines, index, end, table, advancedBlocks, ref tokenNumber);
                index = end;
                continue;
            }

            if (index + 1 < lines.Length && LooksLikeTableRow(line) && IsTableSeparator(lines[index + 1]))
            {
                var end = index + 1;
                while (end + 1 < lines.Length && LooksLikeTableRow(lines[end + 1]) && !string.IsNullOrWhiteSpace(lines[end + 1]))
                    end++;

                var table = ParseTable(lines, index, end, sourceLine);
                ReplaceWithPlaceholder(lines, index, end, table, advancedBlocks, ref tokenNumber);
                index = end;
            }
        }

        var parsed = _inner.Parse(string.Join("\n", lines));
        var blocks = new List<AstBlock>(parsed.Blocks.Count);
        foreach (var block in parsed.Blocks)
        {
            if (TryResolvePlaceholder(block, advancedBlocks, out var advanced))
            {
                blocks.Add(RewriteAdvancedBlock(advanced));
                continue;
            }

            blocks.Add(RewriteBlock(block));
        }

        return new DocumentAst(blocks);
    }

    public static string CreateTableMetadata(string? identifier, string? caption)
    {
        static string Encode(string? value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty));
        return $"{TableMetadataPrefix}id64:{Encode(identifier)} caption64:{Encode(caption)} -->";
    }

    public static string CreateBibliographyDirective(BibliographyEntry entry)
    {
        var json = JsonSerializer.Serialize(entry.Normalize());
        return BibliographyPrefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)) + " -->";
    }

    private static void ReplaceWithPlaceholder(
        string[] lines,
        int start,
        int end,
        AstBlock block,
        IDictionary<string, AstBlock> blocks,
        ref int tokenNumber)
    {
        var token = PlaceholderPrefix + tokenNumber.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        tokenNumber++;
        blocks[token] = block;
        lines[start] = "###### " + token;
        for (var index = start + 1; index <= end; index++) lines[index] = string.Empty;
    }

    private static bool TryResolvePlaceholder(
        AstBlock block,
        IReadOnlyDictionary<string, AstBlock> advancedBlocks,
        out AstBlock advanced)
    {
        advanced = null!;
        if (block is not HeadingBlock { Level: 6 } heading) return false;
        var token = heading.Inlines.ToPlainText().Trim();
        return advancedBlocks.TryGetValue(token, out advanced!);
    }

    private AstBlock RewriteAdvancedBlock(AstBlock block)
        => block switch
        {
            FootnoteDefinitionBlock footnote => footnote with { Inlines = RewriteInlines(footnote.Inlines) },
            TableBlock table => table with
            {
                Header = table.Header.Select(RewriteCell).ToArray(),
                Rows = table.Rows.Select(row => (IReadOnlyList<TableCell>)row.Select(RewriteCell).ToArray()).ToArray()
            },
            _ => block
        };

    private AstBlock RewriteBlock(AstBlock block)
        => block switch
        {
            HeadingBlock heading => RewriteHeading(heading),
            ParagraphBlock paragraph => paragraph with { Inlines = RewriteInlines(paragraph.Inlines) },
            QuoteBlock quote => quote with { Inlines = RewriteInlines(quote.Inlines) },
            ListItemBlock item => item with { Inlines = RewriteInlines(item.Inlines) },
            _ => block
        };

    private HeadingBlock RewriteHeading(HeadingBlock heading)
    {
        var rewritten = RewriteInlines(heading.Inlines);
        var text = rewritten.ToPlainText();
        if (!TryExtractTrailingIdentifier(text, out var clean, out var identifier))
            return heading with { Inlines = rewritten };

        // Identifier suffixes are structural, not visible heading content. Rebuilding the heading
        // text is intentionally conservative and keeps canonical storage simple and deterministic.
        return heading with { Inlines = [new TextInline(clean)], Identifier = identifier };
    }

    private TableCell RewriteCell(TableCell cell) => cell with { Inlines = RewriteInlines(cell.Inlines) };

    private IReadOnlyList<AstInline> RewriteInlines(IReadOnlyList<AstInline> inlines)
    {
        var rewritten = new List<AstInline>(inlines.Count);
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextInline text:
                    rewritten.AddRange(ParseSemanticReferences(text.Text));
                    break;
                case StrongInline strong:
                    rewritten.Add(strong with { Children = RewriteInlines(strong.Children) });
                    break;
                case EmphasisInline emphasis:
                    rewritten.Add(emphasis with { Children = RewriteInlines(emphasis.Children) });
                    break;
                case LinkInline link:
                    rewritten.Add(link with { Label = RewriteInlines(link.Label) });
                    break;
                default:
                    rewritten.Add(inline);
                    break;
            }
        }
        return rewritten;
    }

    private static IEnumerable<AstInline> ParseSemanticReferences(string text)
    {
        var result = new List<AstInline>();
        var cursor = 0;

        while (cursor < text.Length)
        {
            var open = FindReferenceStart(text, cursor);
            if (open < 0)
            {
                if (cursor < text.Length) result.Add(new TextInline(text[cursor..]));
                break;
            }

            if (open > cursor) result.Add(new TextInline(text[cursor..open]));
            var close = text.IndexOf(']', open + 2);
            if (close < 0)
            {
                result.Add(new TextInline(text[open..]));
                break;
            }

            var marker = text[open + 1];
            var body = text[(open + 2)..close].Trim();
            if (marker == '^' && body.Length > 0)
            {
                result.Add(new FootnoteReferenceInline(body));
                cursor = close + 1;
                continue;
            }

            if (marker == '@' && body.Length > 0)
            {
                if (body.StartsWith("ref:", StringComparison.OrdinalIgnoreCase))
                {
                    var identifier = body[4..].Trim();
                    if (identifier.Length > 0)
                    {
                        result.Add(new CrossReferenceInline(identifier));
                        cursor = close + 1;
                        continue;
                    }
                }

                var comma = body.IndexOf(',');
                var key = comma >= 0 ? body[..comma].Trim() : body;
                var locator = comma >= 0 ? body[(comma + 1)..].Trim() : null;
                if (key.Length > 0)
                {
                    result.Add(new CitationInline(key, string.IsNullOrWhiteSpace(locator) ? null : locator));
                    cursor = close + 1;
                    continue;
                }
            }

            result.Add(new TextInline(text[open..(close + 1)]));
            cursor = close + 1;
        }

        if (text.Length == 0) result.Add(new TextInline(string.Empty));
        return result;
    }

    private static int FindReferenceStart(string text, int start)
    {
        for (var index = start; index + 1 < text.Length; index++)
        {
            if (text[index] == '[' && text[index + 1] is '^' or '@') return index;
        }
        return -1;
    }

    private bool TryParseFootnoteDefinition(string line, int sourceLine, out FootnoteDefinitionBlock block)
    {
        block = null!;
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("[^", StringComparison.Ordinal)) return false;
        var close = trimmed.IndexOf("]: ", StringComparison.Ordinal);
        var contentStart = 0;
        if (close >= 0)
        {
            contentStart = close + 3;
        }
        else
        {
            close = trimmed.IndexOf("]:", StringComparison.Ordinal);
            if (close < 3) return false;
            contentStart = close + 2;
        }

        var identifier = trimmed[2..close].Trim();
        if (identifier.Length == 0) return false;
        var body = trimmed[contentStart..].TrimStart();
        block = new FootnoteDefinitionBlock(sourceLine, identifier, ParseCellInlines(body));
        return true;
    }

    private static bool TryParseFigure(string line, int sourceLine, out FigureBlock block)
    {
        block = null!;
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("![", StringComparison.Ordinal)) return false;

        var captionEnd = trimmed.IndexOf("](", StringComparison.Ordinal);
        if (captionEnd <= 2) return false;
        var sourceEnd = trimmed.IndexOf(')', captionEnd + 2);
        if (sourceEnd <= captionEnd + 2) return false;

        var caption = trimmed[2..captionEnd].Trim();
        var source = trimmed[(captionEnd + 2)..sourceEnd].Trim();
        if (source.Length == 0) return false;

        string? identifier = null;
        var suffix = trimmed[(sourceEnd + 1)..].Trim();
        if (suffix.StartsWith("{#", StringComparison.Ordinal) && suffix.EndsWith('}') && suffix.Length > 3)
            identifier = suffix[2..^1].Trim();

        block = new FigureBlock(sourceLine, source, caption, string.IsNullOrWhiteSpace(identifier) ? null : identifier);
        return true;
    }

    private static bool TryParseLabeledMath(string line, int sourceLine, out DisplayMathBlock block)
    {
        block = null!;
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("$$", StringComparison.Ordinal) || !trimmed.EndsWith('}')) return false;
        var labelStart = trimmed.LastIndexOf(" {#", StringComparison.Ordinal);
        if (labelStart <= 4) return false;
        var mathPart = trimmed[..labelStart].TrimEnd();
        if (!mathPart.EndsWith("$$", StringComparison.Ordinal)) return false;
        var identifier = trimmed[(labelStart + 3)..^1].Trim();
        if (identifier.Length == 0) return false;
        var text = mathPart[2..^2].Trim();
        block = new DisplayMathBlock(sourceLine, text, identifier);
        return true;
    }

    private TableBlock ParseTable(string[] lines, int start, int end, int sourceLine)
    {
        var header = ParseTableCells(lines[start]).Select(cell => new TableCell(ParseCellInlines(cell))).ToArray();
        var alignments = ParseTableAlignments(lines[start + 1]);
        var rows = new List<IReadOnlyList<TableCell>>();

        for (var index = start + 2; index <= end; index++)
        {
            var cells = ParseTableCells(lines[index]);
            if (cells.Count == 0) continue;
            var row = new List<TableCell>(Math.Max(header.Length, cells.Count));
            for (var column = 0; column < Math.Max(header.Length, cells.Count); column++)
            {
                var value = column < cells.Count ? cells[column] : string.Empty;
                row.Add(new TableCell(ParseCellInlines(value)));
            }
            rows.Add(row);
        }

        return new TableBlock(sourceLine, header, rows, alignments);
    }

    private IReadOnlyList<AstInline> ParseCellInlines(string text)
    {
        if (string.IsNullOrEmpty(text)) return [new TextInline(string.Empty)];
        var parsed = _inner.Parse(text);
        IReadOnlyList<AstInline> inlines = parsed.Blocks.FirstOrDefault() switch
        {
            ParagraphBlock paragraph => paragraph.Inlines,
            HeadingBlock heading => heading.Inlines,
            QuoteBlock quote => quote.Inlines,
            ListItemBlock item => item.Inlines,
            _ => [new TextInline(text)]
        };
        return RewriteInlines(inlines);
    }

    private static bool LooksLikeTableRow(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 3 && trimmed.Contains('|');
    }

    private static bool IsTableSeparator(string line)
    {
        var cells = ParseTableCells(line);
        if (cells.Count == 0) return false;
        foreach (var cell in cells)
        {
            var value = cell.Trim();
            if (value.StartsWith(':')) value = value[1..];
            if (value.EndsWith(':')) value = value[..^1];
            if (value.Length < 3 || value.Any(character => character != '-')) return false;
        }
        return true;
    }

    private static IReadOnlyList<TableAlignment> ParseTableAlignments(string line)
        => ParseTableCells(line).Select(static cell =>
        {
            var value = cell.Trim();
            var left = value.StartsWith(':');
            var right = value.EndsWith(':');
            return left && right ? TableAlignment.Center
                : right ? TableAlignment.Right
                : left ? TableAlignment.Left
                : TableAlignment.Default;
        }).ToArray();

    private static IReadOnlyList<string> ParseTableCells(string line)
    {
        var text = line.Trim();
        if (text.StartsWith('|')) text = text[1..];
        if (text.EndsWith('|')) text = text[..^1];

        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var escaped = false;
        foreach (var character in text)
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
                continue;
            }
            if (character == '\\')
            {
                escaped = true;
                continue;
            }
            if (character == '|')
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }
            current.Append(character);
        }
        if (escaped) current.Append('\\');
        cells.Add(current.ToString().Trim());
        return cells;
    }

    private static bool TryExtractTrailingIdentifier(string text, out string clean, out string identifier)
    {
        clean = text;
        identifier = string.Empty;
        var trimmed = text.TrimEnd();
        if (!trimmed.EndsWith('}')) return false;
        var start = trimmed.LastIndexOf(" {#", StringComparison.Ordinal);
        if (start < 0) return false;
        identifier = trimmed[(start + 3)..^1].Trim();
        if (identifier.Length == 0) return false;
        clean = trimmed[..start].TrimEnd();
        return true;
    }

    private static bool TryParseTableMetadata(string line, out TableMetadata metadata)
    {
        metadata = new TableMetadata(null, null);
        var trimmed = line.Trim();
        if (!trimmed.StartsWith(TableMetadataPrefix, StringComparison.Ordinal) || !trimmed.EndsWith("-->", StringComparison.Ordinal))
            return false;
        var body = trimmed[TableMetadataPrefix.Length..^3].Trim();
        string? identifier = null;
        string? caption = null;
        foreach (var token in body.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith("id64:", StringComparison.Ordinal)) identifier = Decode(token[5..]);
            else if (token.StartsWith("caption64:", StringComparison.Ordinal)) caption = Decode(token[10..]);
        }
        metadata = new TableMetadata(string.IsNullOrWhiteSpace(identifier) ? null : identifier, string.IsNullOrWhiteSpace(caption) ? null : caption);
        return true;
    }

    private static bool TryParseBibliographyDirective(string line, int sourceLine, out BibliographyEntryBlock block)
    {
        block = null!;
        var trimmed = line.Trim();
        if (!trimmed.StartsWith(BibliographyPrefix, StringComparison.Ordinal) || !trimmed.EndsWith("-->", StringComparison.Ordinal))
            return false;
        var encoded = trimmed[BibliographyPrefix.Length..^3].Trim();
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var entry = JsonSerializer.Deserialize<BibliographyEntry>(json);
            if (entry is null || string.IsNullOrWhiteSpace(entry.CitationKey)) return false;
            block = new BibliographyEntryBlock(sourceLine, entry.Normalize());
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    private static string Decode(string encoded)
    {
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded)); }
        catch (FormatException) { return string.Empty; }
    }

    private sealed record TableMetadata(string? Identifier, string? Caption);
}
