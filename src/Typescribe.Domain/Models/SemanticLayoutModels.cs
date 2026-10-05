namespace Typescribe.Domain.Models;

public enum FigureSourceKind
{
    ProjectAsset,
    ProjectRelative,
    ExternalUri,
    Generated
}

public enum FigureFitMode
{
    Native,
    Contain,
    Cover,
    Fill
}

/// <summary>Instance-level figure layout overrides. Named FigureStyleDefinition remains the reusable base.</summary>
public sealed record FigureLayout(
    double? WidthPercent = null,
    double? HeightInches = null,
    FigureAlignment? Alignment = null,
    FigurePlacement? Placement = null,
    FigureFitMode Fit = FigureFitMode.Contain,
    bool KeepWithCaption = true,
    bool AllowPageBreak = true,
    double RotationDegrees = 0);

/// <summary>Instance-level table overrides applied after the reusable table style.</summary>
public sealed record TableProperties(
    double? WidthPercent = null,
    TableLayoutMode? Layout = null,
    CaptionPosition? CaptionPosition = null,
    bool? KeepTogether = null,
    bool AllowRowBreakAcrossPages = true);

public sealed record TableRowProperties(
    string? StyleId = null,
    bool KeepTogether = false,
    bool AllowBreakAcrossPages = true,
    bool RepeatAsHeader = false,
    double? MinimumHeightPoints = null);

public sealed record TableCellProperties(
    TableCellHorizontalAlignment? HorizontalAlignment = null,
    TableCellVerticalAlignment? VerticalAlignment = null,
    string? BackgroundColorHex = null,
    string? TextColorHex = null,
    double? PaddingPoints = null,
    double? BorderWidthPoints = null,
    string? BorderColorHex = null,
    bool? Bold = null,
    string? NumberFormat = null);
