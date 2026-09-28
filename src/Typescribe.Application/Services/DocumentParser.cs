using System.Globalization;
using System.Text;
using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

public sealed class DocumentParser : IDocumentParser
{
    public DocumentAst Parse(string source)
    {
        source ??= string.Empty;
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var blocks = new List<AstBlock>();
        var paragraph = new StringBuilder();
        var paragraphStart = 0;
        var inCode = false;
        var codeLanguage = string.Empty;
        var codeStart = 0;
        var code = new StringBuilder();
        var inMath = false;
        var mathStart = 0;
        var math = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            blocks.Add(new ParagraphBlock(paragraphStart, ParseInlines(paragraph.ToString().Trim())));
            paragraph.Clear();
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index];
            var trimmed = line.Trim();

            if (inCode)
            {
                if (trimmed == "```")
                {
                    blocks.Add(new CodeBlock(codeStart, codeLanguage, code.ToString().TrimEnd()));
                    code.Clear();
                    inCode = false;
                }
                else
                {
                    code.AppendLine(line);
                }
                continue;
            }

            if (inMath)
            {
                if (trimmed == "$$")
                {
                    blocks.Add(new DisplayMathBlock(mathStart, math.ToString().Trim()));
                    math.Clear();
                    inMath = false;
                }
                else
                {
                    math.AppendLine(line);
                }
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                inCode = true;
                codeLanguage = trimmed[3..].Trim();
                codeStart = lineNumber;
                continue;
            }

            if (trimmed.StartsWith("$$", StringComparison.Ordinal) && trimmed.EndsWith("$$", StringComparison.Ordinal) && trimmed.Length > 4)
            {
                FlushParagraph();
                blocks.Add(new DisplayMathBlock(lineNumber, trimmed[2..^2].Trim()));
                continue;
            }

            if (trimmed == "$$")
            {
                FlushParagraph();
                inMath = true;
                mathStart = lineNumber;
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (trimmed is "---" or "***" or "___")
            {
                FlushParagraph();
                blocks.Add(new ThematicBreakBlock(lineNumber));
                continue;
            }

            var headingLevel = CountHeadingPrefix(trimmed);
            if (headingLevel > 0)
            {
                FlushParagraph();
                blocks.Add(new HeadingBlock(lineNumber, headingLevel, ParseInlines(trimmed[(headingLevel + 1)..].Trim())));
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var quote = trimmed.Length > 1 ? trimmed[1..].TrimStart() : string.Empty;
                blocks.Add(new QuoteBlock(lineNumber, ParseInlines(quote)));
                continue;
            }

            if (TryParseListItem(trimmed, out var ordered, out var number, out var itemText))
            {
                FlushParagraph();
                blocks.Add(new ListItemBlock(lineNumber, ordered, number, ParseInlines(itemText)));
                continue;
            }

            if (paragraph.Length == 0) paragraphStart = lineNumber;
            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(trimmed);
        }

        FlushParagraph();
        if (inCode) blocks.Add(new CodeBlock(codeStart, codeLanguage, code.ToString().TrimEnd()));
        if (inMath) blocks.Add(new DisplayMathBlock(mathStart, math.ToString().Trim()));
        return new DocumentAst(blocks);
    }

    private static IReadOnlyList<AstInline> ParseInlines(string text)
    {
        var result = new List<AstInline>();
        var plain = new StringBuilder();

        void FlushPlain()
        {
            if (plain.Length == 0) return;
            result.Add(new TextInline(plain.ToString()));
            plain.Clear();
        }

        for (var index = 0; index < text.Length;)
        {
            if (text[index] == '\\' && index + 1 < text.Length && IsEscapable(text[index + 1]))
            {
                plain.Append(text[index + 1]);
                index += 2;
                continue;
            }

            if (index + 1 < text.Length && text[index] == '*' && text[index + 1] == '*')
            {
                var end = text.IndexOf("**", index + 2, StringComparison.Ordinal);
                if (end > index + 2)
                {
                    FlushPlain();
                    result.Add(new StrongInline(ParseInlines(text[(index + 2)..end])));
                    index = end + 2;
                    continue;
                }
            }

            if (text[index] is '*' or '_')
            {
                var marker = text[index];
                var end = text.IndexOf(marker, index + 1);
                if (end > index + 1)
                {
                    FlushPlain();
                    result.Add(new EmphasisInline(ParseInlines(text[(index + 1)..end])));
                    index = end + 1;
                    continue;
                }
            }

            if (text[index] == '`')
            {
                var end = text.IndexOf('`', index + 1);
                if (end > index + 1)
                {
                    FlushPlain();
                    result.Add(new CodeInline(text[(index + 1)..end]));
                    index = end + 1;
                    continue;
                }
            }

            if (text[index] == '[')
            {
                var labelEnd = text.IndexOf(']', index + 1);
                if (labelEnd > index + 1 && labelEnd + 1 < text.Length && text[labelEnd + 1] == '(')
                {
                    var urlEnd = text.IndexOf(')', labelEnd + 2);
                    if (urlEnd > labelEnd + 2)
                    {
                        FlushPlain();
                        var label = ParseInlines(text[(index + 1)..labelEnd]);
                        var url = text[(labelEnd + 2)..urlEnd].Trim();
                        result.Add(new LinkInline(label, url));
                        index = urlEnd + 1;
                        continue;
                    }
                }
            }

            if (text[index] == '$')
            {
                var end = text.IndexOf('$', index + 1);
                if (end > index + 1)
                {
                    FlushPlain();
                    result.Add(new MathInline(text[(index + 1)..end].Trim()));
                    index = end + 1;
                    continue;
                }
            }

            plain.Append(text[index]);
            index++;
        }

        FlushPlain();
        return result;
    }

    private static bool TryParseListItem(string text, out bool ordered, out int? number, out string itemText)
    {
        ordered = false;
        number = null;
        itemText = string.Empty;

        if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal) || text.StartsWith("+ ", StringComparison.Ordinal))
        {
            itemText = text[2..].Trim();
            return true;
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot + 1 >= text.Length || text[dot + 1] != ' ') return false;
        if (!int.TryParse(text.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return false;

        ordered = true;
        number = parsed;
        itemText = text[(dot + 2)..].Trim();
        return true;
    }

    private static int CountHeadingPrefix(string text)
    {
        var count = 0;
        while (count < text.Length && count < 6 && text[count] == '#') count++;
        return count > 0 && count < text.Length && text[count] == ' ' ? count : 0;
    }

    private static bool IsEscapable(char value) => value is '\\' or '*' or '_' or '`' or '[' or ']' or '(' or ')' or '$' or '#';
}
