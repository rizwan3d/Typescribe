using System.Text;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Converts one Markdown source line into the clean logical text shown by the native BiDi editor
/// while retaining TypeScribe character-format spans. A single contiguous edit can then be
/// projected back onto those spans and serialized as Markdown-safe inline metadata.
/// </summary>
public static class BidiMarkdownLineCodec
{
    private const string CommentEnd = " -->";

    public static bool TryParse(string source, out BidiEditableLine line)
    {
        ArgumentNullException.ThrowIfNull(source);
        var text = new StringBuilder(source.Length);
        var spans = new List<BidiInlineFormatSpan>();
        var stack = new Stack<OpenSpan>();
        var index = 0;

        while (index < source.Length)
        {
            if (source.AsSpan(index).StartsWith(RichMarkdownFormattingCodec.InlinePrefix.AsSpan(), StringComparison.Ordinal))
            {
                var end = source.IndexOf(CommentEnd, index, StringComparison.Ordinal);
                if (end < 0)
                {
                    line = new BidiEditableLine(source, []);
                    return false;
                }
                end += CommentEnd.Length;
                var marker = source[index..end];
                if (!RichMarkdownFormattingCodec.TryReadInlineOpen(marker, out var formatting) || formatting is null)
                {
                    line = new BidiEditableLine(source, []);
                    return false;
                }
                stack.Push(new OpenSpan(text.Length, formatting));
                index = end;
                continue;
            }

            if (source.AsSpan(index).StartsWith(RichMarkdownFormattingCodec.InlineClose.AsSpan(), StringComparison.Ordinal))
            {
                if (stack.Count == 0)
                {
                    line = new BidiEditableLine(source, []);
                    return false;
                }
                var open = stack.Pop();
                spans.Add(new BidiInlineFormatSpan(open.Start, text.Length, open.Formatting));
                index += RichMarkdownFormattingCodec.InlineClose.Length;
                continue;
            }

            text.Append(source[index]);
            index++;
        }

        if (stack.Count != 0)
        {
            line = new BidiEditableLine(source, []);
            return false;
        }

        line = new BidiEditableLine(text.ToString(), Normalize(spans));
        return true;
    }

    /// <summary>
    /// Rebuilds a source line after a native editor change. TextBox edits are represented as the
    /// smallest single replacement (common prefix/suffix), then formatting spans are adjusted.
    /// This preserves nested/disjoint rich spans without ever changing logical Unicode order.
    /// </summary>
    public static string ApplyEdit(BidiEditableLine original, string editedText)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(editedText);
        if (string.Equals(original.Text, editedText, StringComparison.Ordinal))
            return Serialize(original.Text, original.Spans);

        var prefix = CommonPrefix(original.Text, editedText);
        var suffix = CommonSuffix(original.Text, editedText, prefix);
        var oldEnd = original.Text.Length - suffix;
        var newEnd = editedText.Length - suffix;
        var replacementLength = Math.Max(0, newEnd - prefix);
        var transformed = new List<BidiInlineFormatSpan>(original.Spans.Count);

        foreach (var span in original.Spans)
        {
            if (TryTransformSpan(span, prefix, oldEnd, replacementLength, out var next) && next.End > next.Start)
                transformed.Add(next);
        }

        return Serialize(editedText, Normalize(transformed));
    }

    public static string Serialize(string text, IReadOnlyList<BidiInlineFormatSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(spans);
        if (spans.Count == 0) return text;

        var normalized = Normalize(spans)
            .Where(span => span.Start >= 0 && span.End <= text.Length && span.End > span.Start)
            .ToArray();
        if (normalized.Length == 0) return text;

        var output = new StringBuilder(text.Length + normalized.Length * 96);
        for (var position = 0; position <= text.Length; position++)
        {
            foreach (var span in normalized
                         .Where(span => span.End == position)
                         .OrderByDescending(static span => span.Start))
                output.Append(RichMarkdownFormattingCodec.InlineClose);

            foreach (var span in normalized
                         .Where(span => span.Start == position)
                         .OrderByDescending(static span => span.End))
                output.Append(RichMarkdownFormattingCodec.CreateInlineOpen(span.Formatting));

            if (position < text.Length) output.Append(text[position]);
        }
        return output.ToString();
    }

    private static bool TryTransformSpan(
        BidiInlineFormatSpan span,
        int editStart,
        int oldEnd,
        int replacementLength,
        out BidiInlineFormatSpan transformed)
    {
        transformed = span;
        var removedLength = oldEnd - editStart;
        var delta = replacementLength - removedLength;

        if (removedLength == 0)
        {
            if (editStart < span.Start)
            {
                transformed = span with { Start = span.Start + delta, End = span.End + delta };
                return true;
            }
            if (editStart > span.End) return true;

            // Typing at either boundary of a formatted run inherits that run's formatting.
            transformed = span with { End = span.End + delta };
            return true;
        }

        if (oldEnd <= span.Start)
        {
            transformed = span with { Start = span.Start + delta, End = span.End + delta };
            return true;
        }
        if (editStart >= span.End) return true;

        // Exact replacement of the formatted run keeps the formatting on the replacement text.
        if (editStart == span.Start && oldEnd == span.End)
        {
            transformed = span with { End = editStart + replacementLength };
            return replacementLength > 0;
        }

        // An edit wholly inside a span expands/contracts that span.
        if (editStart >= span.Start && oldEnd <= span.End)
        {
            transformed = span with { End = span.End + delta };
            return transformed.End > transformed.Start;
        }

        // The edit consumes the left edge. Keep formatting on the surviving right-hand text, but
        // do not unexpectedly style replacement text that began outside the span.
        if (editStart <= span.Start && oldEnd < span.End)
        {
            var start = editStart + replacementLength;
            transformed = span with { Start = start, End = span.End + delta };
            return transformed.End > transformed.Start;
        }

        // The edit begins inside the span and consumes its right edge. Replacement text inherits
        // the format because the insertion point was formatted.
        if (editStart > span.Start && oldEnd >= span.End)
        {
            transformed = span with { End = editStart + replacementLength };
            return transformed.End > transformed.Start;
        }

        // The edit fully covered this span as part of a wider replacement. Dropping the span is
        // safer than applying its formatting to unrelated replacement text.
        return false;
    }

    private static int CommonPrefix(string left, string right)
    {
        var length = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < length && left[index] == right[index]) index++;
        return index;
    }

    private static int CommonSuffix(string left, string right, int prefix)
    {
        var max = Math.Min(left.Length, right.Length) - prefix;
        var count = 0;
        while (count < max && left[left.Length - 1 - count] == right[right.Length - 1 - count]) count++;
        return count;
    }

    private static IReadOnlyList<BidiInlineFormatSpan> Normalize(IEnumerable<BidiInlineFormatSpan> spans)
        => spans
            .Where(static span => span.Start >= 0 && span.End >= span.Start)
            .OrderBy(static span => span.Start)
            .ThenByDescending(static span => span.End)
            .ToArray();

    private sealed record OpenSpan(int Start, CharacterFormatting Formatting);
}

public sealed record BidiEditableLine(string Text, IReadOnlyList<BidiInlineFormatSpan> Spans);
public sealed record BidiInlineFormatSpan(int Start, int End, CharacterFormatting Formatting);
