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
                    if (!string.IsNullOrWhiteSpace(code.Language)) output.AppendLine($"[{code.Language}]");
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

    public string RenderLatex(DocumentAst document, string title, BookStyle style)
    {
        style.Validate();
        var output = new StringBuilder(16_384);
        AppendPreamble(output, title, style);

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
                        5 => $"\\paragraph{{{RenderLatexInlines(heading.Inlines)}}}",
                        _ => $"\\subparagraph{{{RenderLatexInlines(heading.Inlines)}}}"
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
                    AppendCodeBlock(output, code);
                    break;
                case DisplayMathBlock math:
                    output.AppendLine("\\[");
                    output.AppendLine(math.Text);
                    output.AppendLine("\\]");
                    break;
                case ThematicBreakBlock:
                    output.AppendLine("\\par\\medskip\\noindent\\rule{\\linewidth}{0.4pt}\\medskip");
                    break;
            }
        }

        CloseList();
        output.AppendLine("\\end{document}");
        return output.ToString();
    }

    private static void AppendPreamble(StringBuilder output, string title, BookStyle style)
    {
        var options = NormalizeClassOptions(style);
        output.Append("\\documentclass");
        if (!string.IsNullOrWhiteSpace(options)) output.Append('[').Append(options).Append(']');
        output.Append('{').Append(SanitizeCommandName(style.DocumentClass, "book")).AppendLine("}");

        output.AppendLine("\\usepackage{fontspec}");
        output.AppendLine("\\usepackage{unicode-math}");
        output.AppendLine("\\usepackage{amsmath,amssymb,mathtools}");
        output.AppendLine("\\usepackage{xcolor}");
        output.AppendLine("\\usepackage{hyperref}");
        output.AppendLine("\\usepackage{titlesec}");
        output.AppendLine("\\usepackage{enumitem}");
        output.AppendLine("\\usepackage{etoolbox}");
        output.AppendLine("\\usepackage{caption}");
        output.AppendLine("\\usepackage{listings}");
        output.AppendLine("\\usepackage{fancyhdr}");
        if (style.EnableMicrotype) output.AppendLine("\\usepackage{microtype}");
        AppendExtraPackages(output, style.ExtraPackages);

        output.Append("\\usepackage[paperwidth=").Append(Inches(style.PageWidthInches))
            .Append(",paperheight=").Append(Inches(style.PageHeightInches))
            .Append(",top=").Append(Inches(style.MarginTopInches))
            .Append(",bottom=").Append(Inches(style.MarginBottomInches))
            .Append(",inner=").Append(Inches(style.MarginInnerInches))
            .Append(",outer=").Append(Inches(style.MarginOuterInches)).AppendLine("]{geometry}");

        DefineColor(output, "tsBody", style.BodyColorHex);
        DefineColor(output, "tsHeading", style.HeadingColorHex);
        DefineColor(output, "tsLink", style.LinkColorHex);
        DefineColor(output, "tsCodeBg", style.CodeBackgroundHex);
        DefineColor(output, "tsCodeText", style.CodeTextHex);
        DefineColor(output, "tsCodeKeyword", style.CodeKeywordHex);
        DefineColor(output, "tsCodeString", style.CodeStringHex);
        DefineColor(output, "tsCodeComment", style.CodeCommentHex);
        DefineColor(output, "tsCodeFrame", style.CodeFrameHex);

        if (style.ColorLinks)
            output.AppendLine("\\hypersetup{colorlinks=true,linkcolor=tsLink,urlcolor=tsLink,citecolor=tsLink}");
        else
            output.AppendLine("\\hypersetup{hidelinks}");

        output.AppendLine("\\defaultfontfeatures{Ligatures=TeX,Scale=MatchLowercase}");
        AppendFontSelection(output, style);
        AppendTypography(output, style);
        AppendCodeStyle(output, style);
        AppendHeadersAndFooters(output, style);

        if (style.AvoidWidowsAndOrphans)
        {
            output.AppendLine("\\widowpenalty=10000");
            output.AppendLine("\\clubpenalty=10000");
            output.AppendLine("\\displaywidowpenalty=10000");
        }

        output.Append("\\setcounter{tocdepth}{").Append(style.TableOfContentsDepth).AppendLine("}");
        output.Append("\\setcounter{secnumdepth}{").Append(style.SectionNumberDepth).AppendLine("}");

        if (!string.IsNullOrWhiteSpace(style.CustomPreamble))
        {
            output.AppendLine();
            output.AppendLine("% --- Typescribe custom preamble ---");
            output.AppendLine(style.CustomPreamble.TrimEnd());
            output.AppendLine("% --- end custom preamble ---");
        }

        output.AppendLine($"\\title{{{EscapeLatex(title)}}}");
        output.AppendLine("\\begin{document}");
        output.Append("\\fontsize{").Append(Format(style.BodyFontSizePoints)).Append("}{")
            .Append(Format(style.BodyFontSizePoints * 1.25)).AppendLine("}\\selectfont");
        output.AppendLine("\\color{tsBody}");
        if (!style.JustifyBody) output.AppendLine("\\raggedright");
        output.AppendLine("\\maketitle");
        if (style.IncludeTableOfContents)
        {
            output.AppendLine("\\tableofcontents");
            output.AppendLine("\\cleardoublepage");
        }
    }

    private static void AppendFontSelection(StringBuilder output, BookStyle style)
    {
        output.Append("\\IfFontExistsTF{").Append(EscapeLatex(style.BodyFontFamily)).Append("}{\\setmainfont{")
            .Append(EscapeLatex(style.BodyFontFamily)).AppendLine("}}{}");
        output.Append("\\IfFontExistsTF{").Append(EscapeLatex(style.HeadingFontFamily)).Append("}{\\newfontfamily\\TypescribeHeadingFont{")
            .Append(EscapeLatex(style.HeadingFontFamily)).AppendLine("}}{\\newcommand{\\TypescribeHeadingFont}{\\rmfamily}}");
        output.Append("\\IfFontExistsTF{").Append(EscapeLatex(style.MonospaceFontFamily)).Append("}{\\setmonofont{")
            .Append(EscapeLatex(style.MonospaceFontFamily)).AppendLine("}}{}");
        output.Append("\\IfFontExistsTF{").Append(EscapeLatex(style.MathFontFamily)).Append("}{\\setmathfont{")
            .Append(EscapeLatex(style.MathFontFamily)).AppendLine("}}{}");
    }

    private static void AppendTypography(StringBuilder output, BookStyle style)
    {
        output.Append("\\setlength{\\parindent}{").Append(Format(style.ParagraphIndentEm)).AppendLine("em}");
        output.Append("\\setlength{\\parskip}{").Append(Points(style.ParagraphSpacingPoints)).AppendLine("}");
        output.Append("\\linespread{").Append(Format(style.LineSpacing)).AppendLine("}");

        AppendTitleFormat(output, "chapter", style.ChapterFontSizePoints, true);
        AppendTitleFormat(output, "section", style.SectionFontSizePoints, false);
        AppendTitleFormat(output, "subsection", style.SubsectionFontSizePoints, false);
        AppendTitleFormat(output, "subsubsection", style.SubsubsectionFontSizePoints, false);

        output.Append("\\titlespacing*{\\chapter}{0pt}{").Append(Points(style.ChapterBeforeSpacingPoints))
            .Append("}{").Append(Points(style.ChapterAfterSpacingPoints)).AppendLine("}");
        output.Append("\\titlespacing*{\\section}{0pt}{").Append(Points(style.SectionBeforeSpacingPoints))
            .Append("}{").Append(Points(style.SectionAfterSpacingPoints)).AppendLine("}");

        output.Append("\\AtBeginEnvironment{quote}{\\itshape");
        if (!style.QuoteItalic) output.Append("\\upshape");
        output.Append("\\fontsize{").Append(Format(style.QuoteFontSizePoints)).Append("}{")
            .Append(Format(style.QuoteFontSizePoints * 1.28)).Append("}\\selectfont\\leftskip=")
            .Append(Format(style.QuoteIndentEm)).AppendLine("em\\rightskip=\\leftskip}");

        output.Append("\\setlist{itemsep=").Append(Points(style.ListItemSpacingPoints)).AppendLine(",topsep=4pt}");
        output.Append("\\DeclareCaptionFont{typescribe}{\\fontsize{").Append(Format(style.CaptionFontSizePoints)).Append("}{")
            .Append(Format(style.CaptionFontSizePoints * 1.2)).AppendLine("}\\selectfont}");
        output.AppendLine("\\captionsetup{font=typescribe}");
        output.Append("\\renewcommand{\\footnotesize}{\\fontsize{").Append(Format(style.FootnoteFontSizePoints)).Append("}{")
            .Append(Format(style.FootnoteFontSizePoints * 1.2)).AppendLine("}\\selectfont}");
    }

    private static void AppendTitleFormat(StringBuilder output, string command, double size, bool display)
    {
        output.Append("\\titleformat{\\").Append(command).Append('}');
        if (display) output.Append("[display]");
        output.Append("{\\TypescribeHeadingFont\\color{tsHeading}\\bfseries\\fontsize{").Append(Format(size)).Append("}{")
            .Append(Format(size * 1.15)).Append("}\\selectfont}");
        if (command == "chapter") output.Append("{\\chaptertitlename\\ \\thechapter}{8pt}{}");
        else output.Append("{\\the").Append(command).Append("}{0.7em}{}");
        output.AppendLine();
    }

    private static void AppendCodeStyle(StringBuilder output, BookStyle style)
    {
        output.AppendLine("\\lstdefinestyle{typescribe}{%");
        output.Append("  basicstyle=\\ttfamily\\color{tsCodeText}\\fontsize{").Append(Format(style.CodeFontSizePoints)).Append("}{")
            .Append(Format(style.CodeFontSizePoints * 1.25)).AppendLine("}\\selectfont,");
        output.AppendLine("  backgroundcolor=\\color{tsCodeBg},");
        output.AppendLine("  keywordstyle=\\color{tsCodeKeyword}\\bfseries,");
        output.AppendLine("  stringstyle=\\color{tsCodeString},");
        output.AppendLine("  commentstyle=\\color{tsCodeComment}\\itshape,");
        output.AppendLine("  rulecolor=\\color{tsCodeFrame},frame=single,");
        output.AppendLine("  breaklines=true,breakatwhitespace=false,showstringspaces=false,");
        output.AppendLine("  keepspaces=true,columns=fullflexible,tabsize=4,");
        output.Append("  numbers=").Append(style.CodeLineNumbers ? "left" : "none").AppendLine(",numberstyle=\\tiny\\color{tsCodeComment}");
        output.AppendLine("}");
    }

    private static void AppendHeadersAndFooters(StringBuilder output, BookStyle style)
    {
        output.AppendLine("\\pagestyle{fancy}");
        output.AppendLine("\\fancyhf{}");
        output.Append("\\renewcommand{\\headrulewidth}{0.3pt}").AppendLine();
        output.Append("\\renewcommand{\\footrulewidth}{0pt}").AppendLine();
        output.Append("\\fancyhead[L]{\\fontsize{").Append(Format(style.HeaderFooterFontSizePoints)).Append("}{")
            .Append(Format(style.HeaderFooterFontSizePoints * 1.2)).Append("}\\selectfont ").Append(EscapeLatex(style.HeaderLeft)).AppendLine("}");
        output.Append("\\fancyhead[C]{").Append(EscapeLatex(style.HeaderCenter)).AppendLine("}");
        output.Append("\\fancyhead[R]{").Append(EscapeLatex(style.HeaderRight)).AppendLine("}");
        output.Append("\\fancyfoot[L]{").Append(EscapeLatex(style.FooterLeft)).AppendLine("}");
        var centerFooter = style.FooterCenter;
        if (style.ShowPageNumbers && string.IsNullOrWhiteSpace(centerFooter))
            output.AppendLine("\\fancyfoot[C]{\\thepage}");
        else
            output.Append("\\fancyfoot[C]{").Append(EscapeLatex(centerFooter)).AppendLine("}");
        output.Append("\\fancyfoot[R]{").Append(EscapeLatex(style.FooterRight)).AppendLine("}");
    }

    private static void AppendCodeBlock(StringBuilder output, CodeBlock code)
    {
        var language = ListingLanguage(code.Language);
        output.Append("\\begin{lstlisting}[style=typescribe");
        if (language.Length > 0) output.Append(",language=").Append(language);
        output.AppendLine("]");
        output.AppendLine(code.Text);
        output.AppendLine("\\end{lstlisting}");
    }

    private static string ListingLanguage(string language)
    {
        var value = language.Trim().ToLowerInvariant();
        return value switch
        {
            "c#" or "csharp" or "cs" => "{[Sharp]C}",
            "c++" or "cpp" => "{C++}",
            "c" => "C",
            "java" => "Java",
            "js" or "javascript" => "JavaScript",
            "python" or "py" => "Python",
            "sql" => "SQL",
            "html" => "HTML",
            "xml" => "XML",
            "tex" or "latex" => "TeX",
            "matlab" => "Matlab",
            "r" => "R",
            _ => string.Empty
        };
    }

    private static void AppendExtraPackages(StringBuilder output, string packages)
    {
        foreach (var raw in packages.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var package = raw.Trim();
            if (package.Length == 0 || package.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.'))) continue;
            output.Append("\\usepackage{").Append(package).AppendLine("}");
        }
    }

    private static string NormalizeClassOptions(BookStyle style)
    {
        var options = style.DocumentClassOptions.Trim();
        if (!style.OpenChaptersOnRight) return options;
        if (options.Contains("openany", StringComparison.OrdinalIgnoreCase))
            return options.Replace("openany", "openright", StringComparison.OrdinalIgnoreCase);
        return options.Length == 0 ? "openright" : options + ",openright";
    }

    private static string SanitizeCommandName(string value, string fallback)
    {
        var cleaned = new string(value.Trim().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        return cleaned.Length == 0 ? fallback : cleaned;
    }

    private static void DefineColor(StringBuilder output, string name, string value)
        => output.Append("\\definecolor{").Append(name).Append("}{HTML}{").Append(value.Trim().TrimStart('#').ToUpperInvariant()).AppendLine("}");

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
                    output.Append("\\textcolor{tsCodeText}{\\texttt{").Append(EscapeLatex(code.Text)).Append("}}");
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
