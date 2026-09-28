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
            blocks.Add(new ParagraphBlock(paragraphStart, paragraph.ToString().Trim()));
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

            var headingLevel = CountHeadingPrefix(trimmed);
            if (headingLevel > 0)
            {
                FlushParagraph();
                blocks.Add(new HeadingBlock(lineNumber, headingLevel, trimmed[(headingLevel + 1)..].Trim()));
                continue;
            }

            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                FlushParagraph();
                blocks.Add(new QuoteBlock(lineNumber, trimmed[2..].Trim()));
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                FlushParagraph();
                blocks.Add(new ListItemBlock(lineNumber, trimmed[2..].Trim()));
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

    private static int CountHeadingPrefix(string text)
    {
        var count = 0;
        while (count < text.Length && count < 6 && text[count] == '#') count++;
        return count > 0 && count < text.Length && text[count] == ' ' ? count : 0;
    }
}
