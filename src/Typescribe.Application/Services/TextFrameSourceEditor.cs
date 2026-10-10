using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

public static class TextFrameSourceEditor
{
    public static AstBlock? FindFrameBlock(DocumentAst document, string frameId, int? sourceLine = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(frameId)) return null;

        if (sourceLine is int line)
        {
            var exact = document.Blocks.FirstOrDefault(block =>
                block.SourceLine == line &&
                string.Equals(block.Formatting?.TextFrame?.Id, frameId, StringComparison.Ordinal));
            if (exact is not null) return exact;
        }

        return document.Blocks.FirstOrDefault(block =>
            string.Equals(block.Formatting?.TextFrame?.Id, frameId, StringComparison.Ordinal));
    }

    public static string UpdateFrame(
        string source,
        DocumentAst document,
        string frameId,
        Func<TextFrameFormatting, TextFrameFormatting> update,
        int? sourceLine = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(update);

        var block = FindFrameBlock(document, frameId, sourceLine)
                    ?? throw new InvalidOperationException($"Text frame '{frameId}' was not found.");
        var current = block.Formatting?.TextFrame
                      ?? throw new InvalidOperationException($"Block at line {block.SourceLine} has no text frame.");

        return RichBlockFormattingEditor.SetTextFrame(
            source,
            block.SourceLine,
            update(current));
    }
}
