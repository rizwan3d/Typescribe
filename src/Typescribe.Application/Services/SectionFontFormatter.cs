using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

public sealed record SectionFontRange(
    int StartLine,
    int EndLine,
    string Label,
    int TextBlockCount);

/// <summary>
/// Applies a font to the current Markdown heading section using the existing paragraph metadata.
/// A section starts at the nearest heading at or before the caret and ends immediately before the
/// next heading at the same or a higher level. Applying from the bottom upward keeps source-line
/// addresses stable while metadata lines are inserted.
/// </summary>
public static class SectionFontFormatter
{
    public static SectionFontRange ResolveSection(
        IDocumentParser parser,
        string markdown,
        int sourceLine)
    {
        ArgumentNullException.ThrowIfNull(parser);
        markdown ??= string.Empty;

        var document = parser.Parse(markdown);
        var totalLines = CountLines(markdown);
        var line = Math.Clamp(sourceLine, 1, totalLines);
        var headings = document.Blocks
            .OfType<HeadingBlock>()
            .OrderBy(static heading => heading.SourceLine)
            .ToArray();

        var current = headings.LastOrDefault(heading => heading.SourceLine <= line);
        int startLine;
        int endLine;
        string label;

        if (current is null)
        {
            startLine = 1;
            endLine = headings.FirstOrDefault()?.SourceLine - 1 ?? totalLines;
            label = "Document opening";
        }
        else
        {
            startLine = current.SourceLine;
            var next = headings.FirstOrDefault(heading =>
                heading.SourceLine > current.SourceLine && heading.Level <= current.Level);
            endLine = next is null ? totalLines : Math.Max(startLine, next.SourceLine - 1);
            label = current.Inlines.ToPlainText();
            if (string.IsNullOrWhiteSpace(label)) label = $"Heading at line {current.SourceLine}";
        }

        var count = document.Blocks.Count(block =>
            block.SourceLine >= startLine &&
            block.SourceLine <= endLine &&
            SupportsParagraphFormatting(block));

        return new SectionFontRange(startLine, endLine, label, count);
    }

    public static string Apply(
        IDocumentParser parser,
        string markdown,
        int sourceLine,
        FontReference font,
        double? fontSizePoints = null)
    {
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(font);
        markdown ??= string.Empty;

        if (fontSizePoints is <= 0) fontSizePoints = null;
        var range = ResolveSection(parser, markdown, sourceLine);
        var document = parser.Parse(markdown);
        var blockLines = document.Blocks
            .Where(block => block.SourceLine >= range.StartLine &&
                            block.SourceLine <= range.EndLine &&
                            SupportsParagraphFormatting(block))
            .Select(static block => block.SourceLine)
            .Distinct()
            .OrderByDescending(static line => line)
            .ToArray();

        var updated = markdown;
        foreach (var line in blockLines)
        {
            updated = RichBlockFormattingEditor.Upsert(updated, line, current =>
            {
                var paragraph = current?.Paragraph ?? new ParagraphFormatting();
                var characters = paragraph.CharacterDefaults ?? new CharacterFormatting();
                characters = characters with
                {
                    Font = font,
                    FontSizePoints = fontSizePoints ?? characters.FontSizePoints
                };
                paragraph = paragraph with { CharacterDefaults = characters };
                return RichBlockFormattingEditor.ReplaceParagraph(current, paragraph);
            });
        }

        return updated;
    }

    public static string Clear(
        IDocumentParser parser,
        string markdown,
        int sourceLine)
    {
        ArgumentNullException.ThrowIfNull(parser);
        markdown ??= string.Empty;

        var range = ResolveSection(parser, markdown, sourceLine);
        var document = parser.Parse(markdown);
        var blockLines = document.Blocks
            .Where(block => block.SourceLine >= range.StartLine &&
                            block.SourceLine <= range.EndLine &&
                            SupportsParagraphFormatting(block))
            .Select(static block => block.SourceLine)
            .Distinct()
            .OrderByDescending(static line => line)
            .ToArray();

        var updated = markdown;
        foreach (var line in blockLines)
        {
            updated = RichBlockFormattingEditor.Upsert(updated, line, current =>
            {
                if (current?.Paragraph?.CharacterDefaults is not { } characters || characters.Font is null)
                    return current;

                var paragraph = current.Paragraph with
                {
                    CharacterDefaults = characters with { Font = null }
                };
                return RichBlockFormattingEditor.ReplaceParagraph(current, paragraph);
            });
        }

        return updated;
    }

    private static bool SupportsParagraphFormatting(AstBlock block)
        => block is HeadingBlock or ParagraphBlock or QuoteBlock or ListItemBlock or FootnoteDefinitionBlock;

    private static int CountLines(string text)
    {
        if (text.Length == 0) return 1;
        var count = 1;
        foreach (var character in text)
            if (character == '\n') count++;
        return count;
    }
}
