using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Adds Typescribe merged-cell metadata to any existing document parser without changing
/// the normal Markdown parser contract. The wrapped parser still owns all syntax; this layer
/// only reapplies table span geometry recorded in the table metadata comment.
/// </summary>
public sealed class MergedTableDocumentParser(IDocumentParser inner) : IDocumentParser
{
    private readonly IDocumentParser _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public DocumentAst Parse(string source)
    {
        source ??= string.Empty;
        var parsed = _inner.Parse(source);
        var spansByHeaderLine = ReadSpanMetadata(source);
        if (spansByHeaderLine.Count == 0) return parsed;

        var changed = false;
        var blocks = new AstBlock[parsed.Blocks.Count];
        for (var index = 0; index < parsed.Blocks.Count; index++)
        {
            var block = parsed.Blocks[index];
            if (block is TableBlock table && spansByHeaderLine.TryGetValue(table.SourceLine, out var spans) && spans.Count > 0)
            {
                blocks[index] = TableMarkupCodec.ApplySpans(table, spans);
                changed = true;
            }
            else
            {
                blocks[index] = block;
            }
        }

        return changed ? new DocumentAst(blocks) : parsed;
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
}
