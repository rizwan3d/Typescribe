using Avalonia.Controls;

namespace Typescribe.Desktop;

/// <summary>
/// Desktop-local tooltip facade. A namespace-local name keeps callers unambiguous when
/// editor helpers are imported and also accepts late-bound nullable controls safely.
/// </summary>
internal static class ToolTip
{
    public static void SetTip(Control? control, object? value)
    {
        if (control is not null)
            Avalonia.Controls.ToolTip.SetTip(control, value);
    }

    public static object? GetTip(Control control)
        => Avalonia.Controls.ToolTip.GetTip(control);
}
