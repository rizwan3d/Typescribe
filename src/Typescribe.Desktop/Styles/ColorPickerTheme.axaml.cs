using Avalonia.Markup.Xaml;

namespace Typescribe.Desktop.Styles;

public sealed partial class ColorPickerTheme : global::Avalonia.Styling.Styles
{
    public ColorPickerTheme()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
