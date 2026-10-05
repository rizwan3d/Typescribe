namespace Typescribe.Domain.Models;

/// <summary>
/// Reusable publishing styles. Every style may inherit from another style of the same kind;
/// null property values mean "inherit/default" rather than "clear".
/// </summary>
public sealed record NamedStyleCatalog(
    IReadOnlyList<ParagraphStyleDefinition> ParagraphStyles,
    IReadOnlyList<CharacterStyleDefinition> CharacterStyles,
    IReadOnlyList<FigureStyleDefinition> FigureStyles,
    IReadOnlyList<TableStyleDefinition> TableStyles,
    IReadOnlyList<CellStyleDefinition> CellStyles,
    IReadOnlyList<PageStyleDefinition> PageStyles)
{
    public static NamedStyleCatalog Default { get; } = new(
        [new("paragraph.body", "Body", null, FontSizePoints: 11, LineSpacing: 1.08, FirstLineIndentEm: 1.25, Alignment: TextAlignmentMode.Justified)],
        [new("character.default", "Default Character", null)],
        [new("figure.default", "Default Figure", null, MaxWidthPercent: 90, Alignment: FigureAlignment.Center, Placement: FigurePlacement.HereOrTop, CaptionPosition: CaptionPosition.Bottom, KeepWithCaption: true)],
        [new("table.default", "Default Table", null, WidthPercent: 100, Layout: TableLayoutMode.Auto, RepeatHeader: true, HeaderCellStyleId: "cell.header", BodyCellStyleId: "cell.body")],
        [
            new("cell.body", "Body Cell", null, HorizontalAlignment: TableCellHorizontalAlignment.Left, VerticalAlignment: TableCellVerticalAlignment.Top, PaddingPoints: 4),
            new("cell.header", "Header Cell", "cell.body", Bold: true, BackgroundColorHex: "#F3F4F6")
        ],
        [new("page.book", "Book Page", null, WidthInches: 6, HeightInches: 9, MarginTopInches: .8, MarginBottomInches: .8, MarginInnerInches: .85, MarginOuterInches: .7)]);

    public NamedStyleCatalog Validate()
    {
        ValidateCollection(ParagraphStyles, static style => style.Id, static style => style.BasedOn, "paragraph");
        ValidateCollection(CharacterStyles, static style => style.Id, static style => style.BasedOn, "character");
        ValidateCollection(FigureStyles, static style => style.Id, static style => style.BasedOn, "figure");
        ValidateCollection(TableStyles, static style => style.Id, static style => style.BasedOn, "table");
        ValidateCollection(CellStyles, static style => style.Id, static style => style.BasedOn, "cell");
        ValidateCollection(PageStyles, static style => style.Id, static style => style.BasedOn, "page");
        return this;
    }

    public ParagraphStyleDefinition? ResolveParagraph(string? id) => Resolve(ParagraphStyles, id, Merge);
    public CharacterStyleDefinition? ResolveCharacter(string? id) => Resolve(CharacterStyles, id, Merge);
    public FigureStyleDefinition? ResolveFigure(string? id) => Resolve(FigureStyles, id, Merge);
    public TableStyleDefinition? ResolveTable(string? id) => Resolve(TableStyles, id, Merge);
    public CellStyleDefinition? ResolveCell(string? id) => Resolve(CellStyles, id, Merge);
    public PageStyleDefinition? ResolvePage(string? id) => Resolve(PageStyles, id, Merge);

    private static T? Resolve<T>(IReadOnlyList<T> styles, string? id, Func<T, T, T> merge)
        where T : class, INamedStyleDefinition
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var map = styles.ToDictionary(static style => style.Id, StringComparer.OrdinalIgnoreCase);
        if (!map.TryGetValue(id.Trim(), out var current)) return null;

        var chain = new Stack<T>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            if (!seen.Add(current.Id)) throw new InvalidOperationException($"Style inheritance cycle detected at '{current.Id}'.");
            chain.Push(current);
            if (string.IsNullOrWhiteSpace(current.BasedOn) || !map.TryGetValue(current.BasedOn, out var parent)) break;
            current = parent;
        }

        var resolved = chain.Pop();
        while (chain.Count > 0) resolved = merge(resolved, chain.Pop());
        return resolved;
    }

    private static void ValidateCollection<T>(
        IReadOnlyList<T> styles,
        Func<T, string> id,
        Func<T, string?> basedOn,
        string kind)
        where T : class
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var style in styles)
        {
            var value = id(style)?.Trim() ?? string.Empty;
            if (value.Length == 0) throw new InvalidOperationException($"Every {kind} style needs an ID.");
            if (!ids.Add(value)) throw new InvalidOperationException($"Duplicate {kind} style ID '{value}'.");
        }

        foreach (var style in styles)
        {
            var parent = basedOn(style);
            if (!string.IsNullOrWhiteSpace(parent) && !ids.Contains(parent))
                throw new InvalidOperationException($"{kind} style '{id(style)}' inherits missing style '{parent}'.");
        }

        foreach (var style in styles)
        {
            var current = id(style);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (!seen.Add(current)) throw new InvalidOperationException($"{kind} style inheritance contains a cycle at '{current}'.");
                var match = styles.FirstOrDefault(candidate => string.Equals(id(candidate), current, StringComparison.OrdinalIgnoreCase));
                if (match is null) break;
                current = basedOn(match);
            }
        }
    }

    private static ParagraphStyleDefinition Merge(ParagraphStyleDefinition parent, ParagraphStyleDefinition child)
        => child with
        {
            BasedOn = null,
            FontFamily = child.FontFamily ?? parent.FontFamily,
            FontSizePoints = child.FontSizePoints ?? parent.FontSizePoints,
            LineSpacing = child.LineSpacing ?? parent.LineSpacing,
            SpaceBeforePoints = child.SpaceBeforePoints ?? parent.SpaceBeforePoints,
            SpaceAfterPoints = child.SpaceAfterPoints ?? parent.SpaceAfterPoints,
            FirstLineIndentEm = child.FirstLineIndentEm ?? parent.FirstLineIndentEm,
            Alignment = child.Alignment ?? parent.Alignment,
            KeepWithNext = child.KeepWithNext ?? parent.KeepWithNext,
            KeepLinesTogether = child.KeepLinesTogether ?? parent.KeepLinesTogether,
            FontSource = child.FontSource ?? parent.FontSource,
            FontPath = child.FontPath ?? parent.FontPath,
            Language = child.Language ?? parent.Language,
            Script = child.Script ?? parent.Script,
            Direction = child.Direction ?? parent.Direction,
            OpticalMarginAlignment = child.OpticalMarginAlignment ?? parent.OpticalMarginAlignment,
            Hyphenation = child.Hyphenation ?? parent.Hyphenation,
            DropCap = child.DropCap ?? parent.DropCap,
            Rules = child.Rules ?? parent.Rules,
            Tabs = child.Tabs ?? parent.Tabs,
            KeepFirstLines = child.KeepFirstLines ?? parent.KeepFirstLines,
            KeepLastLines = child.KeepLastLines ?? parent.KeepLastLines
        };

    private static CharacterStyleDefinition Merge(CharacterStyleDefinition parent, CharacterStyleDefinition child)
        => child with
        {
            BasedOn = null,
            FontFamily = child.FontFamily ?? parent.FontFamily,
            FontSizePoints = child.FontSizePoints ?? parent.FontSizePoints,
            Bold = child.Bold ?? parent.Bold,
            Italic = child.Italic ?? parent.Italic,
            Underline = child.Underline ?? parent.Underline,
            SmallCaps = child.SmallCaps ?? parent.SmallCaps,
            ColorHex = child.ColorHex ?? parent.ColorHex,
            LetterSpacingEm = child.LetterSpacingEm ?? parent.LetterSpacingEm,
            FontSource = child.FontSource ?? parent.FontSource,
            FontPath = child.FontPath ?? parent.FontPath,
            Ligatures = child.Ligatures ?? parent.Ligatures,
            Kerning = child.Kerning ?? parent.Kerning,
            TrackingEm = child.TrackingEm ?? parent.TrackingEm,
            BaselineShiftPoints = child.BaselineShiftPoints ?? parent.BaselineShiftPoints,
            Language = child.Language ?? parent.Language,
            Script = child.Script ?? parent.Script,
            Direction = child.Direction ?? parent.Direction,
            OpenTypeFeatures = child.OpenTypeFeatures ?? parent.OpenTypeFeatures,
            VariableAxes = child.VariableAxes ?? parent.VariableAxes
        };

    private static FigureStyleDefinition Merge(FigureStyleDefinition parent, FigureStyleDefinition child)
        => child with
        {
            BasedOn = null,
            MaxWidthPercent = child.MaxWidthPercent ?? parent.MaxWidthPercent,
            Alignment = child.Alignment ?? parent.Alignment,
            Placement = child.Placement ?? parent.Placement,
            CaptionPosition = child.CaptionPosition ?? parent.CaptionPosition,
            KeepWithCaption = child.KeepWithCaption ?? parent.KeepWithCaption,
            BorderWidthPoints = child.BorderWidthPoints ?? parent.BorderWidthPoints,
            BorderColorHex = child.BorderColorHex ?? parent.BorderColorHex,
            PaddingPoints = child.PaddingPoints ?? parent.PaddingPoints
        };

    private static TableStyleDefinition Merge(TableStyleDefinition parent, TableStyleDefinition child)
        => child with
        {
            BasedOn = null,
            WidthPercent = child.WidthPercent ?? parent.WidthPercent,
            Layout = child.Layout ?? parent.Layout,
            RepeatHeader = child.RepeatHeader ?? parent.RepeatHeader,
            HeaderCellStyleId = child.HeaderCellStyleId ?? parent.HeaderCellStyleId,
            BodyCellStyleId = child.BodyCellStyleId ?? parent.BodyCellStyleId,
            AlternateRowCellStyleId = child.AlternateRowCellStyleId ?? parent.AlternateRowCellStyleId,
            KeepTogether = child.KeepTogether ?? parent.KeepTogether,
            CaptionPosition = child.CaptionPosition ?? parent.CaptionPosition,
            RepeatHeaderRows = child.RepeatHeaderRows ?? parent.RepeatHeaderRows,
            AllowRowBreakAcrossPages = child.AllowRowBreakAcrossPages ?? parent.AllowRowBreakAcrossPages,
            KeepRowsTogether = child.KeepRowsTogether ?? parent.KeepRowsTogether
        };

    private static CellStyleDefinition Merge(CellStyleDefinition parent, CellStyleDefinition child)
        => child with
        {
            BasedOn = null,
            HorizontalAlignment = child.HorizontalAlignment ?? parent.HorizontalAlignment,
            VerticalAlignment = child.VerticalAlignment ?? parent.VerticalAlignment,
            BackgroundColorHex = child.BackgroundColorHex ?? parent.BackgroundColorHex,
            TextColorHex = child.TextColorHex ?? parent.TextColorHex,
            PaddingPoints = child.PaddingPoints ?? parent.PaddingPoints,
            BorderWidthPoints = child.BorderWidthPoints ?? parent.BorderWidthPoints,
            BorderColorHex = child.BorderColorHex ?? parent.BorderColorHex,
            Bold = child.Bold ?? parent.Bold,
            NumberFormat = child.NumberFormat ?? parent.NumberFormat,
            FontFamily = child.FontFamily ?? parent.FontFamily,
            FontSizePoints = child.FontSizePoints ?? parent.FontSizePoints,
            Language = child.Language ?? parent.Language,
            Script = child.Script ?? parent.Script,
            Direction = child.Direction ?? parent.Direction
        };

    private static PageStyleDefinition Merge(PageStyleDefinition parent, PageStyleDefinition child)
        => child with
        {
            BasedOn = null,
            WidthInches = child.WidthInches ?? parent.WidthInches,
            HeightInches = child.HeightInches ?? parent.HeightInches,
            MarginTopInches = child.MarginTopInches ?? parent.MarginTopInches,
            MarginBottomInches = child.MarginBottomInches ?? parent.MarginBottomInches,
            MarginInnerInches = child.MarginInnerInches ?? parent.MarginInnerInches,
            MarginOuterInches = child.MarginOuterInches ?? parent.MarginOuterInches,
            Landscape = child.Landscape ?? parent.Landscape,
            StartOnRight = child.StartOnRight ?? parent.StartOnRight,
            FacingPages = child.FacingPages ?? parent.FacingPages,
            BleedTopInches = child.BleedTopInches ?? parent.BleedTopInches,
            BleedBottomInches = child.BleedBottomInches ?? parent.BleedBottomInches,
            BleedInsideInches = child.BleedInsideInches ?? parent.BleedInsideInches,
            BleedOutsideInches = child.BleedOutsideInches ?? parent.BleedOutsideInches,
            SlugInches = child.SlugInches ?? parent.SlugInches,
            CropMarks = child.CropMarks ?? parent.CropMarks,
            BaselineGrid = child.BaselineGrid ?? parent.BaselineGrid
        };
}

public interface INamedStyleDefinition
{
    string Id { get; }
    string Name { get; }
    string? BasedOn { get; }
}

public enum TextAlignmentMode { Left, Center, Right, Justified }
public enum FigureAlignment { Left, Center, Right }
public enum FigurePlacement { Inline, Here, HereOrTop, Top, Bottom, Page }
public enum CaptionPosition { Top, Bottom }
public enum TableLayoutMode { Auto, Fixed, FitToPage }
public enum TableCellHorizontalAlignment { Left, Center, Right, Decimal }
public enum TableCellVerticalAlignment { Top, Middle, Bottom }

public sealed record ParagraphStyleDefinition(
    string Id,
    string Name,
    string? BasedOn,
    string? FontFamily = null,
    double? FontSizePoints = null,
    double? LineSpacing = null,
    double? SpaceBeforePoints = null,
    double? SpaceAfterPoints = null,
    double? FirstLineIndentEm = null,
    TextAlignmentMode? Alignment = null,
    bool? KeepWithNext = null,
    bool? KeepLinesTogether = null,
    FontSourceKind? FontSource = null,
    string? FontPath = null,
    string? Language = null,
    ScriptMode? Script = null,
    TextDirectionMode? Direction = null,
    bool? OpticalMarginAlignment = null,
    bool? Hyphenation = null,
    DropCapFormatting? DropCap = null,
    IReadOnlyList<ParagraphRule>? Rules = null,
    IReadOnlyList<TabStop>? Tabs = null,
    int? KeepFirstLines = null,
    int? KeepLastLines = null) : INamedStyleDefinition;

public sealed record CharacterStyleDefinition(
    string Id,
    string Name,
    string? BasedOn,
    string? FontFamily = null,
    double? FontSizePoints = null,
    bool? Bold = null,
    bool? Italic = null,
    bool? Underline = null,
    bool? SmallCaps = null,
    string? ColorHex = null,
    double? LetterSpacingEm = null,
    FontSourceKind? FontSource = null,
    string? FontPath = null,
    bool? Ligatures = null,
    bool? Kerning = null,
    double? TrackingEm = null,
    double? BaselineShiftPoints = null,
    string? Language = null,
    ScriptMode? Script = null,
    TextDirectionMode? Direction = null,
    IReadOnlyList<OpenTypeFeatureSetting>? OpenTypeFeatures = null,
    IReadOnlyList<VariableFontAxisSetting>? VariableAxes = null) : INamedStyleDefinition;

public sealed record FigureStyleDefinition(
    string Id,
    string Name,
    string? BasedOn,
    double? MaxWidthPercent = null,
    FigureAlignment? Alignment = null,
    FigurePlacement? Placement = null,
    CaptionPosition? CaptionPosition = null,
    bool? KeepWithCaption = null,
    double? BorderWidthPoints = null,
    string? BorderColorHex = null,
    double? PaddingPoints = null) : INamedStyleDefinition;

public sealed record TableStyleDefinition(
    string Id,
    string Name,
    string? BasedOn,
    double? WidthPercent = null,
    TableLayoutMode? Layout = null,
    bool? RepeatHeader = null,
    string? HeaderCellStyleId = null,
    string? BodyCellStyleId = null,
    string? AlternateRowCellStyleId = null,
    bool? KeepTogether = null,
    CaptionPosition? CaptionPosition = null,
    int? RepeatHeaderRows = null,
    bool? AllowRowBreakAcrossPages = null,
    bool? KeepRowsTogether = null) : INamedStyleDefinition;

public sealed record CellStyleDefinition(
    string Id,
    string Name,
    string? BasedOn,
    TableCellHorizontalAlignment? HorizontalAlignment = null,
    TableCellVerticalAlignment? VerticalAlignment = null,
    string? BackgroundColorHex = null,
    string? TextColorHex = null,
    double? PaddingPoints = null,
    double? BorderWidthPoints = null,
    string? BorderColorHex = null,
    bool? Bold = null,
    string? NumberFormat = null,
    string? FontFamily = null,
    double? FontSizePoints = null,
    string? Language = null,
    ScriptMode? Script = null,
    TextDirectionMode? Direction = null) : INamedStyleDefinition;

public sealed record PageStyleDefinition(
    string Id,
    string Name,
    string? BasedOn,
    double? WidthInches = null,
    double? HeightInches = null,
    double? MarginTopInches = null,
    double? MarginBottomInches = null,
    double? MarginInnerInches = null,
    double? MarginOuterInches = null,
    bool? Landscape = null,
    bool? StartOnRight = null,
    bool? FacingPages = null,
    double? BleedTopInches = null,
    double? BleedBottomInches = null,
    double? BleedInsideInches = null,
    double? BleedOutsideInches = null,
    double? SlugInches = null,
    bool? CropMarks = null,
    BaselineGridFormatting? BaselineGrid = null) : INamedStyleDefinition;
