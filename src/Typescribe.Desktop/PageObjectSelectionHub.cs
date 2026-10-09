using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

internal sealed record PageFigureSelection(
    int SourceLine,
    string? Identifier,
    string Source,
    string Caption,
    FigureLayout? Layout,
    AnchoredObjectFormatting? Anchored,
    double WidthPoints,
    double HeightPoints);

internal sealed record PageTextFrameSelection(
    int SourceLine,
    string FrameId,
    string? NextFrameId,
    int Columns,
    double ColumnGapPoints,
    double InsetTopPoints,
    double InsetRightPoints,
    double InsetBottomPoints,
    double InsetLeftPoints,
    double? XPoints,
    double? YPoints,
    double? WidthPoints,
    double? HeightPoints,
    int PageOffset,
    bool Overset);

internal enum PageFurnitureKind
{
    ParentPage,
    HeaderLeft,
    HeaderCenter,
    HeaderRight,
    FooterLeft,
    FooterCenter,
    FooterRight,
    PageNumber
}

internal sealed record PageFurnitureSelection(
    int PageIndex,
    int DisplayPageNumber,
    string DisplayPageNumberText,
    string? PageStyleId,
    bool IsLeftPage,
    PageFurnitureKind Kind,
    string Value);

internal static class PageObjectSelectionHub
{
    public static event Action<PageFigureSelection?>? FigureSelectionChanged;
    public static event Action<PageTextFrameSelection?>? TextFrameSelectionChanged;
    public static event Action<PageFurnitureSelection?>? FurnitureSelectionChanged;

    public static PageFigureSelection? SelectedFigure { get; private set; }
    public static PageTextFrameSelection? SelectedTextFrame { get; private set; }
    public static PageFurnitureSelection? SelectedFurniture { get; private set; }

    public static void SelectFigure(PageFigureSelection? selection)
    {
        SelectedFigure = selection;
        if (selection is not null && SelectedTextFrame is not null)
        {
            SelectedTextFrame = null;
            TextFrameSelectionChanged?.Invoke(null);
        }
        if (selection is not null && SelectedFurniture is not null)
        {
            SelectedFurniture = null;
            FurnitureSelectionChanged?.Invoke(null);
        }
        FigureSelectionChanged?.Invoke(selection);
    }

    public static void SelectTextFrame(PageTextFrameSelection? selection)
    {
        SelectedTextFrame = selection;
        if (selection is not null && SelectedFigure is not null)
        {
            SelectedFigure = null;
            FigureSelectionChanged?.Invoke(null);
        }
        if (selection is not null && SelectedFurniture is not null)
        {
            SelectedFurniture = null;
            FurnitureSelectionChanged?.Invoke(null);
        }
        TextFrameSelectionChanged?.Invoke(selection);
    }

    public static void SelectFurniture(PageFurnitureSelection? selection)
    {
        SelectedFurniture = selection;
        if (selection is not null && SelectedFigure is not null)
        {
            SelectedFigure = null;
            FigureSelectionChanged?.Invoke(null);
        }
        if (selection is not null && SelectedTextFrame is not null)
        {
            SelectedTextFrame = null;
            TextFrameSelectionChanged?.Invoke(null);
        }
        FurnitureSelectionChanged?.Invoke(selection);
    }

    public static void Clear()
    {
        SelectedFigure = null;
        SelectedTextFrame = null;
        SelectedFurniture = null;
        FigureSelectionChanged?.Invoke(null);
        TextFrameSelectionChanged?.Invoke(null);
        FurnitureSelectionChanged?.Invoke(null);
    }
}
