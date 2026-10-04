using System.Globalization;
using System.Text;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

internal sealed class BookStyleStore
{
    private const string StyleFolder = "styles";
    private const string StyleFile = "book.style";

    public BookStyle Load(string projectRoot)
    {
        var path = GetPath(projectRoot);
        if (!File.Exists(path)) return BookStyle.Default;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        var d = BookStyle.Default;
        var style = d with
        {
            Name = Get(values, "name", d.Name),
            PublishedTitle = GetDecoded(values, "published-title", d.PublishedTitle),
            Subtitle = GetDecoded(values, "subtitle", d.Subtitle),
            DisplayAuthor = GetDecoded(values, "display-author", d.DisplayAuthor),
            SeriesTitle = GetDecoded(values, "series-title", d.SeriesTitle),
            VolumeLabel = GetDecoded(values, "volume-label", d.VolumeLabel),
            Edition = GetDecoded(values, "edition", d.Edition),
            Publisher = GetDecoded(values, "publisher", d.Publisher),
            Isbn = GetDecoded(values, "isbn", d.Isbn),

            DocumentClass = Get(values, "document-class", d.DocumentClass),
            DocumentClassOptions = Get(values, "document-class-options", d.DocumentClassOptions),
            PageWidthInches = GetDouble(values, "page-width-in", d.PageWidthInches),
            PageHeightInches = GetDouble(values, "page-height-in", d.PageHeightInches),
            MarginTopInches = GetDouble(values, "margin-top-in", d.MarginTopInches),
            MarginBottomInches = GetDouble(values, "margin-bottom-in", d.MarginBottomInches),
            MarginInnerInches = GetDouble(values, "margin-inner-in", d.MarginInnerInches),
            MarginOuterInches = GetDouble(values, "margin-outer-in", d.MarginOuterInches),
            IncludeTableOfContents = GetBool(values, "include-toc", d.IncludeTableOfContents),
            TableOfContentsDepth = GetInt(values, "toc-depth", d.TableOfContentsDepth),
            SectionNumberDepth = GetInt(values, "section-number-depth", d.SectionNumberDepth),
            OpenChaptersOnRight = GetBool(values, "open-chapters-right", d.OpenChaptersOnRight),

            FrontMatterOrder = GetDecoded(values, "front-matter-order", d.FrontMatterOrder),
            BackMatterOrder = GetDecoded(values, "back-matter-order", d.BackMatterOrder),
            UseRomanFrontMatterPageNumbers = GetBool(values, "roman-front-matter-pages", d.UseRomanFrontMatterPageNumbers),
            ResetBodyPageNumbers = GetBool(values, "reset-body-page-numbers", d.ResetBodyPageNumbers),
            GeneratedMatterInTableOfContents = GetBool(values, "generated-matter-in-toc", d.GeneratedMatterInTableOfContents),
            StartGeneratedMatterOnRight = GetBool(values, "generated-matter-open-right", d.StartGeneratedMatterOnRight),
            PrefaceHeading = GetDecoded(values, "preface-heading", d.PrefaceHeading),
            ForewordHeading = GetDecoded(values, "foreword-heading", d.ForewordHeading),
            IntroductionHeading = GetDecoded(values, "introduction-heading", d.IntroductionHeading),
            ConclusionHeading = GetDecoded(values, "conclusion-heading", d.ConclusionHeading),
            EpilogueHeading = GetDecoded(values, "epilogue-heading", d.EpilogueHeading),
            AcknowledgmentsHeading = GetDecoded(values, "acknowledgments-heading", d.AcknowledgmentsHeading),
            AppendixHeading = GetDecoded(values, "appendix-heading", d.AppendixHeading),
            GlossaryHeading = GetDecoded(values, "glossary-heading", d.GlossaryHeading),
            ReferencesHeading = GetDecoded(values, "references-heading", d.ReferencesHeading),
            IndexHeading = GetDecoded(values, "index-heading", d.IndexHeading),
            AboutAuthorHeading = GetDecoded(values, "about-author-heading", d.AboutAuthorHeading),

            IncludeFrontCover = GetBool(values, "include-front-cover", d.IncludeFrontCover),
            FrontCoverText = GetDecoded(values, "front-cover-text", d.FrontCoverText),
            IncludeSpine = GetBool(values, "include-spine", d.IncludeSpine),
            SpineText = GetDecoded(values, "spine-text", d.SpineText),
            FrontBlankPages = GetInt(values, "front-blank-pages", d.FrontBlankPages),
            IncludeTitlePage = GetBool(values, "include-title-page", d.IncludeTitlePage),
            IncludeCopyrightPage = GetBool(values, "include-copyright-page", d.IncludeCopyrightPage),
            CopyrightText = GetDecoded(values, "copyright-text", d.CopyrightText),
            IncludeDedication = GetBool(values, "include-dedication", d.IncludeDedication),
            DedicationText = GetDecoded(values, "dedication-text", d.DedicationText),
            IncludePreface = GetBool(values, "include-preface", d.IncludePreface),
            PrefaceText = GetDecoded(values, "preface-text", d.PrefaceText),
            IncludeForeword = GetBool(values, "include-foreword", d.IncludeForeword),
            ForewordText = GetDecoded(values, "foreword-text", d.ForewordText),
            IncludeIntroduction = GetBool(values, "include-introduction", d.IncludeIntroduction),
            IntroductionText = GetDecoded(values, "introduction-text", d.IntroductionText),

            IncludeConclusion = GetBool(values, "include-conclusion", d.IncludeConclusion),
            ConclusionText = GetDecoded(values, "conclusion-text", d.ConclusionText),
            IncludeEpilogue = GetBool(values, "include-epilogue", d.IncludeEpilogue),
            EpilogueText = GetDecoded(values, "epilogue-text", d.EpilogueText),
            IncludeAcknowledgments = GetBool(values, "include-acknowledgments", d.IncludeAcknowledgments),
            AcknowledgmentsText = GetDecoded(values, "acknowledgments-text", d.AcknowledgmentsText),
            IncludeAppendix = GetBool(values, "include-appendix", d.IncludeAppendix),
            AppendixText = GetDecoded(values, "appendix-text", d.AppendixText),
            IncludeGlossary = GetBool(values, "include-glossary", d.IncludeGlossary),
            GlossaryText = GetDecoded(values, "glossary-text", d.GlossaryText),
            IncludeReferencesBibliography = GetBool(values, "include-references-bibliography", d.IncludeReferencesBibliography),
            IncludeIndex = GetBool(values, "include-index", d.IncludeIndex),
            IndexText = GetDecoded(values, "index-text", d.IndexText),
            IncludeAboutAuthor = GetBool(values, "include-about-author", d.IncludeAboutAuthor),
            AboutAuthorText = GetDecoded(values, "about-author-text", d.AboutAuthorText),
            EndBlankPages = GetInt(values, "end-blank-pages", d.EndBlankPages),
            IncludeBackCover = GetBool(values, "include-back-cover", d.IncludeBackCover),
            BackCoverText = GetDecoded(values, "back-cover-text", d.BackCoverText),

            BodyFontFamily = Get(values, "body-font", d.BodyFontFamily),
            HeadingFontFamily = Get(values, "heading-font", d.HeadingFontFamily),
            MonospaceFontFamily = Get(values, "monospace-font", d.MonospaceFontFamily),
            MathFontFamily = Get(values, "math-font", d.MathFontFamily),
            BodyFontSizePoints = GetDouble(values, "body-font-size-pt", d.BodyFontSizePoints),
            LineSpacing = GetDouble(values, "line-spacing", d.LineSpacing),
            ParagraphIndentEm = GetDouble(values, "paragraph-indent-em", d.ParagraphIndentEm),
            ParagraphSpacingPoints = GetDouble(values, "paragraph-spacing-pt", d.ParagraphSpacingPoints),
            JustifyBody = GetBool(values, "justify-body", d.JustifyBody),
            BodyColorHex = Get(values, "body-color", d.BodyColorHex),
            HeadingColorHex = Get(values, "heading-color", d.HeadingColorHex),
            LinkColorHex = Get(values, "link-color", d.LinkColorHex),
            ColorLinks = GetBool(values, "color-links", d.ColorLinks),

            ChapterFontSizePoints = GetDouble(values, "chapter-font-size-pt", d.ChapterFontSizePoints),
            SectionFontSizePoints = GetDouble(values, "section-font-size-pt", d.SectionFontSizePoints),
            SubsectionFontSizePoints = GetDouble(values, "subsection-font-size-pt", d.SubsectionFontSizePoints),
            SubsubsectionFontSizePoints = GetDouble(values, "subsubsection-font-size-pt", d.SubsubsectionFontSizePoints),
            ChapterBeforeSpacingPoints = GetDouble(values, "chapter-before-pt", d.ChapterBeforeSpacingPoints),
            ChapterAfterSpacingPoints = GetDouble(values, "chapter-after-pt", d.ChapterAfterSpacingPoints),
            SectionBeforeSpacingPoints = GetDouble(values, "section-before-pt", d.SectionBeforeSpacingPoints),
            SectionAfterSpacingPoints = GetDouble(values, "section-after-pt", d.SectionAfterSpacingPoints),

            QuoteFontSizePoints = GetDouble(values, "quote-font-size-pt", d.QuoteFontSizePoints),
            QuoteItalic = GetBool(values, "quote-italic", d.QuoteItalic),
            QuoteIndentEm = GetDouble(values, "quote-indent-em", d.QuoteIndentEm),
            ListItemSpacingPoints = GetDouble(values, "list-item-spacing-pt", d.ListItemSpacingPoints),
            CaptionFontSizePoints = GetDouble(values, "caption-font-size-pt", d.CaptionFontSizePoints),
            FootnoteFontSizePoints = GetDouble(values, "footnote-font-size-pt", d.FootnoteFontSizePoints),

            CodeFontSizePoints = GetDouble(values, "code-font-size-pt", d.CodeFontSizePoints),
            CodeBackgroundHex = Get(values, "code-background", d.CodeBackgroundHex),
            CodeTextHex = Get(values, "code-text", d.CodeTextHex),
            CodeKeywordHex = Get(values, "code-keyword", d.CodeKeywordHex),
            CodeStringHex = Get(values, "code-string", d.CodeStringHex),
            CodeCommentHex = Get(values, "code-comment", d.CodeCommentHex),
            CodeFrameHex = Get(values, "code-frame", d.CodeFrameHex),
            CodeLineNumbers = GetBool(values, "code-line-numbers", d.CodeLineNumbers),

            HeaderFooterFontSizePoints = GetDouble(values, "header-footer-font-size-pt", d.HeaderFooterFontSizePoints),
            ShowHeadersAndFooters = GetBool(values, "show-headers-footers", d.ShowHeadersAndFooters),
            HeaderLeft = GetDecoded(values, "header-left", d.HeaderLeft),
            HeaderCenter = GetDecoded(values, "header-center", d.HeaderCenter),
            HeaderRight = GetDecoded(values, "header-right", d.HeaderRight),
            FooterLeft = GetDecoded(values, "footer-left", d.FooterLeft),
            FooterCenter = GetDecoded(values, "footer-center", d.FooterCenter),
            FooterRight = GetDecoded(values, "footer-right", d.FooterRight),
            ShowPageNumbers = GetBool(values, "show-page-numbers", d.ShowPageNumbers),

            EnableMicrotype = GetBool(values, "enable-microtype", d.EnableMicrotype),
            AvoidWidowsAndOrphans = GetBool(values, "avoid-widows-orphans", d.AvoidWidowsAndOrphans),
            ExtraPackages = GetDecoded(values, "extra-packages", d.ExtraPackages),
            CustomPreamble = GetDecoded(values, "custom-preamble", d.CustomPreamble)
        };
        return style.Validate();
    }

    public Task SaveAsync(string projectRoot, BookStyle style, CancellationToken cancellationToken = default)
    {
        style.Validate();
        var builder = new StringBuilder();
        builder.AppendLine("# Typescribe advanced book style v4");
        Append(builder, "name", style.Name);
        AppendEncoded(builder, "published-title", style.PublishedTitle);
        AppendEncoded(builder, "subtitle", style.Subtitle);
        AppendEncoded(builder, "display-author", style.DisplayAuthor);
        AppendEncoded(builder, "series-title", style.SeriesTitle);
        AppendEncoded(builder, "volume-label", style.VolumeLabel);
        AppendEncoded(builder, "edition", style.Edition);
        AppendEncoded(builder, "publisher", style.Publisher);
        AppendEncoded(builder, "isbn", style.Isbn);

        Append(builder, "document-class", style.DocumentClass);
        Append(builder, "document-class-options", style.DocumentClassOptions);
        Append(builder, "page-width-in", style.PageWidthInches);
        Append(builder, "page-height-in", style.PageHeightInches);
        Append(builder, "margin-top-in", style.MarginTopInches);
        Append(builder, "margin-bottom-in", style.MarginBottomInches);
        Append(builder, "margin-inner-in", style.MarginInnerInches);
        Append(builder, "margin-outer-in", style.MarginOuterInches);
        Append(builder, "include-toc", style.IncludeTableOfContents);
        Append(builder, "toc-depth", style.TableOfContentsDepth);
        Append(builder, "section-number-depth", style.SectionNumberDepth);
        Append(builder, "open-chapters-right", style.OpenChaptersOnRight);

        AppendEncoded(builder, "front-matter-order", style.FrontMatterOrder);
        AppendEncoded(builder, "back-matter-order", style.BackMatterOrder);
        Append(builder, "roman-front-matter-pages", style.UseRomanFrontMatterPageNumbers);
        Append(builder, "reset-body-page-numbers", style.ResetBodyPageNumbers);
        Append(builder, "generated-matter-in-toc", style.GeneratedMatterInTableOfContents);
        Append(builder, "generated-matter-open-right", style.StartGeneratedMatterOnRight);
        AppendEncoded(builder, "preface-heading", style.PrefaceHeading);
        AppendEncoded(builder, "foreword-heading", style.ForewordHeading);
        AppendEncoded(builder, "introduction-heading", style.IntroductionHeading);
        AppendEncoded(builder, "conclusion-heading", style.ConclusionHeading);
        AppendEncoded(builder, "epilogue-heading", style.EpilogueHeading);
        AppendEncoded(builder, "acknowledgments-heading", style.AcknowledgmentsHeading);
        AppendEncoded(builder, "appendix-heading", style.AppendixHeading);
        AppendEncoded(builder, "glossary-heading", style.GlossaryHeading);
        AppendEncoded(builder, "references-heading", style.ReferencesHeading);
        AppendEncoded(builder, "index-heading", style.IndexHeading);
        AppendEncoded(builder, "about-author-heading", style.AboutAuthorHeading);

        Append(builder, "include-front-cover", style.IncludeFrontCover);
        AppendEncoded(builder, "front-cover-text", style.FrontCoverText);
        Append(builder, "include-spine", style.IncludeSpine);
        AppendEncoded(builder, "spine-text", style.SpineText);
        Append(builder, "front-blank-pages", style.FrontBlankPages);
        Append(builder, "include-title-page", style.IncludeTitlePage);
        Append(builder, "include-copyright-page", style.IncludeCopyrightPage);
        AppendEncoded(builder, "copyright-text", style.CopyrightText);
        Append(builder, "include-dedication", style.IncludeDedication);
        AppendEncoded(builder, "dedication-text", style.DedicationText);
        Append(builder, "include-preface", style.IncludePreface);
        AppendEncoded(builder, "preface-text", style.PrefaceText);
        Append(builder, "include-foreword", style.IncludeForeword);
        AppendEncoded(builder, "foreword-text", style.ForewordText);
        Append(builder, "include-introduction", style.IncludeIntroduction);
        AppendEncoded(builder, "introduction-text", style.IntroductionText);

        Append(builder, "include-conclusion", style.IncludeConclusion);
        AppendEncoded(builder, "conclusion-text", style.ConclusionText);
        Append(builder, "include-epilogue", style.IncludeEpilogue);
        AppendEncoded(builder, "epilogue-text", style.EpilogueText);
        Append(builder, "include-acknowledgments", style.IncludeAcknowledgments);
        AppendEncoded(builder, "acknowledgments-text", style.AcknowledgmentsText);
        Append(builder, "include-appendix", style.IncludeAppendix);
        AppendEncoded(builder, "appendix-text", style.AppendixText);
        Append(builder, "include-glossary", style.IncludeGlossary);
        AppendEncoded(builder, "glossary-text", style.GlossaryText);
        Append(builder, "include-references-bibliography", style.IncludeReferencesBibliography);
        Append(builder, "include-index", style.IncludeIndex);
        AppendEncoded(builder, "index-text", style.IndexText);
        Append(builder, "include-about-author", style.IncludeAboutAuthor);
        AppendEncoded(builder, "about-author-text", style.AboutAuthorText);
        Append(builder, "end-blank-pages", style.EndBlankPages);
        Append(builder, "include-back-cover", style.IncludeBackCover);
        AppendEncoded(builder, "back-cover-text", style.BackCoverText);

        Append(builder, "body-font", style.BodyFontFamily);
        Append(builder, "heading-font", style.HeadingFontFamily);
        Append(builder, "monospace-font", style.MonospaceFontFamily);
        Append(builder, "math-font", style.MathFontFamily);
        Append(builder, "body-font-size-pt", style.BodyFontSizePoints);
        Append(builder, "line-spacing", style.LineSpacing);
        Append(builder, "paragraph-indent-em", style.ParagraphIndentEm);
        Append(builder, "paragraph-spacing-pt", style.ParagraphSpacingPoints);
        Append(builder, "justify-body", style.JustifyBody);
        Append(builder, "body-color", style.BodyColorHex);
        Append(builder, "heading-color", style.HeadingColorHex);
        Append(builder, "link-color", style.LinkColorHex);
        Append(builder, "color-links", style.ColorLinks);

        Append(builder, "chapter-font-size-pt", style.ChapterFontSizePoints);
        Append(builder, "section-font-size-pt", style.SectionFontSizePoints);
        Append(builder, "subsection-font-size-pt", style.SubsectionFontSizePoints);
        Append(builder, "subsubsection-font-size-pt", style.SubsubsectionFontSizePoints);
        Append(builder, "chapter-before-pt", style.ChapterBeforeSpacingPoints);
        Append(builder, "chapter-after-pt", style.ChapterAfterSpacingPoints);
        Append(builder, "section-before-pt", style.SectionBeforeSpacingPoints);
        Append(builder, "section-after-pt", style.SectionAfterSpacingPoints);

        Append(builder, "quote-font-size-pt", style.QuoteFontSizePoints);
        Append(builder, "quote-italic", style.QuoteItalic);
        Append(builder, "quote-indent-em", style.QuoteIndentEm);
        Append(builder, "list-item-spacing-pt", style.ListItemSpacingPoints);
        Append(builder, "caption-font-size-pt", style.CaptionFontSizePoints);
        Append(builder, "footnote-font-size-pt", style.FootnoteFontSizePoints);

        Append(builder, "code-font-size-pt", style.CodeFontSizePoints);
        Append(builder, "code-background", style.CodeBackgroundHex);
        Append(builder, "code-text", style.CodeTextHex);
        Append(builder, "code-keyword", style.CodeKeywordHex);
        Append(builder, "code-string", style.CodeStringHex);
        Append(builder, "code-comment", style.CodeCommentHex);
        Append(builder, "code-frame", style.CodeFrameHex);
        Append(builder, "code-line-numbers", style.CodeLineNumbers);

        Append(builder, "header-footer-font-size-pt", style.HeaderFooterFontSizePoints);
        Append(builder, "show-headers-footers", style.ShowHeadersAndFooters);
        AppendEncoded(builder, "header-left", style.HeaderLeft);
        AppendEncoded(builder, "header-center", style.HeaderCenter);
        AppendEncoded(builder, "header-right", style.HeaderRight);
        AppendEncoded(builder, "footer-left", style.FooterLeft);
        AppendEncoded(builder, "footer-center", style.FooterCenter);
        AppendEncoded(builder, "footer-right", style.FooterRight);
        Append(builder, "show-page-numbers", style.ShowPageNumbers);

        Append(builder, "enable-microtype", style.EnableMicrotype);
        Append(builder, "avoid-widows-orphans", style.AvoidWidowsAndOrphans);
        AppendEncoded(builder, "extra-packages", style.ExtraPackages);
        AppendEncoded(builder, "custom-preamble", style.CustomPreamble);

        return AtomicFileWriter.WriteTextAsync(GetPath(projectRoot), builder.ToString(), cancellationToken);
    }

    private static string GetPath(string projectRoot)
    {
        var folder = Path.Combine(projectRoot, StyleFolder);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, StyleFile);
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key, string fallback)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static string GetDecoded(IReadOnlyDictionary<string, string> values, string key, string fallback)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) return fallback;
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        catch (FormatException) { return fallback; }
    }

    private static double GetDouble(IReadOnlyDictionary<string, string> values, string key, double fallback)
        => values.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static int GetInt(IReadOnlyDictionary<string, string> values, string key, int fallback)
        => values.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static bool GetBool(IReadOnlyDictionary<string, string> values, string key, bool fallback)
        => values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static void Append(StringBuilder builder, string key, string value)
        => builder.Append(key).Append(" = ").AppendLine(value);

    private static void Append(StringBuilder builder, string key, double value)
        => Append(builder, key, value.ToString("0.###", CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, string key, int value)
        => Append(builder, key, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, string key, bool value)
        => Append(builder, key, value ? "true" : "false");

    private static void AppendEncoded(StringBuilder builder, string key, string value)
        => Append(builder, key, Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty)));
}
