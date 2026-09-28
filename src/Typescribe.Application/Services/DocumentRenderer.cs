using System.Text;
using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

public sealed class DocumentRenderer : IDocumentRenderer
{
    public string RenderPreview(DocumentAst document)
    {
        var output = new StringBuilder();
        foreach (var block in document.Blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    output.AppendLine(new string(' ', Math.Max(0, heading.Level - 1) * 2) + heading.Text.ToUpperInvariant());
                    output.AppendLine();
                    break;
                case ParagraphBlock paragraph:
                    output.AppendLine(paragraph.Text);
                    output.AppendLine();
                    break;
                case QuoteBlock quote:
                    output.AppendLine($"❝ {quote.Text}");
                    output.AppendLine();
                    break;
                case ListItemBlock item:
                    output.AppendLine($"• {item.Text}");
                    break;
                case CodeBlock code:
                    output.AppendLine(code.Text);
                    output.AppendLine();
                    break;
                case DisplayMathBlock math:
                    output.AppendLine($"⟦ {math.Text} ⟧");
                    output.AppendLine();
                    break;
            }
        }
        return output.ToString().TrimEnd();
    }

    public string RenderTypst(DocumentAst document, string title)
    {
        var output = new StringBuilder();
        output.AppendLine("#set page(width: 6in, height: 9in, margin: (x: 0.75in, y: 0.8in))");
        output.AppendLine("#set text(font: (\"Libertinus Serif\", \"Noto Naskh Arabic\", \"Noto Sans Arabic\", \"Geeza Pro\", \"Segoe UI\", \"Arial\"), size: 11pt)");
        output.AppendLine("#set par(justify: true, leading: 0.65em)");
        output.AppendLine($"#metadata({TypstString(title)}) <typescribe-title>");
        output.AppendLine();

        foreach (var block in document.Blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    output.Append(new string('=', Math.Clamp(heading.Level, 1, 6))).Append(' ').AppendLine(EscapeTypst(heading.Text));
                    output.AppendLine();
                    break;
                case ParagraphBlock paragraph:
                    output.AppendLine(EscapeTypst(paragraph.Text));
                    output.AppendLine();
                    break;
                case QuoteBlock quote:
                    output.AppendLine($"#quote(block: true)[{EscapeTypst(quote.Text)}]");
                    output.AppendLine();
                    break;
                case ListItemBlock item:
                    output.Append("- ").AppendLine(EscapeTypst(item.Text));
                    break;
                case CodeBlock code:
                    output.AppendLine("```");
                    output.AppendLine(code.Text.Replace("```", "` ` `", StringComparison.Ordinal));
                    output.AppendLine("```");
                    output.AppendLine();
                    break;
                case DisplayMathBlock math:
                    output.AppendLine("```latex");
                    output.AppendLine(math.Text.Replace("```", "` ` `", StringComparison.Ordinal));
                    output.AppendLine("```");
                    output.AppendLine();
                    break;
            }
        }
        return output.ToString();
    }

    public string RenderLatex(DocumentAst document, string title)
    {
        var output = new StringBuilder();
        output.AppendLine("\\documentclass[11pt]{book}");
        output.AppendLine("\\usepackage{fontspec}");
        output.AppendLine("\\usepackage{microtype}");
        output.AppendLine("\\usepackage{amsmath}");
        output.AppendLine("\\usepackage{hyperref}");
        output.AppendLine("\\setmainfont{Libertinus Serif}");
        output.AppendLine($"\\title{{{EscapeLatex(title)}}}");
        output.AppendLine("\\begin{document}");
        output.AppendLine("\\maketitle");

        foreach (var block in document.Blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    output.AppendLine(heading.Level switch
                    {
                        1 => $"\\chapter{{{EscapeLatex(heading.Text)}}}",
                        2 => $"\\section{{{EscapeLatex(heading.Text)}}}",
                        3 => $"\\subsection{{{EscapeLatex(heading.Text)}}}",
                        _ => $"\\paragraph{{{EscapeLatex(heading.Text)}}}"
                    });
                    break;
                case ParagraphBlock paragraph:
                    output.AppendLine(EscapeLatex(paragraph.Text));
                    output.AppendLine();
                    break;
                case QuoteBlock quote:
                    output.AppendLine("\\begin{quote}");
                    output.AppendLine(EscapeLatex(quote.Text));
                    output.AppendLine("\\end{quote}");
                    break;
                case ListItemBlock item:
                    output.AppendLine($"\\begin{{itemize}}\\item {EscapeLatex(item.Text)}\\end{{itemize}}");
                    break;
                case CodeBlock code:
                    output.AppendLine("\\begin{verbatim}");
                    output.AppendLine(code.Text);
                    output.AppendLine("\\end{verbatim}");
                    break;
                case DisplayMathBlock math:
                    output.AppendLine("\\[");
                    output.AppendLine(math.Text);
                    output.AppendLine("\\]");
                    break;
            }
        }

        output.AppendLine("\\end{document}");
        return output.ToString();
    }

    private static string EscapeTypst(string text) => text
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("#", "\\#", StringComparison.Ordinal)
        .Replace("$", "\\$", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal)
        .Replace("]", "\\]", StringComparison.Ordinal)
        .Replace("*", "\\*", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal)
        .Replace("<", "\\<", StringComparison.Ordinal)
        .Replace(">", "\\>", StringComparison.Ordinal)
        .Replace("@", "\\@", StringComparison.Ordinal);

    private static string TypstString(string text) => $"\"{text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string EscapeLatex(string text)
    {
        var output = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            output.Append(ch switch
            {
                '\\' => "\\textbackslash{}",
                '&' => "\\&",
                '%' => "\\%",
                '$' => "\\$",
                '#' => "\\#",
                '_' => "\\_",
                '{' => "\\{",
                '}' => "\\}",
                '~' => "\\textasciitilde{}",
                '^' => "\\textasciicircum{}",
                _ => ch.ToString()
            });
        }
        return output.ToString();
    }
}
