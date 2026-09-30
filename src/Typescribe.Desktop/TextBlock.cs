namespace Typescribe.Desktop;

/// <summary>
/// Code-created text surface helper. Avalonia exposes tooltips through the ToolTip
/// attached property; this convenience property keeps object initializers readable
/// while forwarding to the native attached property.
/// </summary>
internal class TextBlock : Avalonia.Controls.TextBlock
{
    public object? ToolTip
    {
        get => Avalonia.Controls.ToolTip.GetTip(this);
        set => Avalonia.Controls.ToolTip.SetTip(this, value);
    }
}
