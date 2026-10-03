using Avalonia.Controls;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Null-safe facade for Avalonia tooltips used by editor features. Keeping the nullable
/// check here avoids sprinkling null-forgiving operators through late-bound UI code.
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
