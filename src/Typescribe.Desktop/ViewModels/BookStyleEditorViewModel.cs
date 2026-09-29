using System.ComponentModel;
using System.Runtime.CompilerServices;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop.ViewModels;

public sealed class BookStyleEditorViewModel : INotifyPropertyChanged
{
    private string _errorMessage = string.Empty;

    public BookStyleEditorViewModel(BookStyle source) => Load(source);

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; set; } = string.Empty;
    public string DocumentClass { get; set; } = string.Empty;
    public string DocumentClassOptions { get; set; } = string.Empty;
    public decimal PageWidthInches { get; set; }
    public decimal PageHeightInches { get; set; }
    public decimal MarginTopInches { get; set; }
    public decimal MarginBottomInches { get; set; }
    public decimal MarginInnerInches { get; set; }
    public decimal MarginOuterInches { get; set; }
    public bool IncludeTableOfContents { get; set; }
    public decimal TableOfContentsDepth { get; set; }
    public decimal SectionNumberDepth { get; set; }
    public bool OpenChaptersOnRight { get; set; }

    public string BodyFontFamily { get; set; } = string.Empty;
    public string HeadingFontFamily { get; set; } = string.Empty;
    public string MonospaceFontFamily { get; set; } = string.Empty;
    public string MathFontFamily { get; set; } = string.Empty;
    public decimal BodyFontSizePoints { get; set; }
    public decimal LineSpacing { get; set; }
    public decimal ParagraphIndentEm { get; set; }
    public decimal ParagraphSpacingPoints { get; set; }
    public bool JustifyBody { get; set; }
    public string BodyColorHex { get; set; } = string.Empty;
    public string HeadingColorHex { get; set; } = string.Empty;
    public string LinkColorHex { get; set; } = string.Empty;
    public bool ColorLinks { get; set; }

    public decimal ChapterFontSizePoints { get; set; }
    public decimal SectionFontSizePoints { get; set; }
    public decimal SubsectionFontSizePoints { get; set; }
    public decimal SubsubsectionFontSizePoints { get; set; }
    public decimal ChapterBeforeSpacingPoints { get; set; }
    public decimal ChapterAfterSpacingPoints { get; set; }
    public decimal SectionBeforeSpacingPoints { get; set; }
    public decimal SectionAfterSpacingPoints { get; set; }

    public decimal QuoteFontSizePoints { get; set; }
    public bool QuoteItalic { get; set; }
    public decimal QuoteIndentEm { get; set; }
    public decimal ListItemSpacingPoints { get; set; }
    public decimal CaptionFontSizePoints { get; set; }
    public decimal FootnoteFontSizePoints { get; set; }

    public decimal CodeFontSizePoints { get; set; }
    public string CodeBackgroundHex { get; set; } = string.Empty;
    public string CodeTextHex { get; set; } = string.Empty;
    public string CodeKeywordHex { get; set; } = string.Empty;
    public string CodeStringHex { get; set; } = string.Empty;
    public string CodeCommentHex { get; set; } = string.Empty;
    public string CodeFrameHex { get; set; } = string.Empty;
    public bool CodeLineNumbers { get; set; }

    public decimal HeaderFooterFontSizePoints { get; set; }
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

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (string.Equals(_errorMessage, value, StringComparison.Ordinal)) return;
            _errorMessage = value;
            OnPropertyChanged();
        }
    }

    public void ResetToDefaults() => Load(BookStyle.Default);

    public bool TryBuild(out BookStyle style)
    {
        try
        {
            style = BuildStyle();
            ErrorMessage = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            style = BookStyle.Default;
            ErrorMessage = ex.Message;
            return false;
        }
    }

    public BookStyle BuildStyle()
        => new BookStyle
        {
            Name = Required(Name, "Style name"),
            DocumentClass = Required(DocumentClass, "Document class"),
            DocumentClassOptions = DocumentClassOptions.Trim(),
            PageWidthInches = D(PageWidthInches),
            PageHeightInches = D(PageHeightInches),
            MarginTopInches = D(MarginTopInches),
            MarginBottomInches = D(MarginBottomInches),
            MarginInnerInches = D(MarginInnerInches),
            MarginOuterInches = D(MarginOuterInches),
            IncludeTableOfContents = IncludeTableOfContents,
            TableOfContentsDepth = I(TableOfContentsDepth),
            SectionNumberDepth = I(SectionNumberDepth),
            OpenChaptersOnRight = OpenChaptersOnRight,

            BodyFontFamily = Required(BodyFontFamily, "Body font"),
            HeadingFontFamily = Required(HeadingFontFamily, "Heading font"),
            MonospaceFontFamily = Required(MonospaceFontFamily, "Monospace font"),
            MathFontFamily = Required(MathFontFamily, "Math font"),
            BodyFontSizePoints = D(BodyFontSizePoints),
            LineSpacing = D(LineSpacing),
            ParagraphIndentEm = D(ParagraphIndentEm),
            ParagraphSpacingPoints = D(ParagraphSpacingPoints),
            JustifyBody = JustifyBody,
            BodyColorHex = Required(BodyColorHex, "Body color"),
            HeadingColorHex = Required(HeadingColorHex, "Heading color"),
            LinkColorHex = Required(LinkColorHex, "Link color"),
            ColorLinks = ColorLinks,

            ChapterFontSizePoints = D(ChapterFontSizePoints),
            SectionFontSizePoints = D(SectionFontSizePoints),
            SubsectionFontSizePoints = D(SubsectionFontSizePoints),
            SubsubsectionFontSizePoints = D(SubsubsectionFontSizePoints),
            ChapterBeforeSpacingPoints = D(ChapterBeforeSpacingPoints),
            ChapterAfterSpacingPoints = D(ChapterAfterSpacingPoints),
            SectionBeforeSpacingPoints = D(SectionBeforeSpacingPoints),
            SectionAfterSpacingPoints = D(SectionAfterSpacingPoints),

            QuoteFontSizePoints = D(QuoteFontSizePoints),
            QuoteItalic = QuoteItalic,
            QuoteIndentEm = D(QuoteIndentEm),
            ListItemSpacingPoints = D(ListItemSpacingPoints),
            CaptionFontSizePoints = D(CaptionFontSizePoints),
            FootnoteFontSizePoints = D(FootnoteFontSizePoints),

            CodeFontSizePoints = D(CodeFontSizePoints),
            CodeBackgroundHex = Required(CodeBackgroundHex, "Code background"),
            CodeTextHex = Required(CodeTextHex, "Code text color"),
            CodeKeywordHex = Required(CodeKeywordHex, "Code keyword color"),
            CodeStringHex = Required(CodeStringHex, "Code string color"),
            CodeCommentHex = Required(CodeCommentHex, "Code comment color"),
            CodeFrameHex = Required(CodeFrameHex, "Code frame color"),
            CodeLineNumbers = CodeLineNumbers,

            HeaderFooterFontSizePoints = D(HeaderFooterFontSizePoints),
            HeaderLeft = HeaderLeft,
            HeaderCenter = HeaderCenter,
            HeaderRight = HeaderRight,
            FooterLeft = FooterLeft,
            FooterCenter = FooterCenter,
            FooterRight = FooterRight,
            ShowPageNumbers = ShowPageNumbers,

            EnableMicrotype = EnableMicrotype,
            AvoidWidowsAndOrphans = AvoidWidowsAndOrphans,
            ExtraPackages = ExtraPackages,
            CustomPreamble = CustomPreamble
        }.Validate();

    private void Load(BookStyle style)
    {
        Name = style.Name;
        DocumentClass = style.DocumentClass;
        DocumentClassOptions = style.DocumentClassOptions;
        PageWidthInches = M(style.PageWidthInches);
        PageHeightInches = M(style.PageHeightInches);
        MarginTopInches = M(style.MarginTopInches);
        MarginBottomInches = M(style.MarginBottomInches);
        MarginInnerInches = M(style.MarginInnerInches);
        MarginOuterInches = M(style.MarginOuterInches);
        IncludeTableOfContents = style.IncludeTableOfContents;
        TableOfContentsDepth = style.TableOfContentsDepth;
        SectionNumberDepth = style.SectionNumberDepth;
        OpenChaptersOnRight = style.OpenChaptersOnRight;

        BodyFontFamily = style.BodyFontFamily;
        HeadingFontFamily = style.HeadingFontFamily;
        MonospaceFontFamily = style.MonospaceFontFamily;
        MathFontFamily = style.MathFontFamily;
        BodyFontSizePoints = M(style.BodyFontSizePoints);
        LineSpacing = M(style.LineSpacing);
        ParagraphIndentEm = M(style.ParagraphIndentEm);
        ParagraphSpacingPoints = M(style.ParagraphSpacingPoints);
        JustifyBody = style.JustifyBody;
        BodyColorHex = style.BodyColorHex;
        HeadingColorHex = style.HeadingColorHex;
        LinkColorHex = style.LinkColorHex;
        ColorLinks = style.ColorLinks;

        ChapterFontSizePoints = M(style.ChapterFontSizePoints);
        SectionFontSizePoints = M(style.SectionFontSizePoints);
        SubsectionFontSizePoints = M(style.SubsectionFontSizePoints);
        SubsubsectionFontSizePoints = M(style.SubsubsectionFontSizePoints);
        ChapterBeforeSpacingPoints = M(style.ChapterBeforeSpacingPoints);
        ChapterAfterSpacingPoints = M(style.ChapterAfterSpacingPoints);
        SectionBeforeSpacingPoints = M(style.SectionBeforeSpacingPoints);
        SectionAfterSpacingPoints = M(style.SectionAfterSpacingPoints);

        QuoteFontSizePoints = M(style.QuoteFontSizePoints);
        QuoteItalic = style.QuoteItalic;
        QuoteIndentEm = M(style.QuoteIndentEm);
        ListItemSpacingPoints = M(style.ListItemSpacingPoints);
        CaptionFontSizePoints = M(style.CaptionFontSizePoints);
        FootnoteFontSizePoints = M(style.FootnoteFontSizePoints);

        CodeFontSizePoints = M(style.CodeFontSizePoints);
        CodeBackgroundHex = style.CodeBackgroundHex;
        CodeTextHex = style.CodeTextHex;
        CodeKeywordHex = style.CodeKeywordHex;
        CodeStringHex = style.CodeStringHex;
        CodeCommentHex = style.CodeCommentHex;
        CodeFrameHex = style.CodeFrameHex;
        CodeLineNumbers = style.CodeLineNumbers;

        HeaderFooterFontSizePoints = M(style.HeaderFooterFontSizePoints);
        HeaderLeft = style.HeaderLeft;
        HeaderCenter = style.HeaderCenter;
        HeaderRight = style.HeaderRight;
        FooterLeft = style.FooterLeft;
        FooterCenter = style.FooterCenter;
        FooterRight = style.FooterRight;
        ShowPageNumbers = style.ShowPageNumbers;

        EnableMicrotype = style.EnableMicrotype;
        AvoidWidowsAndOrphans = style.AvoidWidowsAndOrphans;
        ExtraPackages = style.ExtraPackages;
        CustomPreamble = style.CustomPreamble;
        ErrorMessage = string.Empty;
        OnPropertyChanged(string.Empty);
    }

    private static string Required(string value, string label)
    {
        var result = value?.Trim() ?? string.Empty;
        if (result.Length == 0) throw new InvalidOperationException($"{label} is required.");
        return result;
    }

    private static decimal M(double value) => Convert.ToDecimal(value);
    private static double D(decimal value) => Convert.ToDouble(value);
    private static int I(decimal value) => decimal.ToInt32(decimal.Truncate(value));

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
