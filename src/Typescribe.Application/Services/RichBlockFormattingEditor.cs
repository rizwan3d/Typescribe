using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Applies page-layout/paragraph changes to the canonical Markdown stream. The caller addresses a
/// semantic block by its current 1-based source line (the same line stored on AstBlock). Existing
/// TypeScribe block metadata is replaced in place; otherwise one metadata line is inserted directly
/// before the authored block. Authored Markdown is never reordered or rewritten.
/// </summary>
public static class RichBlockFormattingEditor
{
    public static string Upsert(
        string markdown,
        int sourceLine,
        Func<RichBlockFormatting?, RichBlockFormatting?> update)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(update);
        if (sourceLine < 1) throw new ArgumentOutOfRangeException(nameof(sourceLine));

        var newline = DetectNewline(markdown);
        var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var hadTrailingNewline = normalized.EndsWith('\n');
        var lines = normalized.Split('\n').ToList();
        if (hadTrailingNewline && lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        var targetIndex = sourceLine - 1;
        if (targetIndex >= lines.Count)
            throw new ArgumentOutOfRangeException(nameof(sourceLine), "The source line is outside the Markdown document.");

        var metadataIndex = FindMetadataLine(lines, targetIndex);
        RichBlockFormatting? current = null;
        if (metadataIndex >= 0)
            RichMarkdownFormattingCodec.TryReadBlockMetadata(lines[metadataIndex], out current);

        var next = update(current);
        if (next is null)
        {
            if (metadataIndex >= 0) lines.RemoveAt(metadataIndex);
        }
        else
        {
            var metadata = RichMarkdownFormattingCodec.CreateBlockMetadata(next);
            if (metadataIndex >= 0)
                lines[metadataIndex] = metadata;
            else
                lines.Insert(targetIndex, metadata);
        }

        var result = string.Join(newline, lines);
        return hadTrailingNewline ? result + newline : result;
    }

    public static string SetSection(string markdown, int sourceLine, SectionFormatting? section)
        => Upsert(markdown, sourceLine, current => MergeOrRemove(current, section: section));

    public static string SetParagraph(string markdown, int sourceLine, ParagraphFormatting? paragraph)
        => Upsert(markdown, sourceLine, current => MergeOrRemove(current, paragraph: paragraph));

    public static string SetTextFrame(string markdown, int sourceLine, TextFrameFormatting? frame)
        => Upsert(markdown, sourceLine, current => MergeOrRemove(current, textFrame: frame));

    public static string SetAnchoredObject(string markdown, int sourceLine, AnchoredObjectFormatting? anchoredObject)
        => Upsert(markdown, sourceLine, current => MergeOrRemove(current, anchoredObject: anchoredObject));

    private static RichBlockFormatting? MergeOrRemove(
        RichBlockFormatting? current,
        ParagraphFormatting? paragraph = null,
        SectionFormatting? section = null,
        TextFrameFormatting? textFrame = null,
        AnchoredObjectFormatting? anchoredObject = null)
    {
        var next = new RichBlockFormatting(
            paragraph ?? current?.Paragraph,
            section ?? current?.Section,
            textFrame ?? current?.TextFrame,
            anchoredObject ?? current?.AnchoredObject);
        return IsEmpty(next) ? null : next;
    }

    public static RichBlockFormatting? ReplaceSection(RichBlockFormatting? current, SectionFormatting? section)
    {
        var next = new RichBlockFormatting(current?.Paragraph, section, current?.TextFrame, current?.AnchoredObject);
        return IsEmpty(next) ? null : next;
    }

    public static RichBlockFormatting? ReplaceParagraph(RichBlockFormatting? current, ParagraphFormatting? paragraph)
    {
        var next = new RichBlockFormatting(paragraph, current?.Section, current?.TextFrame, current?.AnchoredObject);
        return IsEmpty(next) ? null : next;
    }

    public static RichBlockFormatting? ReplaceTextFrame(RichBlockFormatting? current, TextFrameFormatting? textFrame)
    {
        var next = new RichBlockFormatting(current?.Paragraph, current?.Section, textFrame, current?.AnchoredObject);
        return IsEmpty(next) ? null : next;
    }

    public static RichBlockFormatting? ReplaceAnchoredObject(RichBlockFormatting? current, AnchoredObjectFormatting? anchoredObject)
    {
        var next = new RichBlockFormatting(current?.Paragraph, current?.Section, current?.TextFrame, anchoredObject);
        return IsEmpty(next) ? null : next;
    }

    public static bool IsEmpty(RichBlockFormatting formatting)
        => formatting.Paragraph is null &&
           formatting.Section is null &&
           formatting.TextFrame is null &&
           formatting.AnchoredObject is null;

    private static int FindMetadataLine(IReadOnlyList<string> lines, int targetIndex)
    {
        for (var index = targetIndex - 1; index >= 0; index--)
        {
            if (RichMarkdownFormattingCodec.IsBlockMetadata(lines[index])) return index;
            if (!string.IsNullOrWhiteSpace(lines[index])) break;
        }
        return -1;
    }

    private static string DetectNewline(string text)
    {
        var crlf = text.IndexOf("\r\n", StringComparison.Ordinal);
        if (crlf >= 0) return "\r\n";
        return text.IndexOf('\r') >= 0 ? "\r" : "\n";
    }
}
