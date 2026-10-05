using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Enriches the existing Markdown parser with Typescribe layout metadata while keeping
/// the underlying canonical text human-readable. This wrapper intentionally sits outside
/// AdvancedDocumentParser/EmojiDocumentParser so legacy parsing remains backward compatible.
/// </summary>
public sealed class SemanticLayoutDocumentParser(IDocumentParser inner) : IDocumentParser
{
    public DocumentAst Parse(string source)
    {
        source ??= string.Empty;
        var parsed = inner.Parse(source);
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var tableMetadata = new Dictionary<int, string>();
        var figureMetadata = new Dictionary<int, string>();
        var metadataLines = new HashSet<int>();

        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith(TableMarkupCodec.MetadataPrefix, StringComparison.Ordinal) &&
                index + 1 < lines.Length)
            {
                tableMetadata[index + 2] = lines[index];
                metadataLines.Add(index + 1);
                continue;
            }

            if (trimmed.StartsWith(FigureMarkupCodec.MetadataPrefix, StringComparison.Ordinal) &&
                index + 1 < lines.Length &&
                lines[index + 1].TrimStart().StartsWith("![", StringComparison.Ordinal))
            {
                figureMetadata[index + 2] = lines[index];
                metadataLines.Add(index + 1);
            }
        }

        if (tableMetadata.Count == 0 && figureMetadata.Count == 0) return parsed;

        var blocks = new List<AstBlock>(parsed.Blocks.Count);
        foreach (var block in parsed.Blocks)
        {
            if (metadataLines.Contains(block.SourceLine) && block is ParagraphBlock)
                continue;

            if (block is TableBlock table && tableMetadata.TryGetValue(table.SourceLine, out var tableLine))
            {
                blocks.Add(TableMarkupCodec.ApplyMetadata(tableLine, table));
                continue;
            }

            if (block is FigureBlock figure && figureMetadata.TryGetValue(figure.SourceLine, out var figureLine) &&
                FigureMarkupCodec.TryApplyMetadata(figureLine, figure, out var enriched))
            {
                blocks.Add(enriched);
                continue;
            }

            blocks.Add(block);
        }

        return new DocumentAst(blocks);
    }
}
