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
    private const string BeginDocumentMarker = "\\begin{document}";
    private const string BibliographyStart = "\\begin{thebibliography}";
    private const string BibliographyEnd = "\\end{thebibliography}";

    private static readonly string[] FrontMatterTokens =
    [
        "front-cover", "spine", "front-blanks", "title-page", "copyright",
        "dedication", "toc", "foreword", "preface", "introduction"
    ];

    private static readonly string[] BackMatterTokens =
    [
        "conclusion", "epilogue", "acknowledgments", "appendix", "glossary",
        "references", "index", "about-author", "end-blanks", "back-cover"
    ];

    public static string Apply(string latex, string title, BookStyle style)
    {
        if (string.IsNullOrWhiteSpace(latex)) return latex;

        var bibliography = ExtractBibliography(ref latex);
        latex = ApplyPublishingMetadata(latex, title, style);

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

    private static string BuildFrontMatter(string projectTitle, BookStyle style)
    {
        var output = new StringBuilder();
        var publishedTitle = EffectiveTitle(projectTitle, style);

        if (style.UseRomanFrontMatterPageNumbers)
            output.AppendLine("\\pagenumbering{roman}");

        foreach (var token in NormalizeOrder(style.FrontMatterOrder, FrontMatterTokens))
        {
            switch (token)
            {
                case "front-cover" when style.IncludeFrontCover:
                    AppendCoverProof(output, style.FrontCoverText, publishedTitle);
                    break;
                case "spine" when style.IncludeSpine:
                    AppendCoverProof(output, style.SpineText, publishedTitle);
                    break;
                case "front-blanks":
                    AppendBlankPages(output, style.FrontBlankPages);
                    break;
                case "title-page" when style.IncludeTitlePage:
                    AppendTitlePage(output, publishedTitle, style);
                    break;
                case "copyright" when style.IncludeCopyrightPage:
                    AppendCopyrightPage(output, style.CopyrightText);
                    break;
                case "dedication" when style.IncludeDedication:
                    AppendDedicationPage(output, style.DedicationText);
                    break;
                case "toc" when style.IncludeTableOfContents:
                    AppendPageBreak(output, style);
                    output.AppendLine("\\tableofcontents");
                    AppendPageBreak(output, style);
                    break;
                case "foreword" when style.IncludeForeword:
                    AppendChapter(output, Heading(style.ForewordHeading, "Foreword"), style.ForewordText, style);
                    break;
                case "preface" when style.IncludePreface:
                    AppendChapter(output, Heading(style.PrefaceHeading, "Preface"), style.PrefaceText, style);
                    break;
                case "introduction" when style.IncludeIntroduction:
                    AppendChapter(output, Heading(style.IntroductionHeading, "Introduction"), style.IntroductionText, style);
                    break;
            }
        }

        if (style.ResetBodyPageNumbers)
        {
            AppendPageBreak(output, style);
            output.AppendLine("\\pagenumbering{arabic}");
        }

        return output.ToString();
    }

    private static string BuildBackMatter(string projectTitle, BookStyle style, string bibliography)
    {
        var output = new StringBuilder();
        var publishedTitle = EffectiveTitle(projectTitle, style);

        foreach (var token in NormalizeOrder(style.BackMatterOrder, BackMatterTokens))
        {
            switch (token)
            {
                case "conclusion" when style.IncludeConclusion:
                    AppendChapter(output, Heading(style.ConclusionHeading, "Conclusion"), style.ConclusionText, style);
                    break;
                case "epilogue" when style.IncludeEpilogue:
                    AppendChapter(output, Heading(style.EpilogueHeading, "Epilogue"), style.EpilogueText, style);
                    break;
                case "acknowledgments" when style.IncludeAcknowledgments:
                    AppendChapter(output, Heading(style.AcknowledgmentsHeading, "Acknowledgments"), style.AcknowledgmentsText, style);
                    break;
                case "appendix" when style.IncludeAppendix:
                    AppendChapter(output, Heading(style.AppendixHeading, "Appendix"), style.AppendixText, style);
                    break;
                case "glossary" when style.IncludeGlossary:
                    AppendChapter(output, Heading(style.GlossaryHeading, "Glossary"), style.GlossaryText, style);
                    break;
                case "references" when style.IncludeReferencesBibliography && !string.IsNullOrWhiteSpace(bibliography):
                    AppendBibliography(output, bibliography, Heading(style.ReferencesHeading, "Bibliography"), style);
                    break;
                case "index" when style.IncludeIndex:
                    AppendChapter(output, Heading(style.IndexHeading, "Index"), style.IndexText, style);
                    break;
                case "about-author" when style.IncludeAboutAuthor:
                    AppendChapter(output, Heading(style.AboutAuthorHeading, "About the Author"), style.AboutAuthorText, style);
                    break;
                case "end-blanks":
                    AppendBlankPages(output, style.EndBlankPages);
                    break;
                case "back-cover" when style.IncludeBackCover:
                    AppendCoverProof(output, style.BackCoverText, publishedTitle);
                    break;
            }
        }

        return output.ToString();
    }

    private static void AppendTitlePage(StringBuilder output, string title, BookStyle style)
    {
        output.AppendLine("\\begin{titlepage}");
        output.AppendLine("\\thispagestyle{empty}");
        output.AppendLine("\\centering");
        output.AppendLine("\\vspace*{0.10\\textheight}");

        if (!string.IsNullOrWhiteSpace(style.SeriesTitle))
        {
            output.Append("{\\large ").Append(RenderPlainText(style.SeriesTitle.Trim())).AppendLine("\\par}");
            if (!string.IsNullOrWhiteSpace(style.VolumeLabel))
                output.Append("{\\normalsize ").Append(RenderPlainText(style.VolumeLabel.Trim())).AppendLine("\\par}");
            output.AppendLine("\\vspace{1.5cm}");
        }

        output.Append("{\\Huge\\bfseries ").Append(RenderPlainText(title)).AppendLine("\\par}");
        if (!string.IsNullOrWhiteSpace(style.Subtitle))
        {
            output.AppendLine("\\vspace{0.8cm}");
            output.Append("{\\Large ").Append(RenderPlainText(style.Subtitle.Trim())).AppendLine("\\par}");
        }

        output.AppendLine("\\vfill");
        if (!string.IsNullOrWhiteSpace(style.DisplayAuthor))
            output.Append("{\\Large ").Append(RenderPlainText(style.DisplayAuthor.Trim())).AppendLine("\\par}");
        if (!string.IsNullOrWhiteSpace(style.Edition))
            output.Append("\\vspace{0.7cm}{\\normalsize ").Append(RenderPlainText(style.Edition.Trim())).AppendLine("\\par}");
        if (!string.IsNullOrWhiteSpace(style.Publisher))
            output.Append("\\vspace{1.0cm}{\\normalsize ").Append(RenderPlainText(style.Publisher.Trim())).AppendLine("\\par}");
        if (!string.IsNullOrWhiteSpace(style.Isbn))
            output.Append("{\\small ISBN: ").Append(RenderPlainText(style.Isbn.Trim())).AppendLine("\\par}");

        output.AppendLine("\\end{titlepage}");
        AppendPageBreak(output, style);
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

    private static void AppendChapter(StringBuilder output, string heading, string text, BookStyle style)
    {
        AppendPageBreak(output, style);
        output.Append("\\chapter*{").Append(EscapeLatex(heading)).AppendLine("}");
        if (style.GeneratedMatterInTableOfContents)
            output.Append("\\addcontentsline{toc}{chapter}{").Append(EscapeLatex(heading)).AppendLine("}");
        if (!string.IsNullOrWhiteSpace(text))
        {
            output.AppendLine(RenderPlainText(text));
            output.AppendLine();
        }
        AppendPageBreak(output, style);
    }

    private static void AppendBibliography(StringBuilder output, string bibliography, string heading, BookStyle style)
    {
        AppendPageBreak(output, style);
        output.Append("\\renewcommand{\\bibname}{").Append(EscapeLatex(heading)).AppendLine("}");
        if (style.GeneratedMatterInTableOfContents)
            output.Append("\\addcontentsline{toc}{chapter}{").Append(EscapeLatex(heading)).AppendLine("}");
        output.AppendLine(bibliography.Trim());
        output.AppendLine();
        AppendPageBreak(output, style);
    }

    private static void AppendPageBreak(StringBuilder output, BookStyle style)
        => output.AppendLine(style.StartGeneratedMatterOnRight ? "\\cleardoublepage" : "\\clearpage");

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

    private static IEnumerable<string> NormalizeOrder(string configured, IReadOnlyList<string> known)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (configured ?? string.Empty).Split([',', ';', '|', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = NormalizeToken(raw);
            if (known.Contains(token, StringComparer.OrdinalIgnoreCase) && seen.Add(token))
                yield return token;
        }

        foreach (var token in known)
            if (seen.Add(token)) yield return token;
    }

    private static string NormalizeToken(string value)
        => value.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');

    private static string EffectiveTitle(string projectTitle, BookStyle style)
        => string.IsNullOrWhiteSpace(style.PublishedTitle) ? projectTitle.Trim() : style.PublishedTitle.Trim();

    private static string Heading(string configured, string fallback)
        => string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();

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

    private static string ApplyPublishingMetadata(string latex, string projectTitle, BookStyle style)
    {
        var index = latex.IndexOf(BeginDocumentMarker, StringComparison.Ordinal);
        if (index < 0) return latex;

        var title = EffectiveTitle(projectTitle, style);
        var metadata = new StringBuilder();
        metadata.AppendLine();
        metadata.Append("\\hypersetup{pdftitle={").Append(EscapeLatex(title)).Append('}');
        if (!string.IsNullOrWhiteSpace(style.DisplayAuthor))
            metadata.Append(",pdfauthor={").Append(EscapeLatex(style.DisplayAuthor.Trim())).Append('}');
        metadata.AppendLine("}");

        return latex.Insert(index + BeginDocumentMarker.Length, metadata.ToString());
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
            var index = latex.IndexOf(BeginDocumentMarker, StringComparison.Ordinal);
            if (index >= 0)
            {
                var insert = index + BeginDocumentMarker.Length;
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
