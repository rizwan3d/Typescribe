using Avalonia.Controls.Primitives;

namespace Typescribe.Desktop;

/// <summary>
/// Typescribe's code-created text box surface. Avalonia 12 exposes text-box scrolling
/// through ScrollViewer attached properties; these convenience properties keep the UI
/// construction code readable while forwarding directly to the native Avalonia settings.
/// </summary>
internal class TextBox : Avalonia.Controls.TextBox
{
    public ScrollBarVisibility HorizontalScrollBarVisibility
    {
        get => GetValue(Avalonia.Controls.ScrollViewer.HorizontalScrollBarVisibilityProperty);
        set => SetValue(Avalonia.Controls.ScrollViewer.HorizontalScrollBarVisibilityProperty, value);
    }

    public ScrollBarVisibility VerticalScrollBarVisibility
    {
        get => GetValue(Avalonia.Controls.ScrollViewer.VerticalScrollBarVisibilityProperty);
        set => SetValue(Avalonia.Controls.ScrollViewer.VerticalScrollBarVisibilityProperty, value);
    }
}
