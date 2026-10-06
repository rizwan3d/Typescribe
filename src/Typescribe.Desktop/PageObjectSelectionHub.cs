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

internal static class PageObjectSelectionHub
{
    public static event Action<PageFigureSelection?>? FigureSelectionChanged;

    public static PageFigureSelection? SelectedFigure { get; private set; }

    public static void SelectFigure(PageFigureSelection? selection)
    {
        SelectedFigure = selection;
        FigureSelectionChanged?.Invoke(selection);
    }
}
