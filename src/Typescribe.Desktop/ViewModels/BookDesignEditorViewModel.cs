using System.Globalization;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop.ViewModels;

/// <summary>
/// Mutable editing model for BookStyle. Numeric values are kept as text while the dialog is
/// open so incomplete input such as "1." does not immediately mutate the immutable project
/// style. ToStyle performs all parsing and domain validation atomically when Apply/Save runs.
/// </summary>
public sealed class BookDesignEditorViewModel
{
    public string Name { get; set; } = string.Empty;
    public string DocumentClass { get; set; } = string.Empty;
    public string DocumentClassOptions { get; set; } = string.Empty;
    public string PageWidthInches { get; set; } = string.Empty;
    public string PageHeightInches { get; set; } = string.Empty;
    public string MarginTopInches { get; set; } = string.Empty;
    public string MarginBottomInches { get; set; } = string.Empty;
    public string MarginInnerInches { get; set; } = string.Empty;
    public string MarginOuterInches { get; set; } = string.Empty;
    public bool IncludeTableOfContents { get; set; }
    public string TableOfContentsDepth { get; set; } = string.Empty;
    public string SectionNumberDepth { get; set; } = string.Empty;
    public bool OpenChaptersOnRight { get; set; }

    public string BodyFontFamily { get; set; } = string.Empty;
    public string HeadingFontFamily { get; set; } = string.Empty;
    public string MonospaceFontFamily { get; set; } = string.Empty;
    public string MathFontFamily { get; set; } = string.Empty;
    public string BodyFontSizePoints { get; set; } = string.Empty;
    public string LineSpacing { get; set; } = string.Empty;
    public string ParagraphIndentEm { get; set; } = string.Empty;
    public string ParagraphSpacingPoints { get; set; } = string.Empty;
    public bool JustifyBody { get; set; }
    public string BodyColorHex { get; set; } = string.Empty;
    public string HeadingColorHex { get; set; } = string.Empty;
    public string LinkColorHex { get; set; } = string.Empty;
    public bool ColorLinks { get; set; }

    public string ChapterFontSizePoints { get; set; } = string.Empty;
    public string SectionFontSizePoints { get; set; } = string.Empty;
    public string SubsectionFontSizePoints { get; set; } = string.Empty;
    public string SubsubsectionFontSizePoints { get; set; } = string.Empty;
    public string ChapterBeforeSpacingPoints { get; set; } = string.Empty;
    public string ChapterAfterSpacingPoints { get; set; } = string.Empty;
    public string SectionBeforeSpacingPoints { get; set; } = string.Empty;
    public string SectionAfterSpacingPoints { get; set; } = string.Empty;

    public string QuoteFontSizePoints { get; set; } = string.Empty;
    public bool QuoteItalic { get; set; }
    public string QuoteIndentEm { get; set; } = string.Empty;
    public string ListItemSpacingPoints { get; set; } = string.Empty;
    public string CaptionFontSizePoints { get; set; } = string.Empty;
    public string FootnoteFontSizePoints { get; set; } = string.Empty;

    public string CodeFontSizePoints { get; set; } = string.Empty;
    public string CodeBackgroundHex { get; set; } = string.Empty;
    public string CodeTextHex { get; set; } = string.Empty;
    public string CodeKeywordHex { get; set; } = string.Empty;
    public string CodeStringHex { get; set; } = string.Empty;
    public string CodeCommentHex { get; set; } = string.Empty;
    public string CodeFrameHex { get; set; } = string.Empty;
    public bool CodeLineNumbers { get; set; }

    public string HeaderFooterFontSizePoints { get; set; } = string.Empty;
    public string HeaderLeft { get; set; } = string.Empty;
    public string HeaderCenter { get; set; } = string.Empty;
    public string HeaderRight { get; set; } = string.Empty;
    public string FooterLeft { get; set; } = string.Empty;
    public string FooterCenter { get; set; } = string.Empty;
    public string FooterRight { get; set; } = string.Empty;
    public bool ShowPageNumbers { get; set; }

    public bool EnableMicrotype { get; set; }
    public bool AvoidWidowsAndOrphans { get; set; }
    public string ExtraPackages { get; set; } = string.Empty;
    public string CustomPreamble { get; set; } = string.Empty;

    public static BookDesignEditorViewModel FromStyle(BookStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return new BookDesignEditorViewModel
        {
            Name = style.Name,
            DocumentClass = style.DocumentClass,
            DocumentClassOptions = style.DocumentClassOptions,
            PageWidthInches = Number(style.PageWidthInches),
            PageHeightInches = Number(style.PageHeightInches),
            MarginTopInches = Number(style.MarginTopInches),
            MarginBottomInches = Number(style.MarginBottomInches),
            MarginInnerInches = Number(style.MarginInnerInches),
            MarginOuterInches = Number(style.MarginOuterInches),
            IncludeTableOfContents = style.IncludeTableOfContents,
            TableOfContentsDepth = style.TableOfContentsDepth.ToString(CultureInfo.InvariantCulture),
            SectionNumberDepth = style.SectionNumberDepth.ToString(CultureInfo.InvariantCulture),
            OpenChaptersOnRight = style.OpenChaptersOnRight,

            BodyFontFamily = style.BodyFontFamily,
            HeadingFontFamily = style.HeadingFontFamily,
            MonospaceFontFamily = style.MonospaceFontFamily,
            MathFontFamily = style.MathFontFamily,
            BodyFontSizePoints = Number(style.BodyFontSizePoints),
            LineSpacing = Number(style.LineSpacing),
            ParagraphIndentEm = Number(style.ParagraphIndentEm),
            ParagraphSpacingPoints = Number(style.ParagraphSpacingPoints),
            JustifyBody = style.JustifyBody,
            BodyColorHex = style.BodyColorHex,
            HeadingColorHex = style.HeadingColorHex,
            LinkColorHex = style.LinkColorHex,
            ColorLinks = style.ColorLinks,

            ChapterFontSizePoints = Number(style.ChapterFontSizePoints),
            SectionFontSizePoints = Number(style.SectionFontSizePoints),
            SubsectionFontSizePoints = Number(style.SubsectionFontSizePoints),
            SubsubsectionFontSizePoints = Number(style.SubsubsectionFontSizePoints),
            ChapterBeforeSpacingPoints = Number(style.ChapterBeforeSpacingPoints),
            ChapterAfterSpacingPoints = Number(style.ChapterAfterSpacingPoints),
            SectionBeforeSpacingPoints = Number(style.SectionBeforeSpacingPoints),
            SectionAfterSpacingPoints = Number(style.SectionAfterSpacingPoints),

            QuoteFontSizePoints = Number(style.QuoteFontSizePoints),
            QuoteItalic = style.QuoteItalic,
            QuoteIndentEm = Number(style.QuoteIndentEm),
            ListItemSpacingPoints = Number(style.ListItemSpacingPoints),
            CaptionFontSizePoints = Number(style.CaptionFontSizePoints),
            FootnoteFontSizePoints = Number(style.FootnoteFontSizePoints),

            CodeFontSizePoints = Number(style.CodeFontSizePoints),
            CodeBackgroundHex = style.CodeBackgroundHex,
            CodeTextHex = style.CodeTextHex,
            CodeKeywordHex = style.CodeKeywordHex,
            CodeStringHex = style.CodeStringHex,
            CodeCommentHex = style.CodeCommentHex,
            CodeFrameHex = style.CodeFrameHex,
            CodeLineNumbers = style.CodeLineNumbers,

            HeaderFooterFontSizePoints = Number(style.HeaderFooterFontSizePoints),
            HeaderLeft = style.HeaderLeft,
            HeaderCenter = style.HeaderCenter,
            HeaderRight = style.HeaderRight,
            FooterLeft = style.FooterLeft,
            FooterCenter = style.FooterCenter,
            FooterRight = style.FooterRight,
            ShowPageNumbers = style.ShowPageNumbers,
            EnableMicrotype = style.EnableMicrotype,
            AvoidWidowsAndOrphans = style.AvoidWidowsAndOrphans,
            ExtraPackages = RemoveUnsafeUnicodeMathPackages(style.ExtraPackages),
            CustomPreamble = style.CustomPreamble
        };
    }

    public BookStyle ToStyle()
        => (new BookStyle
        {
            Name = Required(Name, "Style name"),
            DocumentClass = Required(DocumentClass, "Document class"),
            DocumentClassOptions = DocumentClassOptions.Trim(),
            PageWidthInches = Double(PageWidthInches, "Page width"),
            PageHeightInches = Double(PageHeightInches, "Page height"),
            MarginTopInches = Double(MarginTopInches, "Top margin"),
            MarginBottomInches = Double(MarginBottomInches, "Bottom margin"),
            MarginInnerInches = Double(MarginInnerInches, "Inner margin"),
            MarginOuterInches = Double(MarginOuterInches, "Outer margin"),
            IncludeTableOfContents = IncludeTableOfContents,
            TableOfContentsDepth = Integer(TableOfContentsDepth, "TOC depth"),
            SectionNumberDepth = Integer(SectionNumberDepth, "Section number depth"),
            OpenChaptersOnRight = OpenChaptersOnRight,

            BodyFontFamily = Required(BodyFontFamily, "Body font"),
            HeadingFontFamily = Required(HeadingFontFamily, "Heading font"),
            MonospaceFontFamily = Required(MonospaceFontFamily, "Monospace font"),
            MathFontFamily = Required(MathFontFamily, "Math font"),
            BodyFontSizePoints = Double(BodyFontSizePoints, "Body size"),
            LineSpacing = Double(LineSpacing, "Line spacing"),
            ParagraphIndentEm = Double(ParagraphIndentEm, "Paragraph indent"),
            ParagraphSpacingPoints = Double(ParagraphSpacingPoints, "Paragraph spacing"),
            JustifyBody = JustifyBody,
            BodyColorHex = Required(BodyColorHex, "Body color"),
            HeadingColorHex = Required(HeadingColorHex, "Heading color"),
            LinkColorHex = Required(LinkColorHex, "Link color"),
            ColorLinks = ColorLinks,

            ChapterFontSizePoints = Double(ChapterFontSizePoints, "Chapter size"),
            SectionFontSizePoints = Double(SectionFontSizePoints, "Section size"),
            SubsectionFontSizePoints = Double(SubsectionFontSizePoints, "Subsection size"),
            SubsubsectionFontSizePoints = Double(SubsubsectionFontSizePoints, "Subsubsection size"),
            ChapterBeforeSpacingPoints = Double(ChapterBeforeSpacingPoints, "Chapter spacing before"),
            ChapterAfterSpacingPoints = Double(ChapterAfterSpacingPoints, "Chapter spacing after"),
            SectionBeforeSpacingPoints = Double(SectionBeforeSpacingPoints, "Section spacing before"),
            SectionAfterSpacingPoints = Double(SectionAfterSpacingPoints, "Section spacing after"),

            QuoteFontSizePoints = Double(QuoteFontSizePoints, "Quote size"),
            QuoteItalic = QuoteItalic,
            QuoteIndentEm = Double(QuoteIndentEm, "Quote indent"),
            ListItemSpacingPoints = Double(ListItemSpacingPoints, "List item spacing"),
            CaptionFontSizePoints = Double(CaptionFontSizePoints, "Caption size"),
            FootnoteFontSizePoints = Double(FootnoteFontSizePoints, "Footnote size"),

            CodeFontSizePoints = Double(CodeFontSizePoints, "Code size"),
            CodeBackgroundHex = Required(CodeBackgroundHex, "Code background"),
            CodeTextHex = Required(CodeTextHex, "Code text"),
            CodeKeywordHex = Required(CodeKeywordHex, "Code keyword"),
            CodeStringHex = Required(CodeStringHex, "Code string"),
            CodeCommentHex = Required(CodeCommentHex, "Code comment"),
            CodeFrameHex = Required(CodeFrameHex, "Code frame"),
            CodeLineNumbers = CodeLineNumbers,

            HeaderFooterFontSizePoints = Double(HeaderFooterFontSizePoints, "Header/footer size"),
            HeaderLeft = HeaderLeft,
            HeaderCenter = HeaderCenter,
            HeaderRight = HeaderRight,
            FooterLeft = FooterLeft,
            FooterCenter = FooterCenter,
            FooterRight = FooterRight,
            ShowPageNumbers = ShowPageNumbers,
            EnableMicrotype = EnableMicrotype,
            AvoidWidowsAndOrphans = AvoidWidowsAndOrphans,
            ExtraPackages = RemoveUnsafeUnicodeMathPackages(ExtraPackages),
            CustomPreamble = CustomPreamble
        }).Validate();

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Required(string value, string label)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0) throw new InvalidOperationException($"{label} is required.");
        return text;
    }

    private static double Double(string value, string label)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var local)) return local;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant)) return invariant;
        throw new FormatException($"{label} must be a number.");
    }

    private static int Integer(string value, string label)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var local)) return local;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var invariant)) return invariant;
        throw new FormatException($"{label} must be an integer.");
    }

    private static string RemoveUnsafeUnicodeMathPackages(string value)
    {
        var packages = value.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static package => !string.Equals(package, "amssymb", StringComparison.OrdinalIgnoreCase) &&
                                     !string.Equals(package, "amsfonts", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join(Environment.NewLine, packages);
    }
}
