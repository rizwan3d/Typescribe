namespace Typescribe.Domain.Models;

public sealed record BookStyle
{
    public static BookStyle Default { get; } = new();

    public string Name { get; init; } = "Default";
    public double PageWidthInches { get; init; } = 6.0;
    public double PageHeightInches { get; init; } = 9.0;
    public double MarginTopInches { get; init; } = 0.8;
    public double MarginBottomInches { get; init; } = 0.8;
    public double MarginInnerInches { get; init; } = 0.85;
    public double MarginOuterInches { get; init; } = 0.7;
    public string BodyFontFamily { get; init; } = "Latin Modern Roman";
    public double BodyFontSizePoints { get; init; } = 11.0;
    public double LineSpacing { get; init; } = 1.08;
    public double ParagraphIndentEm { get; init; } = 1.25;
    public double ParagraphSpacingPoints { get; init; } = 0.0;
    public bool JustifyBody { get; init; } = true;

    public BookStyle Validate()
    {
        if (PageWidthInches is < 3 or > 20) throw new InvalidOperationException("Page width must be between 3 and 20 inches.");
        if (PageHeightInches is < 3 or > 24) throw new InvalidOperationException("Page height must be between 3 and 24 inches.");
        if (MarginTopInches is < 0 or > 5 || MarginBottomInches is < 0 or > 5 || MarginInnerInches is < 0 or > 5 || MarginOuterInches is < 0 or > 5)
            throw new InvalidOperationException("Margins must be between 0 and 5 inches.");
        if (BodyFontSizePoints is < 6 or > 36) throw new InvalidOperationException("Body font size must be between 6 and 36 points.");
        if (LineSpacing is < 0.8 or > 3) throw new InvalidOperationException("Line spacing must be between 0.8 and 3.0.");
        if (string.IsNullOrWhiteSpace(BodyFontFamily)) throw new InvalidOperationException("Body font family is required.");
        return this;
    }
}
