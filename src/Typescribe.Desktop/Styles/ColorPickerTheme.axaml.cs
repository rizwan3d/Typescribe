using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Typescribe.Desktop.Styles;

public sealed partial class ColorPickerTheme : Styles
{
    public ColorPickerTheme()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
