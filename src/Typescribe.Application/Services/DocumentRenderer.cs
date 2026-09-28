using System.Globalization;
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
                    output.AppendLine(new string(' ', Math.Max(0, heading.Level - 1) * 2) + heading.Inlines.ToPlainText().ToUpperInvariant());
                    output.AppendLine();
                    break;
                case ParagraphBlock paragraph:
                    output.AppendLine(RenderPreviewInlines(paragraph.Inlines));
                    output.AppendLine();
                    break;
                case QuoteBlock quote:
                    output.AppendLine($"❝ {RenderPreviewInlines(quote.Inlines)}");
                    output.AppendLine();
                    break;
                case ListItemBlock item:
                    output.Append(item.Ordered ? $"{item.Number ?? 1}. " : "• ")
                        .AppendLine(RenderPreviewInlines(item.Inlines));
                    break;
                case CodeBlock code:
                    output.AppendLine(code.Text);
                    output.AppendLine();
                    break;
                case DisplayMathBlock math:
                    output.AppendLine($"⟦ {math.Text} ⟧");
                    output.AppendLine();
                    break;
                case ThematicBreakBlock:
                    output.AppendLine("────────────");
                    output.AppendLine();
                    break;
            }
        }
        return output.ToString().TrimEnd();
    }

    public string RenderTypst(DocumentAst document, string title, BookStyle style)
    {
        style.Validate();
        var output = new StringBuilder();
        output.Append("#set page(width: ").Append(Inches(style.PageWidthInches))
            .Append(", height: ").Append(Inches(style.PageHeightInches))
            .Append(", margin: (top: ").Append(Inches(style.MarginTopInches))
            .Append(", bottom: ").Append(Inches(style.MarginBottomInches))
            .Append(", left: ").Append(Inches(style.MarginInnerInches))
            .Append(", right: ").Append(Inches(style.MarginOuterInches)).AppendLine("))");
        output.Append("#set text(font: ").Append(TypstString(style.BodyFontFamily))
            .Append(", size: ").Append(Points(style.BodyFontSizePoints)).AppendLine(")");
        output.Append("#set par(justify: ").Append(style.JustifyBody ? "true" : "false")
            .Append(", leading: ").Append(Format(style.LineSpacing)).AppendLine("em)");
        output.AppendLine($"#metadata({TypstString(title)}) <typescribe-title>");
        output.AppendLine();

        foreach (var block in document.Blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    output.Append(new string('=', Math.Clamp(heading.Level, 1, 6))).Append(' ')
                        .AppendLine(RenderTypstInlines(heading.Inlines));
                    output.AppendLine();
                    break;
                case ParagraphBlock paragraph:
                    output.AppendLine(RenderTypstInlines(paragraph.Inlines));
                    output.AppendLine();
                    break;
                case QuoteBlock quote:
                    output.AppendLine($"#quote(block: true)[{RenderTypstInlines(quote.Inlines)}]");
                    output.AppendLine();
                    break;
                case ListItemBlock item:
                    output.Append(item.Ordered ? "+ " : "- ").AppendLine(RenderTypstInlines(item.Inlines));
                    break;
                case CodeBlock code:
                    output.AppendLine("```");
                    output.AppendLine(code.Text.Replace("```", "` ` `", StringComparison.Ordinal));
                    output.AppendLine("```");
                    output.AppendLine();
                    break;
                case DisplayMathBlock math:
                    output.Append("#raw(").Append(TypstString(math.Text)).AppendLine(", lang: \"latex\", block: true)");
                    output.AppendLine();
                    break;
                case ThematicBreakBlock:
                    output.AppendLine("#line(length: 100%)");
                    output.AppendLine();
                    break;
            }
        }
        return output.ToString();
    }

    public string RenderLatex(DocumentAst document, string title, BookStyle style)
    {
        style.Validate();
        var output = new StringBuilder();
        output.AppendLine("\\documentclass[11pt,openany]{book}");
        output.AppendLine("\\usepackage{fontspec}");
        output.AppendLine("\\usepackage{microtype}");
        output.AppendLine("\\usepackage{amsmath,amssymb}");
        output.AppendLine("\\usepackage{hyperref}");
        output.Append("\\usepackage[paperwidth=").Append(Inches(style.PageWidthInches))
            .Append(",paperheight=").Append(Inches(style.PageHeightInches))
            .Append(",top=").Append(Inches(style.MarginTopInches))
            .Append(",bottom=").Append(Inches(style.MarginBottomInches))
            .Append(",inner=").Append(Inches(style.MarginInnerInches))
            .Append(",outer=").Append(Inches(style.MarginOuterInches)).AppendLine("]{geometry}");
        output.AppendLine("\\hypersetup{hidelinks}");
        output.Append("\\IfFontExistsTF{").Append(EscapeLatex(style.BodyFontFamily)).Append("}{\\setmainfont{")
            .Append(EscapeLatex(style.BodyFontFamily)).AppendLine("}}{\\setmainfont{Latin Modern Roman}}");
        output.Append("\\setlength{\\parindent}{").Append(Format(style.ParagraphIndentEm)).AppendLine("em}");
        output.Append("\\setlength{\\parskip}{").Append(Points(style.ParagraphSpacingPoints)).AppendLine("}");
        output.Append("\\linespread{").Append(Format(style.LineSpacing)).AppendLine("}");
        output.AppendLine("\\setcounter{secnumdepth}{3}");
        output.AppendLine($"\\title{{{EscapeLatex(title)}}}");
        output.AppendLine("\\begin{document}");
        output.Append("\\fontsize{").Append(Format(style.BodyFontSizePoints)).Append("}{")
            .Append(Format(style.BodyFontSizePoints * 1.25)).AppendLine("}\\selectfont");
        if (!style.JustifyBody) output.AppendLine("\\raggedright");
        output.AppendLine("\\maketitle");

        bool? openOrderedList = null;

        void CloseList()
        {
            if (openOrderedList is null) return;
            output.AppendLine(openOrderedList.Value ? "\\end{enumerate}" : "\\end{itemize}");
            openOrderedList = null;
        }

        foreach (var block in document.Blocks)
        {
            if (block is not ListItemBlock) CloseList();

            switch (block)
            {
                case HeadingBlock heading:
                    output.AppendLine(heading.Level switch
                    {
                        1 => $"\\chapter{{{RenderLatexInlines(heading.Inlines)}}}",
                        2 => $"\\section{{{RenderLatexInlines(heading.Inlines)}}}",
                        3 => $"\\subsection{{{RenderLatexInlines(heading.Inlines)}}}",
                        4 => $"\\subsubsection{{{RenderLatexInlines(heading.Inlines)}}}",
                        _ => $"\\paragraph{{{RenderLatexInlines(heading.Inlines)}}}"
                    });
                    break;
                case ParagraphBlock paragraph:
                    output.AppendLine(RenderLatexInlines(paragraph.Inlines));
                    output.AppendLine();
                    break;
                case QuoteBlock quote:
                    output.AppendLine("\\begin{quote}");
                    output.AppendLine(RenderLatexInlines(quote.Inlines));
                    output.AppendLine("\\end{quote}");
                    break;
                case ListItemBlock item:
                    if (openOrderedList != item.Ordered)
                    {
                        CloseList();
                        openOrderedList = item.Ordered;
                        output.AppendLine(item.Ordered ? "\\begin{enumerate}" : "\\begin{itemize}");
                    }
                    output.Append("\\item ").AppendLine(RenderLatexInlines(item.Inlines));
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
                case ThematicBreakBlock:
                    output.AppendLine("\\par\\medskip\\hrule\\medskip");
                    break;
            }
        }

        CloseList();
        output.AppendLine("\\end{document}");
        return output.ToString();
    }

    private static string RenderPreviewInlines(IEnumerable<AstInline> inlines)
    {
        var output = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextInline text:
                    output.Append(text.Text);
                    break;
                case StrongInline strong:
                    output.Append(RenderPreviewInlines(strong.Children));
                    break;
                case EmphasisInline emphasis:
                    output.Append(RenderPreviewInlines(emphasis.Children));
                    break;
                case CodeInline code:
                    output.Append('`').Append(code.Text).Append('`');
                    break;
                case LinkInline link:
                    output.Append(RenderPreviewInlines(link.Label)).Append(" (link)");
                    break;
                case MathInline math:
                    output.Append('⟨').Append(math.Text).Append('⟩');
                    break;
            }
        }
        return output.ToString();
    }

    private static string RenderTypstInlines(IEnumerable<AstInline> inlines)
    {
        var output = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextInline text:
                    output.Append(EscapeTypst(text.Text));
                    break;
                case StrongInline strong:
                    output.Append('*').Append(RenderTypstInlines(strong.Children)).Append('*');
                    break;
                case EmphasisInline emphasis:
                    output.Append('_').Append(RenderTypstInlines(emphasis.Children)).Append('_');
                    break;
                case CodeInline code:
                    output.Append('`').Append(code.Text.Replace("`", "\\`", StringComparison.Ordinal)).Append('`');
                    break;
                case LinkInline link:
                    output.Append("#link(").Append(TypstString(link.Url)).Append(")[")
                        .Append(RenderTypstInlines(link.Label)).Append(']');
                    break;
                case MathInline math:
                    output.Append("#raw(").Append(TypstString(math.Text)).Append(", lang: \"latex\")");
                    break;
            }
        }
        return output.ToString();
    }

    private static string RenderLatexInlines(IEnumerable<AstInline> inlines)
    {
        var output = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextInline text:
                    output.Append(EscapeLatex(text.Text));
                    break;
                case StrongInline strong:
                    output.Append("\\textbf{").Append(RenderLatexInlines(strong.Children)).Append('}');
                    break;
                case EmphasisInline emphasis:
                    output.Append("\\emph{").Append(RenderLatexInlines(emphasis.Children)).Append('}');
                    break;
                case CodeInline code:
                    output.Append("\\texttt{").Append(EscapeLatex(code.Text)).Append('}');
                    break;
                case LinkInline link:
                    output.Append("\\href{").Append(EscapeLatexUrl(link.Url)).Append("}{")
                        .Append(RenderLatexInlines(link.Label)).Append('}');
                    break;
                case MathInline math:
                    output.Append('$').Append(math.Text).Append('$');
                    break;
            }
        }
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

    private static string EscapeLatexUrl(string text) => text
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("#", "\\#", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("&", "\\&", StringComparison.Ordinal)
        .Replace("{", "\\{", StringComparison.Ordinal)
        .Replace("}", "\\}", StringComparison.Ordinal);

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Inches(double value) => $"{Format(value)}in";
    private static string Points(double value) => $"{Format(value)}pt";
}
