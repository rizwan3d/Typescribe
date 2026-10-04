using System.Text;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

/// <summary>
/// Adds generated book matter around the semantic manuscript without changing source parsing.
/// Cover and spine options intentionally render as proof pages in the book PDF; printer-specific
/// wrap-cover geometry can still be handled separately by a print production workflow.
/// </summary>
internal static class BookMatterLatexPostProcessor
{
    private const string MakeTitleMarker = "\\maketitle";
    private const string EndDocumentMarker = "\\end{document}";
    private const string BibliographyStart = "\\begin{thebibliography}";
    private const string BibliographyEnd = "\\end{thebibliography}";

    public static string Apply(string latex, string title, BookStyle style)
    {
        if (string.IsNullOrWhiteSpace(latex)) return latex;

        var bibliography = ExtractBibliography(ref latex);
        var makeTitle = latex.IndexOf(MakeTitleMarker, StringComparison.Ordinal);
        if (makeTitle >= 0)
        {
            var frontMatter = BuildFrontMatter(title, style);
            latex = latex.Remove(makeTitle, MakeTitleMarker.Length).Insert(makeTitle, frontMatter);
        }

        var endDocument = latex.LastIndexOf(EndDocumentMarker, StringComparison.Ordinal);
        if (endDocument >= 0)
        {
            var backMatter = BuildBackMatter(title, style, bibliography);
            latex = latex.Insert(endDocument, backMatter);
        }

        return ApplyRunningElements(latex, style);
    }

    private static string BuildFrontMatter(string title, BookStyle style)
    {
        var output = new StringBuilder();
        if (style.IncludeFrontCover)
            AppendCoverProof(output, style.FrontCoverText, title);
        if (style.IncludeSpine)
            AppendCoverProof(output, style.SpineText, title);

        AppendBlankPages(output, style.FrontBlankPages);

        if (style.IncludeTitlePage)
        {
            output.AppendLine("\\maketitle");
            output.AppendLine("\\cleardoublepage");
        }

        if (style.IncludeCopyrightPage)
            AppendCopyrightPage(output, style.CopyrightText);
        if (style.IncludeDedication)
            AppendDedicationPage(output, style.DedicationText);

        if (style.IncludeTableOfContents)
        {
            output.AppendLine("\\tableofcontents");
            output.AppendLine("\\cleardoublepage");
        }

        if (style.IncludePreface)
            AppendChapter(output, "Preface", style.PrefaceText);
        if (style.IncludeForeword)
            AppendChapter(output, "Foreword", style.ForewordText);
        if (style.IncludeIntroduction)
            AppendChapter(output, "Introduction", style.IntroductionText);

        return output.ToString();
    }

    private static string BuildBackMatter(string title, BookStyle style, string bibliography)
    {
        var output = new StringBuilder();
        if (style.IncludeConclusion)
            AppendChapter(output, "Conclusion", style.ConclusionText);
        if (style.IncludeEpilogue)
            AppendChapter(output, "Epilogue", style.EpilogueText);
        if (style.IncludeAcknowledgments)
            AppendChapter(output, "Acknowledgments", style.AcknowledgmentsText);
        if (style.IncludeAppendix)
            AppendChapter(output, "Appendix", style.AppendixText);
        if (style.IncludeGlossary)
            AppendChapter(output, "Glossary", style.GlossaryText);

        if (style.IncludeReferencesBibliography && !string.IsNullOrWhiteSpace(bibliography))
        {
            output.AppendLine("\\cleardoublepage");
            output.AppendLine(bibliography.Trim());
            output.AppendLine();
            output.AppendLine("\\cleardoublepage");
        }

        if (style.IncludeIndex)
            AppendChapter(output, "Index", style.IndexText);
        if (style.IncludeAboutAuthor)
            AppendChapter(output, "About the Author", style.AboutAuthorText);

        AppendBlankPages(output, style.EndBlankPages);

        if (style.IncludeBackCover)
            AppendCoverProof(output, style.BackCoverText, title);

        return output.ToString();
    }

    private static void AppendCoverProof(StringBuilder output, string configuredText, string fallbackTitle)
    {
        var text = string.IsNullOrWhiteSpace(configuredText) ? fallbackTitle : configuredText.Trim();
        output.AppendLine("\\begin{titlepage}");
        output.AppendLine("\\thispagestyle{empty}");
        output.AppendLine("\\centering");
        output.AppendLine("\\vspace*{\\fill}");
        output.Append("{\\Huge\\bfseries ").Append(RenderPlainText(text)).AppendLine("\\par}");
        output.AppendLine("\\vspace*{\\fill}");
        output.AppendLine("\\end{titlepage}");
        output.AppendLine("\\clearpage");
    }

    private static void AppendCopyrightPage(StringBuilder output, string text)
    {
        output.AppendLine("\\clearpage");
        output.AppendLine("\\thispagestyle{empty}");
        output.AppendLine("\\vspace*{\\fill}");
        output.AppendLine("\\noindent\\textbf{Copyright}");
        if (!string.IsNullOrWhiteSpace(text))
            output.Append("\\par\\medskip ").AppendLine(RenderPlainText(text));
        output.AppendLine("\\clearpage");
    }

    private static void AppendDedicationPage(StringBuilder output, string text)
    {
        output.AppendLine("\\begin{titlepage}");
        output.AppendLine("\\thispagestyle{empty}");
        output.AppendLine("\\centering");
        output.AppendLine("\\vspace*{\\fill}");
        if (!string.IsNullOrWhiteSpace(text))
            output.Append("{\\itshape ").Append(RenderPlainText(text)).AppendLine("\\par}");
        output.AppendLine("\\vspace*{\\fill}");
        output.AppendLine("\\end{titlepage}");
        output.AppendLine("\\clearpage");
    }

    private static void AppendChapter(StringBuilder output, string heading, string text)
    {
        output.AppendLine("\\cleardoublepage");
        output.Append("\\chapter*{").Append(EscapeLatex(heading)).AppendLine("}");
        output.Append("\\addcontentsline{toc}{chapter}{").Append(EscapeLatex(heading)).AppendLine("}");
        if (!string.IsNullOrWhiteSpace(text))
        {
            output.AppendLine(RenderPlainText(text));
            output.AppendLine();
        }
        output.AppendLine("\\cleardoublepage");
    }

    private static void AppendBlankPages(StringBuilder output, int count)
    {
        for (var page = 0; page < Math.Max(0, count); page++)
        {
            output.AppendLine("\\clearpage");
            output.AppendLine("\\thispagestyle{empty}");
            output.AppendLine("\\null");
            output.AppendLine("\\clearpage");
        }
    }

    private static string ExtractBibliography(ref string latex)
    {
        var start = latex.IndexOf(BibliographyStart, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        var end = latex.IndexOf(BibliographyEnd, start, StringComparison.Ordinal);
        if (end < 0) return string.Empty;
        end += BibliographyEnd.Length;

        var block = latex[start..end];
        latex = latex.Remove(start, end - start);
        return block;
    }

    private static string ApplyRunningElements(string latex, BookStyle style)
    {
        if (!style.ShowHeadersAndFooters)
        {
            var pageStyle = style.ShowPageNumbers ? "\\pagestyle{plain}" : "\\pagestyle{empty}";
            latex = latex.Replace("\\pagestyle{fancy}", pageStyle, StringComparison.Ordinal);
        }

        if (!style.ShowPageNumbers)
        {
            const string marker = "\\begin{document}";
            var index = latex.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                var insert = index + marker.Length;
                latex = latex.Insert(insert,
                    Environment.NewLine + "\\makeatletter\\let\\ps@plain\\ps@empty\\makeatother");
            }
        }

        return latex;
    }

    private static string RenderPlainText(string value)
    {
        var normalized = (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (normalized.Length == 0) return string.Empty;

        return string.Join("\\par" + Environment.NewLine,
            normalized.Split('\n').Select(EscapeLatex));
    }

    private static string EscapeLatex(string value)
    {
        var output = new StringBuilder(value.Length + 16);
        foreach (var character in value)
        {
            output.Append(character switch
            {
                '\\' => "\\textbackslash{}",
                '{' => "\\{",
                '}' => "\\}",
                '$' => "\\$",
                '&' => "\\&",
                '#' => "\\#",
                '_' => "\\_",
                '%' => "\\%",
                '^' => "\\textasciicircum{}",
                '~' => "\\textasciitilde{}",
                _ => character.ToString()
            });
        }
        return output.ToString();
    }
}
