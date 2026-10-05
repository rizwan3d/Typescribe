using System.Globalization;
using System.Text;

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
/// Unicode first-strong-direction and script heuristics shared by the editor and exporters.
/// This classifier never reorders, normalizes, joins, or otherwise mutates authored text: the
/// platform text formatter remains responsible for the full Unicode BiDi algorithm and OpenType
/// shaping. These helpers choose only the paragraph base direction and a sensible script/font hint.
/// </summary>
public static class UnicodeScriptClassifier
{
    private static readonly int[] UrduDistinctCodepoints =
    [
        0x0679, // TTEH
        0x0688, // DDAL
        0x0691, // RREH
        0x06BA, // NOON GHUNNA
        0x06BE, // HEH DOACHASHMEE
        0x06D2, // YEH BARREE
        0x06D3  // YEH BARREE WITH HAMZA ABOVE
    ];

    public static bool IsArabicScript(char value) => IsArabicScript(new Rune(value));

    public static bool IsArabicScript(Rune value)
    {
        var codepoint = value.Value;
        return codepoint is >= 0x0600 and <= 0x06FF
            or >= 0x0750 and <= 0x077F
            or >= 0x0870 and <= 0x089F
            or >= 0x08A0 and <= 0x08FF
            or >= 0xFB50 and <= 0xFDFF
            or >= 0xFE70 and <= 0xFEFF
            or >= 0x1EE00 and <= 0x1EEFF;
    }

    public static bool ContainsArabicScript(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var rune in text.EnumerateRunes())
            if (IsArabicScript(rune)) return true;
        return false;
    }

    public static bool ContainsUrduDistinctCharacters(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var rune in text.EnumerateRunes())
            if (UrduDistinctCodepoints.Contains(rune.Value)) return true;
        return false;
    }

    /// <summary>
    /// Implements the paragraph-level first-strong rule used to choose a base direction.
    /// Neutral punctuation, Markdown markers, emoji and digits are ignored until a strong script
    /// character is found. Full mixed-run ordering is delegated to Avalonia's text formatter.
    /// </summary>
    public static TextDirectionMode DetectDirection(string? text)
    {
        if (string.IsNullOrEmpty(text)) return TextDirectionMode.Auto;

        foreach (var rune in text.EnumerateRunes())
        {
            if (IsRightToLeftStrong(rune)) return TextDirectionMode.RightToLeft;
            if (IsLeftToRightStrong(rune)) return TextDirectionMode.LeftToRight;
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
            if (language.StartsWith("he", StringComparison.OrdinalIgnoreCase) ||
                language.StartsWith("yi", StringComparison.OrdinalIgnoreCase)) return ScriptMode.Hebrew;
        }

        if (string.IsNullOrEmpty(text)) return ScriptMode.Auto;
        if (ContainsUrduDistinctCharacters(text)) return ScriptMode.UrduNastaliq;

        foreach (var rune in text.EnumerateRunes())
        {
            if (IsHebrew(rune)) return ScriptMode.Hebrew;
            if (IsArabicScript(rune))
                return ContainsPersianDistinctCharacters(text) ? ScriptMode.Persian : ScriptMode.Arabic;
            if (IsDevanagari(rune)) return ScriptMode.Devanagari;
            if (IsCjk(rune)) return ScriptMode.Cjk;
            if (Rune.IsLetter(rune)) return ScriptMode.Latin;
        }

        return ScriptMode.Auto;
    }

    public static bool IsRtlLanguage(string? language)
        => !string.IsNullOrWhiteSpace(language) &&
           (language.StartsWith("ar", StringComparison.OrdinalIgnoreCase) ||
            language.StartsWith("ur", StringComparison.OrdinalIgnoreCase) ||
            language.StartsWith("fa", StringComparison.OrdinalIgnoreCase) ||
            language.StartsWith("he", StringComparison.OrdinalIgnoreCase) ||
            language.StartsWith("yi", StringComparison.OrdinalIgnoreCase) ||
            language.StartsWith("ps", StringComparison.OrdinalIgnoreCase) ||
            language.StartsWith("sd", StringComparison.OrdinalIgnoreCase));

    private static bool IsRightToLeftStrong(Rune rune)
        => IsArabicScript(rune) || IsHebrew(rune) || IsSyriac(rune) || IsThaana(rune) || IsNko(rune);

    private static bool IsLeftToRightStrong(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.LetterNumber;
    }

    private static bool IsHebrew(Rune rune) => rune.Value is >= 0x0590 and <= 0x05FF;
    private static bool IsSyriac(Rune rune) => rune.Value is >= 0x0700 and <= 0x074F;
    private static bool IsThaana(Rune rune) => rune.Value is >= 0x0780 and <= 0x07BF;
    private static bool IsNko(Rune rune) => rune.Value is >= 0x07C0 and <= 0x07FF;
    private static bool IsDevanagari(Rune rune) => rune.Value is >= 0x0900 and <= 0x097F;

    private static bool IsCjk(Rune rune)
        => rune.Value is >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF
            or >= 0x20000 and <= 0x2FA1F;

    private static bool ContainsPersianDistinctCharacters(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is 0x067E or 0x0686 or 0x0698 or 0x06AF)
                return true;
        }
        return false;
    }
}
