using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Maps deterministic paged-layout fragments back to the canonical Markdown source.
/// The page canvas edits these spans; pagination remains a projection and never becomes
/// a second document model.
/// </summary>
public static class PagedLayoutSourceMapper
{
    public sealed record EditableSpan(
        int SourceBlockIndex,
        int StartLine,
        int EndLine,
        int StartOffset,
        int Length,
        string Text);

    public static EditableSpan GetEditableSpan(
        string source,
        DocumentAst document,
        int sourceBlockIndex)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(document);

        if (sourceBlockIndex < 0 || sourceBlockIndex >= document.Blocks.Count)
            throw new ArgumentOutOfRangeException(nameof(sourceBlockIndex));

        var block = document.Blocks[sourceBlockIndex];
        var index = LineIndex.Create(source);
        var startLine = Math.Clamp(block.SourceLine, 1, index.LineCount);

        var nextLine = document.Blocks
            .Select(static candidate => candidate.SourceLine)
            .Where(line => line > startLine)
            .DefaultIfEmpty(index.LineCount + 1)
            .Min();
        nextLine = Math.Clamp(nextLine, startLine + 1, index.LineCount + 1);

        var lastCandidateLine = Math.Min(index.LineCount, nextLine - 1);
        var endLine = lastCandidateLine;
        while (endLine > startLine && string.IsNullOrWhiteSpace(index.LineText(source, endLine)))
            endLine--;

        var startOffset = index.LineStart(startLine);
        var endOffset = index.LineContentEnd(source, endLine);
        if (endOffset < startOffset) endOffset = startOffset;

        return new EditableSpan(
            sourceBlockIndex,
            startLine,
            endLine,
            startOffset,
            endOffset - startOffset,
            source[startOffset..endOffset]);
    }

    public static string ReplaceBlock(
        string source,
        DocumentAst document,
        int sourceBlockIndex,
        string replacement)
    {
        var span = GetEditableSpan(source, document, sourceBlockIndex);
        replacement ??= string.Empty;
        return string.Concat(
            source.AsSpan(0, span.StartOffset),
            replacement,
            source.AsSpan(span.StartOffset + span.Length));
    }

    private sealed class LineIndex
    {
        private readonly int[] _starts;

        private LineIndex(int[] starts)
            => _starts = starts;

        public int LineCount => _starts.Length;

        public static LineIndex Create(string source)
        {
            var starts = new List<int> { 0 };
            for (var index = 0; index < source.Length; index++)
            {
                if (source[index] != '\n') continue;
                if (index + 1 <= source.Length)
                    starts.Add(index + 1);
            }

            // A trailing newline does not create an addressable authored line for source mapping.
            if (starts.Count > 1 && starts[^1] == source.Length)
                starts.RemoveAt(starts.Count - 1);

            return new LineIndex(starts.ToArray());
        }

        public int LineStart(int oneBasedLine)
            => _starts[Math.Clamp(oneBasedLine - 1, 0, _starts.Length - 1)];

        public string LineText(string source, int oneBasedLine)
        {
            var start = LineStart(oneBasedLine);
            var end = oneBasedLine < LineCount ? LineStart(oneBasedLine + 1) : source.Length;
            while (end > start && source[end - 1] is '\r' or '\n') end--;
            return source[start..end];
        }

        public int LineContentEnd(string source, int oneBasedLine)
        {
            var start = LineStart(oneBasedLine);
            var end = oneBasedLine < LineCount ? LineStart(oneBasedLine + 1) : source.Length;
            while (end > start && source[end - 1] is '\r' or '\n') end--;
            return end;
        }
    }
}
