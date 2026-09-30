using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop;

/// <summary>
/// Keeps the Book Design preset selectors while restoring a real editable value field.
/// This matters for arbitrary installed font names and arbitrary #RRGGBB colors: a preset
/// must never be the only way to reach a BookStyle property.
/// </summary>
internal static class BookDesignInputWiring
{
    private static readonly FieldSpec[] Fields =
    [
        new("Typography", "Body font", nameof(BookDesignEditorViewModel.BodyFontFamily), "Installed font name", 180),
        new("Typography", "Heading font", nameof(BookDesignEditorViewModel.HeadingFontFamily), "Installed font name", 180),
        new("Typography", "Monospace font", nameof(BookDesignEditorViewModel.MonospaceFontFamily), "Installed font name", 180),
        new("Typography", "Math font", nameof(BookDesignEditorViewModel.MathFontFamily), "Installed math font", 180),
        new("Typography", "Body color", nameof(BookDesignEditorViewModel.BodyColorHex), "#RRGGBB", 145),
        new("Typography", "Heading color", nameof(BookDesignEditorViewModel.HeadingColorHex), "#RRGGBB", 145),
        new("Typography", "Link color", nameof(BookDesignEditorViewModel.LinkColorHex), "#RRGGBB", 145),
        new("Code", "Background", nameof(BookDesignEditorViewModel.CodeBackgroundHex), "#RRGGBB", 145),
        new("Code", "Text", nameof(BookDesignEditorViewModel.CodeTextHex), "#RRGGBB", 145),
        new("Code", "Keywords", nameof(BookDesignEditorViewModel.CodeKeywordHex), "#RRGGBB", 145),
        new("Code", "Strings", nameof(BookDesignEditorViewModel.CodeStringHex), "#RRGGBB", 145),
        new("Code", "Comments", nameof(BookDesignEditorViewModel.CodeCommentHex), "#RRGGBB", 145),
        new("Code", "Frame", nameof(BookDesignEditorViewModel.CodeFrameHex), "#RRGGBB", 145)
    ];

    public static void Apply(BookDesignDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        dialog.Opened += (_, _) => Install(dialog);
    }

    private static void Install(BookDesignDialog dialog)
    {
        const string installedClass = "complete-book-design-inputs";
        if (dialog.Classes.Contains(installedClass)) return;
        dialog.Classes.Add(installedClass);

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

            var editor = new TextBox
            {
                Text = GetEditorString(dialog, spec.Property),
                Watermark = spec.Watermark,
                MinWidth = 100,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            combo.Width = spec.PresetWidth;
            combo.MinWidth = 0;
            combo.HorizontalAlignment = HorizontalAlignment.Right;

            // Detach before re-parenting: Avalonia controls can only belong to one visual parent.
            grid.Children.Remove(combo);

            var host = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 6,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            host.Children.Add(editor);
            Grid.SetColumn(combo, 1);
            host.Children.Add(combo);

            Grid.SetRow(host, row);
            Grid.SetColumn(host, column);
            Grid.SetRowSpan(host, rowSpan);
            Grid.SetColumnSpan(host, columnSpan);
            grid.Children.Add(host);

            var state = new WiredInput(spec, editor);
            wired.Add(state);

            editor.TextChanged += (_, _) =>
            {
                if (state.Syncing) return;
                SetEditorString(dialog, spec.Property, editor.Text ?? string.Empty);
                dialog.RefreshDesignPreviewFromInputWiring();
            };

            combo.SelectionChanged += (_, _) => Dispatcher.UIThread.Post(
                () => SyncFromModel(dialog, state),
                DispatcherPriority.Background);
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

    private static void SyncFromModel(BookDesignDialog dialog, WiredInput state)
    {
        var value = GetEditorString(dialog, state.Spec.Property);
        if (string.Equals(state.Editor.Text, value, StringComparison.Ordinal)) return;

        state.Syncing = true;
        try
        {
            state.Editor.Text = value;
        }
        finally
        {
            state.Syncing = false;
        }
    }

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
            var labelBlock = candidate.Children.OfType<TextBlock>()
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
        double PresetWidth);

    private sealed class WiredInput(FieldSpec spec, TextBox editor)
    {
        public FieldSpec Spec { get; } = spec;
        public TextBox Editor { get; } = editor;
        public bool Syncing { get; set; }
    }
}
