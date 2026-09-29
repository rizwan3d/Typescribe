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
        builder.AppendLine("# Typescribe advanced book style v2");
        Append(builder, "name", style.Name);
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
