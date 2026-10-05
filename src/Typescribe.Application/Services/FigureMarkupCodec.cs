using System.Text;
using System.Text.Json;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Keeps figures readable as ordinary Markdown images while preserving Typescribe-only
/// accessibility, reusable style and layout information in an adjacent metadata comment.
/// </summary>
public static class FigureMarkupCodec
{
    public const string MetadataPrefix = "<!-- typescribe:figure ";

    public static string Serialize(FigureBlock figure, string? newline = null)
    {
        newline ??= Environment.NewLine;
        var image = BuildImageLine(figure);
        var payload = BuildPayload(figure);
        if (payload is null) return image;

        var json = JsonSerializer.Serialize(payload);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return $"{MetadataPrefix}semantic64:{encoded} -->{newline}{image}";
    }

    public static bool TryApplyMetadata(string metadataLine, FigureBlock figure, out FigureBlock result)
    {
        result = figure;
        var trimmed = metadataLine.Trim();
        if (!trimmed.StartsWith(MetadataPrefix, StringComparison.Ordinal) || !trimmed.EndsWith("-->", StringComparison.Ordinal))
            return false;

        var body = trimmed[MetadataPrefix.Length..^3].Trim();
        var token = body.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static candidate => candidate.StartsWith("semantic64:", StringComparison.Ordinal));
        if (token is null) return true;

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(token[11..]));
            var payload = JsonSerializer.Deserialize<FigureSemanticPayload>(json);
            if (payload is null) return true;
            result = figure with
            {
                AltText = Clean(payload.AltText),
                Credit = Clean(payload.Credit),
                StyleId = Clean(payload.StyleId),
                SourceKind = payload.SourceKind,
                Layout = payload.Layout
            };
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    private static string BuildImageLine(FigureBlock figure)
    {
        var caption = EscapeCaption(figure.Caption);
        var source = figure.Source.Trim();
        var id = string.IsNullOrWhiteSpace(figure.Identifier) ? string.Empty : $" {{#{figure.Identifier.Trim()}}}";
        return $"![{caption}]({source}){id}";
    }

    private static FigureSemanticPayload? BuildPayload(FigureBlock figure)
    {
        var hasSemantic = !string.IsNullOrWhiteSpace(figure.AltText) ||
                          !string.IsNullOrWhiteSpace(figure.Credit) ||
                          !string.IsNullOrWhiteSpace(figure.StyleId) ||
                          figure.SourceKind != FigureSourceKind.ProjectRelative ||
                          figure.Layout is not null;
        return hasSemantic
            ? new FigureSemanticPayload(Clean(figure.AltText), Clean(figure.Credit), Clean(figure.StyleId), figure.SourceKind, figure.Layout)
            : null;
    }

    private static string EscapeCaption(string value)
        => (value ?? string.Empty).Replace("]", "\\]", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record FigureSemanticPayload(
        string? AltText,
        string? Credit,
        string? StyleId,
        FigureSourceKind SourceKind,
        FigureLayout? Layout);
}
