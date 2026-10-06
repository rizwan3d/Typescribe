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

internal static class PageObjectSelectionHub
{
    public static event Action<PageFigureSelection?>? FigureSelectionChanged;
    public static event Action<PageTextFrameSelection?>? TextFrameSelectionChanged;

    public static PageFigureSelection? SelectedFigure { get; private set; }
    public static PageTextFrameSelection? SelectedTextFrame { get; private set; }

    public static void SelectFigure(PageFigureSelection? selection)
    {
        SelectedFigure = selection;
        if (selection is not null && SelectedTextFrame is not null)
        {
            SelectedTextFrame = null;
            TextFrameSelectionChanged?.Invoke(null);
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
        TextFrameSelectionChanged?.Invoke(selection);
    }

    public static void Clear()
    {
        SelectedFigure = null;
        SelectedTextFrame = null;
        FigureSelectionChanged?.Invoke(null);
        TextFrameSelectionChanged?.Invoke(null);
    }
}
