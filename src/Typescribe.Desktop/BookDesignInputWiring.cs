using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Typescribe.Desktop.ViewModels;
using NativeTextBlock = Avalonia.Controls.TextBlock;
using NativeTextBox = Avalonia.Controls.TextBox;

namespace Typescribe.Desktop;

/// <summary>
/// Keeps font presets alongside editable values, while color fields use a real spectrum-based
/// color picker plus an editable #RRGGBB value. Presets must never be the only way to reach a
/// BookStyle property.
/// </summary>
internal static class BookDesignInputWiring
{
    private static bool _colorPickerStylesInstalled;

    private static readonly FieldSpec[] Fields =
    [
        new("Typography", "Body font", nameof(BookDesignEditorViewModel.BodyFontFamily), "Installed font name", 180),
        new("Typography", "Heading font", nameof(BookDesignEditorViewModel.HeadingFontFamily), "Installed font name", 180),
        new("Typography", "Monospace font", nameof(BookDesignEditorViewModel.MonospaceFontFamily), "Installed font name", 180),
        new("Typography", "Math font", nameof(BookDesignEditorViewModel.MathFontFamily), "Installed math font", 180),
        new("Typography", "Body color", nameof(BookDesignEditorViewModel.BodyColorHex), "#RRGGBB", 52, true),
        new("Typography", "Heading color", nameof(BookDesignEditorViewModel.HeadingColorHex), "#RRGGBB", 52, true),
        new("Typography", "Link color", nameof(BookDesignEditorViewModel.LinkColorHex), "#RRGGBB", 52, true),
        new("Code", "Background", nameof(BookDesignEditorViewModel.CodeBackgroundHex), "#RRGGBB", 52, true),
        new("Code", "Text", nameof(BookDesignEditorViewModel.CodeTextHex), "#RRGGBB", 52, true),
        new("Code", "Keywords", nameof(BookDesignEditorViewModel.CodeKeywordHex), "#RRGGBB", 52, true),
        new("Code", "Strings", nameof(BookDesignEditorViewModel.CodeStringHex), "#RRGGBB", 52, true),
        new("Code", "Comments", nameof(BookDesignEditorViewModel.CodeCommentHex), "#RRGGBB", 52, true),
        new("Code", "Frame", nameof(BookDesignEditorViewModel.CodeFrameHex), "#RRGGBB", 52, true)
    ];

    public static void Apply(BookDesignDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        EnsureColorPickerStyles();
        dialog.Opened += (_, _) => Install(dialog);
    }

    private static void Install(BookDesignDialog dialog)
    {
        const string installedClass = "complete-book-design-inputs";
        if (dialog.Classes.Contains(installedClass)) return;
        dialog.Classes.Add(installedClass);
        EnsureColorPickerStyles();

        if (dialog.Content is not Control root) return;
        var tabs = EnumerateControls(root).OfType<TabControl>().FirstOrDefault();
        if (tabs is null) return;

        var wired = new List<WiredInput>();
        foreach (var spec in Fields)
        {
            if (!TryFindPresetCombo(tabs, spec.Tab, spec.Label, out var grid, out var combo)) continue;

            var row = Grid.GetRow(combo);
            var column = Grid.GetColumn(combo);
            var rowSpan = Grid.GetRowSpan(combo);
            var columnSpan = Grid.GetColumnSpan(combo);
            var current = GetEditorString(dialog, spec.Property);

            var editor = new NativeTextBox
            {
                Text = current,
                Watermark = spec.Watermark,
                MinWidth = 100,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            Control selector;
            ColorPicker? colorPicker = null;
            if (spec.IsColor)
            {
                colorPicker = CreateColorPicker(current, spec.SelectorWidth);
                selector = colorPicker;
            }
            else
            {
                combo.Width = spec.SelectorWidth;
                combo.MinWidth = 0;
                combo.HorizontalAlignment = HorizontalAlignment.Right;
                selector = combo;
            }

            // Detach the old selector before re-parenting/replacing it: Avalonia controls can only
            // belong to one visual parent. Color rows intentionally discard the preset ComboBox.
            grid.Children.Remove(combo);

            var host = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 6,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            host.Children.Add(editor);
            Grid.SetColumn(selector, 1);
            host.Children.Add(selector);

            Grid.SetRow(host, row);
            Grid.SetColumn(host, column);
            Grid.SetRowSpan(host, rowSpan);
            Grid.SetColumnSpan(host, columnSpan);
            grid.Children.Add(host);

            var state = new WiredInput(spec, editor, colorPicker);
            wired.Add(state);

            editor.TextChanged += (_, _) =>
            {
                if (state.Syncing) return;

                var value = editor.Text ?? string.Empty;
                SetEditorString(dialog, spec.Property, value);

                if (state.ColorPicker is { } picker && TryParseColor(value, out var parsed))
                {
                    state.Syncing = true;
                    try
                    {
                        picker.Color = parsed;
                    }
                    finally
                    {
                        state.Syncing = false;
                    }
                }

                dialog.RefreshDesignPreviewFromInputWiring();
            };

            if (colorPicker is not null)
            {
                colorPicker.ColorChanged += (_, _) =>
                {
                    if (state.Syncing) return;

                    var value = ToRgbHex(colorPicker.Color);
                    state.Syncing = true;
                    try
                    {
                        editor.Text = value;
                        SetEditorString(dialog, spec.Property, value);
                    }
                    finally
                    {
                        state.Syncing = false;
                    }

                    dialog.RefreshDesignPreviewFromInputWiring();
                };
            }
            else
            {
                combo.SelectionChanged += (_, _) => Dispatcher.UIThread.Post(
                    () => SyncFromModel(dialog, state),
                    DispatcherPriority.Background);
            }
        }

        dialog.PropertyChanged += (_, e) =>
        {
            if (!string.Equals(e.Property.Name, "DataContext", StringComparison.Ordinal)) return;
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var state in wired) SyncFromModel(dialog, state);
            }, DispatcherPriority.Background);
        };
    }

    private static ColorPicker CreateColorPicker(string value, double width)
    {
        var picker = new ColorPicker
        {
            Width = width,
            MinWidth = width,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsAlphaEnabled = false,
            IsAlphaVisible = false,
            IsAccentColorsVisible = false,
            IsColorPaletteVisible = false,
            IsColorSpectrumVisible = true,
            IsColorComponentsVisible = true,
            IsHexInputVisible = true
        };

        if (TryParseColor(value, out var color)) picker.Color = color;
        return picker;
    }

    private static void EnsureColorPickerStyles()
    {
        if (_colorPickerStylesInstalled || global::Avalonia.Application.Current is not { } app) return;

        // Instantiate our compiled XAML wrapper rather than dynamically loading the external
        // ColorPicker XAML at runtime. This keeps the theme safe for trimming and Native AOT.
        app.Styles.Add(new Styles.ColorPickerTheme());
        _colorPickerStylesInstalled = true;
    }

    private static void SyncFromModel(BookDesignDialog dialog, WiredInput state)
    {
        var value = GetEditorString(dialog, state.Spec.Property);

        state.Syncing = true;
        try
        {
            if (!string.Equals(state.Editor.Text, value, StringComparison.Ordinal))
                state.Editor.Text = value;

            if (state.ColorPicker is { } picker && TryParseColor(value, out var parsed))
                picker.Color = parsed;
        }
        finally
        {
            state.Syncing = false;
        }
    }

    private static bool TryParseColor(string? value, out Color color)
    {
        try
        {
            color = Color.Parse(value?.Trim() ?? string.Empty);
            return true;
        }
        catch
        {
            color = Colors.Black;
            return false;
        }
    }

    private static string ToRgbHex(Color color)
        => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string GetEditorString(BookDesignDialog dialog, string propertyName)
    {
        if (dialog.DataContext is not BookDesignEditorViewModel editor) return string.Empty;
        return typeof(BookDesignEditorViewModel)
            .GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?
            .GetValue(editor) as string ?? string.Empty;
    }

    private static void SetEditorString(BookDesignDialog dialog, string propertyName, string value)
    {
        if (dialog.DataContext is not BookDesignEditorViewModel editor) return;
        typeof(BookDesignEditorViewModel)
            .GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?
            .SetValue(editor, value);
    }

    private static bool TryFindPresetCombo(
        TabControl tabs,
        string tabHeader,
        string label,
        out Grid grid,
        out ComboBox combo)
    {
        var tab = TabItems(tabs).FirstOrDefault(item =>
            string.Equals(item.Header?.ToString(), tabHeader, StringComparison.Ordinal));
        if (tab?.Content is not Control content)
        {
            grid = null!;
            combo = null!;
            return false;
        }

        foreach (var candidate in EnumerateControls(content).OfType<Grid>())
        {
            var labelBlock = candidate.Children.OfType<NativeTextBlock>()
                .FirstOrDefault(block => string.Equals(block.Text, label, StringComparison.Ordinal));
            if (labelBlock is null) continue;

            var row = Grid.GetRow(labelBlock);
            var selector = candidate.Children.OfType<ComboBox>()
                .FirstOrDefault(box => Grid.GetRow(box) == row && Grid.GetColumn(box) == 1);
            if (selector is null) continue;

            grid = candidate;
            combo = selector;
            return true;
        }

        grid = null!;
        combo = null!;
        return false;
    }

    private static IEnumerable<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is IEnumerable source)
            return source.Cast<object?>().OfType<TabItem>();
        return tabs.Items.OfType<TabItem>();
    }

    private static IEnumerable<Control> EnumerateControls(Control root)
    {
        yield return root;

        if (root is Panel panel)
        {
            foreach (var child in panel.Children.OfType<Control>())
            {
                foreach (var descendant in EnumerateControls(child))
                    yield return descendant;
            }
        }

        if (root is Decorator decorator && decorator.Child is Control decoratedChild)
        {
            foreach (var descendant in EnumerateControls(decoratedChild))
                yield return descendant;
        }

        if (root is ContentControl contentControl && contentControl.Content is Control content)
        {
            foreach (var descendant in EnumerateControls(content))
                yield return descendant;
        }
    }

    private sealed record FieldSpec(
        string Tab,
        string Label,
        string Property,
        string Watermark,
        double SelectorWidth,
        bool IsColor = false);

    private sealed class WiredInput(FieldSpec spec, NativeTextBox editor, ColorPicker? colorPicker)
    {
        public FieldSpec Spec { get; } = spec;
        public NativeTextBox Editor { get; } = editor;
        public ColorPicker? ColorPicker { get; } = colorPicker;
        public bool Syncing { get; set; }
    }
}
