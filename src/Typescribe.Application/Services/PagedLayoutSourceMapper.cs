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
        return source[..span.StartOffset] +
               replacement +
               source[(span.StartOffset + span.Length)..];
    }

    /// <summary>
    /// Resolves an offset in the plain text consumed by <see cref="PagedLayoutEngine"/> back
    /// to the corresponding canonical Markdown source offset. Markdown punctuation is skipped
    /// while authored characters remain addressable, allowing page continuations inside one
    /// paragraph to keep one global caret/selection model.
    /// </summary>
    public static int GetSourceOffsetForPlainText(
        string source,
        DocumentAst document,
        int sourceBlockIndex,
        int plainTextOffset)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(document);

        var span = GetEditableSpan(source, document, sourceBlockIndex);
        var block = document.Blocks[sourceBlockIndex];
        var raw = span.Text;
        var plain = PlainTextFor(block);
        var target = Math.Clamp(plainTextOffset, 0, plain.Length);

        if (target <= 0)
            return span.StartOffset + ContentStart(raw, block);
        if (target >= plain.Length)
            return span.StartOffset + span.Length;

        var rawStart = ContentStart(raw, block);

        if (block is ListItemBlock item)
        {
            var prefix = item.Ordered
                ? $"{item.Number ?? 1}. "
                : "• ";
            if (target < prefix.Length)
                return span.StartOffset + rawStart;

            target -= prefix.Length;
            plain = item.Inlines.ToPlainText();
            if (target <= 0)
                return span.StartOffset + rawStart;
            if (target >= plain.Length)
                return span.StartOffset + span.Length;
        }

        var rawIndex = rawStart;
        for (var plainIndex = 0; plainIndex < target && rawIndex < raw.Length; plainIndex++)
        {
            var found = FindEquivalent(raw, rawIndex, plain[plainIndex]);
            if (found < 0)
                return ProportionalFallback(span, target, Math.Max(1, plain.Length));
            rawIndex = found + 1;
        }

        var targetIndex = target < plain.Length
            ? FindEquivalent(raw, rawIndex, plain[target])
            : raw.Length;
        if (targetIndex < 0)
            return ProportionalFallback(span, target, Math.Max(1, plain.Length));

        return Math.Clamp(span.StartOffset + targetIndex, span.StartOffset, span.StartOffset + span.Length);
    }

    public static string PlainTextFor(AstBlock block)
        => block switch
        {
            HeadingBlock heading => heading.Inlines.ToPlainText(),
            ParagraphBlock paragraph => paragraph.Inlines.ToPlainText(),
            QuoteBlock quote => quote.Inlines.ToPlainText(),
            ListItemBlock item => (item.Ordered ? $"{item.Number ?? 1}. " : "• ") + item.Inlines.ToPlainText(),
            CodeBlock code => code.Text,
            DisplayMathBlock math => math.Text,
            FigureBlock figure => figure.Caption,
            _ => string.Empty
        };

    private static int FindEquivalent(string raw, int start, char expected)
    {
        var expectedWhitespace = char.IsWhiteSpace(expected);
        for (var index = Math.Clamp(start, 0, raw.Length); index < raw.Length; index++)
        {
            var candidate = raw[index];
            if (expectedWhitespace)
            {
                if (char.IsWhiteSpace(candidate)) return index;
                continue;
            }

            if (candidate == expected) return index;
        }

        return -1;
    }

    private static int ContentStart(string raw, AstBlock block)
    {
        if (raw.Length == 0) return 0;

        var index = 0;
        while (index < raw.Length && char.IsWhiteSpace(raw[index]) && raw[index] is not '\r' and not '\n')
            index++;

        if (block is HeadingBlock)
        {
            while (index < raw.Length && raw[index] == '#') index++;
            while (index < raw.Length && raw[index] is ' ' or '\t') index++;
            return index;
        }

        if (block is QuoteBlock)
        {
            if (index < raw.Length && raw[index] == '>') index++;
            while (index < raw.Length && raw[index] is ' ' or '\t') index++;
            return index;
        }

        if (block is ListItemBlock)
        {
            if (index < raw.Length && raw[index] is '-' or '*' or '+')
            {
                index++;
            }
            else
            {
                while (index < raw.Length && char.IsDigit(raw[index])) index++;
                if (index < raw.Length && raw[index] == '.') index++;
            }
            while (index < raw.Length && raw[index] is ' ' or '\t') index++;
            return index;
        }

        if (block is CodeBlock && raw.AsSpan(index).StartsWith("```".AsSpan(), StringComparison.Ordinal))
        {
            var newline = raw.IndexOf('\n', index);
            return newline >= 0 ? Math.Min(raw.Length, newline + 1) : index;
        }

        if (block is DisplayMathBlock && raw.AsSpan(index).StartsWith("$".AsSpan(), StringComparison.Ordinal))
        {
            index += 2;
            while (index < raw.Length && raw[index] is ' ' or '\t') index++;
            if (index < raw.Length && raw[index] is '\r' or '\n')
            {
                while (index < raw.Length && raw[index] is '\r' or '\n') index++;
            }
            return index;
        }

        return index;
    }

    private static int ProportionalFallback(EditableSpan span, int plainOffset, int plainLength)
    {
        var ratio = Math.Clamp((double)plainOffset / plainLength, 0, 1);
        return span.StartOffset + (int)Math.Round(span.Length * ratio);
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
