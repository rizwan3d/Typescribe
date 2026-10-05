using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Markdown-compatible persistence for rich-document formatting.
///
/// Block/section/layout metadata is stored in an ignorable HTML comment immediately before the
/// Markdown block it decorates. Selection/character formatting uses paired ignorable comments,
/// leaving the selected Markdown text intact and readable in other editors.
///
/// Examples:
///   <!-- typescribe:block64:... -->
///   A paragraph that remains ordinary Markdown.
///
///   <!-- typescribe:inline64:... -->متن<!-- /typescribe:inline -->
///
/// Consumers that do not understand TypeScribe metadata simply ignore the comments.
/// </summary>
public static class RichMarkdownFormattingCodec
{
    public const string BlockPrefix = "<!-- typescribe:block64:";
    public const string InlinePrefix = "<!-- typescribe:inline64:";
    public const string InlineClose = "<!-- /typescribe:inline -->";
    private const string CommentSuffix = " -->";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string CreateBlockMetadata(RichBlockFormatting formatting)
    {
        ArgumentNullException.ThrowIfNull(formatting);
        return EncodeComment(BlockPrefix, formatting);
    }

    public static string CreateInlineOpen(CharacterFormatting formatting)
    {
        ArgumentNullException.ThrowIfNull(formatting);
        return EncodeComment(InlinePrefix, formatting);
    }

    public static string WrapInline(string markdown, CharacterFormatting formatting)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return CreateInlineOpen(formatting) + markdown + InlineClose;
    }

    public static bool TryReadBlockMetadata(string? line, out RichBlockFormatting? formatting)
        => TryDecodeComment(line, BlockPrefix, out formatting);

    public static bool TryReadInlineOpen(string? text, out CharacterFormatting? formatting)
        => TryDecodeComment(text, InlinePrefix, out formatting);

    public static bool IsBlockMetadata(string? line)
        => IsEncodedComment(line, BlockPrefix);

    public static bool IsInlineOpen(string? text)
        => IsEncodedComment(text, InlinePrefix);

    public static bool IsInlineClose(string? text)
        => string.Equals(text?.Trim(), InlineClose, StringComparison.Ordinal);

    /// <summary>
    /// Returns the Markdown text without TypeScribe formatting comments. This is useful for
    /// clipboard/plain-text export; it intentionally leaves all non-TypeScribe HTML comments.
    /// </summary>
    public static string StripFormattingMetadata(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown ?? string.Empty;
        var result = new StringBuilder(markdown.Length);
        var index = 0;
        while (index < markdown.Length)
        {
            var nextBlock = markdown.IndexOf(BlockPrefix, index, StringComparison.Ordinal);
            var nextInline = markdown.IndexOf(InlinePrefix, index, StringComparison.Ordinal);
            var nextClose = markdown.IndexOf(InlineClose, index, StringComparison.Ordinal);
            var next = SmallestPositive(nextBlock, nextInline, nextClose);
            if (next < 0)
            {
                result.Append(markdown, index, markdown.Length - index);
                break;
            }

            result.Append(markdown, index, next - index);
            if (next == nextClose)
            {
                index = next + InlineClose.Length;
                continue;
            }

            var end = markdown.IndexOf(CommentSuffix, next, StringComparison.Ordinal);
            if (end < 0)
            {
                // Malformed metadata should remain visible rather than causing data loss.
                result.Append(markdown, next, markdown.Length - next);
                break;
            }
            index = end + CommentSuffix.Length;
        }
        return result.ToString();
    }

    private static string EncodeComment<T>(string prefix, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return prefix + encoded + CommentSuffix;
    }

    private static bool TryDecodeComment<T>(string? text, string prefix, out T? value)
    {
        value = default;
        if (!TryGetEncodedPayload(text, prefix, out var payload)) return false;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return value is not null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            value = default;
            return false;
        }
    }

    private static bool IsEncodedComment(string? text, string prefix)
        => TryGetEncodedPayload(text, prefix, out _);

    private static bool TryGetEncodedPayload(string? text, string prefix, out string payload)
    {
        payload = string.Empty;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) ||
            !trimmed.StartsWith(prefix, StringComparison.Ordinal) ||
            !trimmed.EndsWith(CommentSuffix, StringComparison.Ordinal))
            return false;

        payload = trimmed[prefix.Length..^CommentSuffix.Length].Trim();
        return payload.Length > 0;
    }

    private static int SmallestPositive(params int[] values)
    {
        var smallest = int.MaxValue;
        foreach (var value in values)
            if (value >= 0 && value < smallest) smallest = value;
        return smallest == int.MaxValue ? -1 : smallest;
    }
}
