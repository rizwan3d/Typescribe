namespace Typescribe.Domain.Models;

/// <summary>
/// Markdown-first rich-document semantics. These records describe formatting and page-layout
/// intent without making the persisted manuscript binary or editor-specific. Typescribe stores
/// the values in ignorable Markdown HTML comments, while ordinary Markdown readers continue to
/// see the authored text.
/// </summary>
public enum TextDirectionMode
{
    Auto,
    LeftToRight,
    RightToLeft
}

public enum ScriptMode
{
    Auto,
    Latin,
    Arabic,
    UrduNastaliq,
    Persian,
    Hebrew,
    Devanagari,
    Cjk
}

public enum FontSourceKind
{
    System,
    Project,
    Embedded
}

public enum TabStopAlignment
{
    Left,
    Center,
    Right,
    Decimal
}

public enum ParagraphRulePosition
{
    Above,
    Below
}

public enum TextWrapMode
{
    None,
    BoundingBox,
    Contour,
    JumpObject
}

public enum SectionStartMode
{
    Continuous,
    NextPage,
    NextOddPage,
    NextEvenPage
}

public enum PageNumberStyle
{
    Arabic,
    LowerRoman,
    UpperRoman,
    LowerLetters,
    UpperLetters
}

public enum FloatPlacementMode
{
    Inline,
    Here,
    Top,
    Bottom,
    Page,
    Margin
}

public sealed record FontReference(
    string Family,
    FontSourceKind Source = FontSourceKind.System,
    string? ProjectPath = null,
    IReadOnlyList<string>? FallbackFamilies = null);

public sealed record OpenTypeFeatureSetting(string Tag, int Value = 1);
public sealed record VariableFontAxisSetting(string Tag, double Value);

public sealed record CharacterFormatting(
    string? StyleId = null,
    FontReference? Font = null,
    double? FontSizePoints = null,
    bool? Bold = null,
    bool? Italic = null,
    bool? Underline = null,
    bool? SmallCaps = null,
    bool? Ligatures = null,
    bool? Kerning = null,
    double? TrackingEm = null,
    double? BaselineShiftPoints = null,
    string? ColorHex = null,
    string? Language = null,
    ScriptMode? Script = null,
    TextDirectionMode? Direction = null,
    IReadOnlyList<OpenTypeFeatureSetting>? OpenTypeFeatures = null,
    IReadOnlyList<VariableFontAxisSetting>? VariableAxes = null);

public sealed record TabStop(
    double PositionPoints,
    TabStopAlignment Alignment = TabStopAlignment.Left,
    char? Leader = null);

public sealed record ParagraphRule(
    ParagraphRulePosition Position,
    double WidthPoints = .5,
    string? ColorHex = null,
    double OffsetPoints = 0,
    double LeftInsetPoints = 0,
    double RightInsetPoints = 0);

public sealed record DropCapFormatting(
    int Lines = 2,
    int Characters = 1,
    double? GapPoints = null,
    CharacterFormatting? Character = null);

public sealed record ParagraphFormatting(
    string? StyleId = null,
    CharacterFormatting? CharacterDefaults = null,
    TextAlignmentMode? Alignment = null,
    TextDirectionMode? Direction = null,
    string? Language = null,
    ScriptMode? Script = null,
    double? LineSpacing = null,
    double? SpaceBeforePoints = null,
    double? SpaceAfterPoints = null,
    double? FirstLineIndentPoints = null,
    double? LeftIndentPoints = null,
    double? RightIndentPoints = null,
    bool? KeepWithNext = null,
    bool? KeepLinesTogether = null,
    int? KeepFirstLines = null,
    int? KeepLastLines = null,
    bool? OpticalMarginAlignment = null,
    bool? Hyphenation = null,
    DropCapFormatting? DropCap = null,
    IReadOnlyList<ParagraphRule>? Rules = null,
    IReadOnlyList<TabStop>? Tabs = null);

public sealed record BaselineGridFormatting(
    bool Enabled = false,
    double IncrementPoints = 12,
    double StartPoints = 0);

public sealed record SectionFormatting(
    string? StyleId = null,
    int Columns = 1,
    double ColumnGapPoints = 18,
    SectionStartMode Start = SectionStartMode.Continuous,
    string? PageStyleId = null,
    PageNumberStyle PageNumberStyle = PageNumberStyle.Arabic,
    int? PageNumberStart = null,
    BaselineGridFormatting? BaselineGrid = null,
    bool FacingPages = false,
    double BleedTopPoints = 0,
    double BleedBottomPoints = 0,
    double BleedInsidePoints = 0,
    double BleedOutsidePoints = 0,
    double SlugPoints = 0,
    bool CropMarks = false);

public sealed record TextFrameFormatting(
    string Id,
    string? NextFrameId = null,
    int Columns = 1,
    double ColumnGapPoints = 12,
    double InsetTopPoints = 0,
    double InsetRightPoints = 0,
    double InsetBottomPoints = 0,
    double InsetLeftPoints = 0,
    TextDirectionMode Direction = TextDirectionMode.Auto,
    BaselineGridFormatting? BaselineGrid = null);

public sealed record AnchoredObjectFormatting(
    string Id,
    FloatPlacementMode Placement = FloatPlacementMode.Inline,
    TextWrapMode Wrap = TextWrapMode.None,
    double OffsetXPoints = 0,
    double OffsetYPoints = 0,
    double WrapTopPoints = 0,
    double WrapRightPoints = 0,
    double WrapBottomPoints = 0,
    double WrapLeftPoints = 0,
    bool KeepWithAnchor = true);

public sealed record RichBlockFormatting(
    ParagraphFormatting? Paragraph = null,
    SectionFormatting? Section = null,
    TextFrameFormatting? TextFrame = null,
    AnchoredObjectFormatting? AnchoredObject = null);

/// <summary>
/// Script heuristics used by the editor and exporters. Unicode BiDi remains the authority for
/// mixed-direction runs; this helper only supplies a sensible paragraph default.
/// </summary>
public static class UnicodeScriptClassifier
{
    public static bool IsArabicScript(char value)
        => value is >= '\u0600' and <= '\u06FF'
            or >= '\u0750' and <= '\u077F'
            or >= '\u08A0' and <= '\u08FF'
            or >= '\uFB50' and <= '\uFDFF'
            or >= '\uFE70' and <= '\uFEFF';

    public static TextDirectionMode DetectDirection(string? text)
    {
        if (string.IsNullOrEmpty(text)) return TextDirectionMode.Auto;
        foreach (var value in text)
        {
            if (IsArabicScript(value) || value is >= '\u0590' and <= '\u05FF')
                return TextDirectionMode.RightToLeft;
            if (char.IsLetter(value)) return TextDirectionMode.LeftToRight;
        }
        return TextDirectionMode.Auto;
    }

    public static ScriptMode DetectScript(string? text, string? language = null)
    {
        if (!string.IsNullOrWhiteSpace(language))
        {
            if (language.StartsWith("ur", StringComparison.OrdinalIgnoreCase)) return ScriptMode.UrduNastaliq;
            if (language.StartsWith("fa", StringComparison.OrdinalIgnoreCase)) return ScriptMode.Persian;
            if (language.StartsWith("ar", StringComparison.OrdinalIgnoreCase)) return ScriptMode.Arabic;
        }

        if (string.IsNullOrEmpty(text)) return ScriptMode.Auto;
        foreach (var value in text)
            if (IsArabicScript(value)) return ScriptMode.Arabic;
        return ScriptMode.Latin;
    }
}
