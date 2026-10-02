using System;
using Avalonia.Controls.Primitives;

namespace Typescribe.Desktop;

/// <summary>
/// Typescribe's code-created text box surface. Avalonia 12 exposes text-box scrolling
/// through ScrollViewer attached properties; these convenience properties keep the UI
/// construction code readable while forwarding directly to the native Avalonia settings.
/// </summary>
internal class TextBox : Avalonia.Controls.TextBox
{
    // This control is only a convenience subclass of Avalonia's TextBox. Without the base
    // style key, standalone windows (for example Edit Corkboard Card) may not receive the
    // Fluent TextBox template, leaving an editable control in layout that draws nothing.
    // Reuse the native TextBox style/template everywhere this convenience type is used.
    protected override Type StyleKeyOverride => typeof(Avalonia.Controls.TextBox);

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
