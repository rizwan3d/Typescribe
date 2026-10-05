using Avalonia;
using Avalonia.Media;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Keeps rich block rendering calls concise when a fractional corner radius is expressed as a
/// double literal while Avalonia's DrawingContext API accepts a float radius.
/// </summary>
internal static class DrawingContextCompatExtensions
{
    public static void FillRectangle(this DrawingContext drawingContext, IBrush brush, Rect rect, double radius)
        => drawingContext.FillRectangle(brush, rect, (float)radius);
}
