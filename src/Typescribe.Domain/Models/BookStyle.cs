namespace Typescribe.Domain.Models;

public sealed record BookStyle
{
    public static BookStyle Default { get; } = new();

    public string Name { get; init; } = "Default";

    // Page and document structure.
    public string DocumentClass { get; init; } = "book";
    public string DocumentClassOptions { get; init; } = "11pt,openany";
    public double PageWidthInches { get; init; } = 6.0;
    public double PageHeightInches { get; init; } = 9.0;
    public double MarginTopInches { get; init; } = 0.8;
    public double MarginBottomInches { get; init; } = 0.8;
    public double MarginInnerInches { get; init; } = 0.85;
    public double MarginOuterInches { get; init; } = 0.7;
    public bool IncludeTableOfContents { get; init; }
    public int TableOfContentsDepth { get; init; } = 3;
    public int SectionNumberDepth { get; init; } = 3;
    public bool OpenChaptersOnRight { get; init; }

    // Generated front matter / cover proof pages.
    public bool IncludeFrontCover { get; init; }
    public string FrontCoverText { get; init; } = string.Empty;
    public bool IncludeSpine { get; init; }
    public string SpineText { get; init; } = string.Empty;
    public int FrontBlankPages { get; init; }
    public bool IncludeTitlePage { get; init; } = true;
    public bool IncludeCopyrightPage { get; init; }
    public string CopyrightText { get; init; } = string.Empty;
    public bool IncludeDedication { get; init; }
    public string DedicationText { get; init; } = string.Empty;
    public bool IncludePreface { get; init; }
    public string PrefaceText { get; init; } = string.Empty;
    public bool IncludeForeword { get; init; }
    public string ForewordText { get; init; } = string.Empty;
    public bool IncludeIntroduction { get; init; }
    public string IntroductionText { get; init; } = string.Empty;

    // Generated back matter.
    public bool IncludeConclusion { get; init; }
    public string ConclusionText { get; init; } = string.Empty;
    public bool IncludeEpilogue { get; init; }
    public string EpilogueText { get; init; } = string.Empty;
    public bool IncludeAcknowledgments { get; init; }
    public string AcknowledgmentsText { get; init; } = string.Empty;
    public bool IncludeAppendix { get; init; }
    public string AppendixText { get; init; } = string.Empty;
    public bool IncludeGlossary { get; init; }
    public string GlossaryText { get; init; } = string.Empty;
    public bool IncludeReferencesBibliography { get; init; } = true;
    public bool IncludeIndex { get; init; }
    public string IndexText { get; init; } = string.Empty;
    public bool IncludeAboutAuthor { get; init; }
    public string AboutAuthorText { get; init; } = string.Empty;
    public int EndBlankPages { get; init; }
    public bool IncludeBackCover { get; init; }
    public string BackCoverText { get; init; } = string.Empty;

    // Core typography.
    public string BodyFontFamily { get; init; } = "Latin Modern Roman";
    public string HeadingFontFamily { get; init; } = "Latin Modern Roman";
    public string MonospaceFontFamily { get; init; } = "Latin Modern Mono";
    public string MathFontFamily { get; init; } = "Latin Modern Math";
    public double BodyFontSizePoints { get; init; } = 11.0;
    public double LineSpacing { get; init; } = 1.08;
    public double ParagraphIndentEm { get; init; } = 1.25;
    public double ParagraphSpacingPoints { get; init; } = 0.0;
    public bool JustifyBody { get; init; } = true;
    public string BodyColorHex { get; init; } = "#202124";
    public string HeadingColorHex { get; init; } = "#202124";
    public string LinkColorHex { get; init; } = "#2563EB";
    public bool ColorLinks { get; init; }

    // Heading hierarchy.
    public double ChapterFontSizePoints { get; init; } = 24.0;
    public double SectionFontSizePoints { get; init; } = 16.0;
    public double SubsectionFontSizePoints { get; init; } = 13.0;
    public double SubsubsectionFontSizePoints { get; init; } = 11.5;
    public double ChapterBeforeSpacingPoints { get; init; } = 18.0;
    public double ChapterAfterSpacingPoints { get; init; } = 18.0;
    public double SectionBeforeSpacingPoints { get; init; } = 12.0;
    public double SectionAfterSpacingPoints { get; init; } = 6.0;

    // Quotes, lists, captions and notes.
    public double QuoteFontSizePoints { get; init; } = 10.5;
    public bool QuoteItalic { get; init; } = true;
    public double QuoteIndentEm { get; init; } = 1.5;
    public double ListItemSpacingPoints { get; init; } = 1.5;
    public double CaptionFontSizePoints { get; init; } = 9.5;
    public double FootnoteFontSizePoints { get; init; } = 8.5;

    // Code blocks and inline code.
    public double CodeFontSizePoints { get; init; } = 9.0;
    public string CodeBackgroundHex { get; init; } = "#F5F5F5";
    public string CodeTextHex { get; init; } = "#202124";
    public string CodeKeywordHex { get; init; } = "#7C3AED";
    public string CodeStringHex { get; init; } = "#047857";
    public string CodeCommentHex { get; init; } = "#6B7280";
    public string CodeFrameHex { get; init; } = "#D1D5DB";
    public bool CodeLineNumbers { get; init; }

    // Running heads / feet.
    public double HeaderFooterFontSizePoints { get; init; } = 9.0;
    public bool ShowHeadersAndFooters { get; init; } = true;
    public string HeaderLeft { get; init; } = string.Empty;
    public string HeaderCenter { get; init; } = string.Empty;
    public string HeaderRight { get; init; } = string.Empty;
    public string FooterLeft { get; init; } = string.Empty;
    public string FooterCenter { get; init; } = string.Empty;
    public string FooterRight { get; init; } = string.Empty;
    public bool ShowPageNumbers { get; init; } = true;

    // Advanced LuaLaTeX controls.
    public bool EnableMicrotype { get; init; } = true;
    public bool AvoidWidowsAndOrphans { get; init; } = true;
    public string ExtraPackages { get; init; } = string.Empty;
    public string CustomPreamble { get; init; } = string.Empty;

    public BookStyle Validate()
    {
        if (PageWidthInches is < 3 or > 20) throw new InvalidOperationException("Page width must be between 3 and 20 inches.");
        if (PageHeightInches is < 3 or > 24) throw new InvalidOperationException("Page height must be between 3 and 24 inches.");
        if (MarginTopInches is < 0 or > 5 || MarginBottomInches is < 0 or > 5 || MarginInnerInches is < 0 or > 5 || MarginOuterInches is < 0 or > 5)
            throw new InvalidOperationException("Margins must be between 0 and 5 inches.");
        if (FrontBlankPages is < 0 or > 20 || EndBlankPages is < 0 or > 20)
            throw new InvalidOperationException("Blank page counts must be between 0 and 20.");
        if (BodyFontSizePoints is < 6 or > 36) throw new InvalidOperationException("Body font size must be between 6 and 36 points.");
        if (LineSpacing is < 0.8 or > 3) throw new InvalidOperationException("Line spacing must be between 0.8 and 3.0.");
        if (TableOfContentsDepth is < 0 or > 6 || SectionNumberDepth is < -1 or > 6)
            throw new InvalidOperationException("TOC and section numbering depth are out of range.");

        ValidateFont(BodyFontFamily, "Body font");
        ValidateFont(HeadingFontFamily, "Heading font");
        ValidateFont(MonospaceFontFamily, "Monospace font");
        ValidateFont(MathFontFamily, "Math font");
        ValidatePointSize(ChapterFontSizePoints, "Chapter font size", 8, 72);
        ValidatePointSize(SectionFontSizePoints, "Section font size", 7, 48);
        ValidatePointSize(SubsectionFontSizePoints, "Subsection font size", 7, 36);
        ValidatePointSize(SubsubsectionFontSizePoints, "Subsubsection font size", 7, 30);
        ValidatePointSize(QuoteFontSizePoints, "Quote font size", 6, 36);
        ValidatePointSize(CodeFontSizePoints, "Code font size", 5, 30);
        ValidatePointSize(CaptionFontSizePoints, "Caption font size", 5, 24);
        ValidatePointSize(FootnoteFontSizePoints, "Footnote font size", 5, 24);
        ValidatePointSize(HeaderFooterFontSizePoints, "Header/footer font size", 5, 24);

        ValidateHex(BodyColorHex, "Body color");
        ValidateHex(HeadingColorHex, "Heading color");
        ValidateHex(LinkColorHex, "Link color");
        ValidateHex(CodeBackgroundHex, "Code background");
        ValidateHex(CodeTextHex, "Code text color");
        ValidateHex(CodeKeywordHex, "Code keyword color");
        ValidateHex(CodeStringHex, "Code string color");
        ValidateHex(CodeCommentHex, "Code comment color");
        ValidateHex(CodeFrameHex, "Code frame color");

        if (string.IsNullOrWhiteSpace(DocumentClass)) throw new InvalidOperationException("LaTeX document class is required.");
        return this;
    }

    private static void ValidateFont(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{name} is required.");
    }

    private static void ValidatePointSize(double value, string name, double min, double max)
    {
        if (value < min || value > max) throw new InvalidOperationException($"{name} must be between {min} and {max} points.");
    }

    private static void ValidateHex(string value, string name)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length != 7 || text[0] != '#' || !text[1..].All(Uri.IsHexDigit))
            throw new InvalidOperationException($"{name} must be a color such as #2563EB.");
    }
}
