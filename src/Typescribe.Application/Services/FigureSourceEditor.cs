using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Source-safe figure mutation helpers shared by visual page editing and the figure manager.
/// Figure semantics remain ordinary Markdown image syntax plus the adjacent TypeScribe figure
/// metadata comment; unrelated rich block metadata is preserved.
/// </summary>
public static class FigureSourceEditor
{
    public static string ReplaceFigure(string source, FigureBlock figure, FigureBlock updated)
        => ReplaceFigure(source, figure, FigureMarkupCodec.Serialize(updated, DetectNewline(source)));

    public static string ReplaceFigure(string source, FigureBlock figure, string replacement)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(figure);
        ArgumentNullException.ThrowIfNull(replacement);

        var newline = DetectNewline(source);
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var hadTrailingNewline = normalized.EndsWith('\n');
        var lines = normalized.Split('\n').ToList();
        if (hadTrailingNewline && lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        var image = Math.Clamp(figure.SourceLine - 1, 0, Math.Max(0, lines.Count - 1));
        var start = image > 0 && lines[image - 1].TrimStart().StartsWith(FigureMarkupCodec.MetadataPrefix, StringComparison.Ordinal)
            ? image - 1
            : image;

        lines.RemoveRange(start, image - start + 1);
        lines.InsertRange(
            start,
            replacement.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'));

        var result = string.Join(newline, lines);
        return hadTrailingNewline ? result + newline : result;
    }

    public static FigureBlock? FindFigure(
        DocumentAst document,
        int sourceLine,
        string? identifier = null,
        string? source = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!string.IsNullOrWhiteSpace(identifier))
        {
            var byId = document.Blocks.OfType<FigureBlock>()
                .FirstOrDefault(candidate => string.Equals(candidate.Identifier, identifier, StringComparison.Ordinal));
            if (byId is not null) return byId;
        }

        var exact = document.Blocks.OfType<FigureBlock>()
            .FirstOrDefault(candidate => candidate.SourceLine == sourceLine);
        if (exact is not null) return exact;

        if (!string.IsNullOrWhiteSpace(source))
        {
            return document.Blocks.OfType<FigureBlock>()
                .OrderBy(candidate => Math.Abs(candidate.SourceLine - sourceLine))
                .FirstOrDefault(candidate => string.Equals(candidate.Source, source, StringComparison.Ordinal));
        }

        return null;
    }

    public static string DetectNewline(string source)
        => source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" :
           source.Contains('\r') ? "\r" : "\n";
}
