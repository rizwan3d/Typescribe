using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Reframes the long-form editor toolbar as a compact, grouped command bar without
/// changing any of the editing commands owned by LongFormEditorFeature.
/// </summary>
internal sealed class EditorToolbarPolishFeature
{
    private readonly StudioWorkspaceWindow _window;
    private bool _applyQueued;
    private bool _applied;
    private bool _disposed;

    private EditorToolbarPolishFeature(StudioWorkspaceWindow window)
        => _window = window;

    public static void Apply(StudioWorkspaceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var feature = new EditorToolbarPolishFeature(window);
        window.Opened += feature.WindowOpened;
        window.LayoutUpdated += feature.WindowLayoutUpdated;
        window.Closed += feature.WindowClosed;
        feature.QueueApply();
    }

    private void WindowOpened(object? sender, EventArgs e) => QueueApply();

    private void WindowLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_applied) QueueApply();
    }

    private void QueueApply()
    {
        if (_disposed || _applied || _applyQueued) return;
        _applyQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _applyQueued = false;
            if (!_disposed && !_applied) TryApply();
        }, DispatcherPriority.Background);
    }

    private void TryApply()
    {
        var host = _window.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(static grid => grid.Classes.Contains("long-form-editor-host"));
        if (host is null) return;

        var toolbar = host.Children
            .OfType<Border>()
            .FirstOrDefault(static border =>
                Grid.GetRow(border) == 0 &&
                border.Child is ScrollViewer { Content: Panel });
        if (toolbar?.Child is not ScrollViewer { Content: Panel legacyPanel }) return;

        // LongFormEditorFeature currently creates 21 controls in a stable command order.
        // Require the complete set so a future toolbar change fails safely instead of
        // accidentally moving unrelated controls.
        var controls = legacyPanel.Children.OfType<Control>().ToArray();
        if (controls.Length < 21) return;

        legacyPanel.Children.Clear();

        SetText(controls[5], "❝");
        SetText(controls[8], "Find");
        SetText(controls[11], "Center");
        SetText(controls[14], "Lines");

        var format = CommandGroup("Format", controls[0], controls[1], controls[2]);
        var structure = CommandGroup("Structure", controls[3], controls[4], controls[5], controls[6]);
        var find = CommandGroup("Find", controls[8]);
        var view = CommandGroup("View", controls[10], controls[11], controls[12], controls[13], controls[14], controls[15]);
        var zoom = CommandGroup("Zoom", controls[17], controls[18], controls[19], controls[20]);
        zoom.Margin = new Thickness(6, 5, 8, 5);

        var commands = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 0,
            Margin = new Thickness(8, 5, 0, 5)
        };
        commands.Children.Add(format);
        commands.Children.Add(structure);
        commands.Children.Add(find);
        commands.Children.Add(view);

        var commandScroll = new ScrollViewer
        {
            Content = commands,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var root = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            MinHeight = 58,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };
        root.Children.Add(commandScroll);
        Grid.SetColumn(zoom, 1);
        root.Children.Add(zoom);

        toolbar.Child = root;
        toolbar.BorderThickness = new Thickness(0, 0, 0, 1);
        toolbar.BorderBrush = NeutralBrush(42);
        toolbar.Background = NeutralBrush(12);
        if (!toolbar.Classes.Contains("editor-command-bar"))
            toolbar.Classes.Add("editor-command-bar");

        _applied = true;
    }

    private static Border CommandGroup(string label, params Control[] controls)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2
        };

        foreach (var control in controls)
        {
            NormalizeControl(control);
            row.Children.Add(control);
        }

        var caption = new TextBlock
        {
            Text = label.ToUpperInvariant(),
            FontSize = 9.5,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.56,
            Margin = new Thickness(2, 0, 2, 1)
        };

        var content = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(6, 4)
        };
        content.Children.Add(caption);
        content.Children.Add(row);

        return new Border
        {
            Child = content,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = NeutralBrush(35),
            Background = NeutralBrush(10),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static void NormalizeControl(Control control)
    {
        control.Margin = new Thickness(0);
        control.VerticalAlignment = VerticalAlignment.Center;

        if (control is ToggleButton toggle)
        {
            toggle.MinWidth = 42;
            toggle.Height = 30;
            toggle.MinHeight = 30;
            toggle.Padding = new Thickness(8, 2);
            toggle.FontSize = 11.5;
            return;
        }

        if (control is Button button)
        {
            var text = ButtonText(button);
            button.MinWidth = text.Length > 2 ? 44 : 32;
            button.Height = 30;
            button.MinHeight = 30;
            button.Padding = new Thickness(8, 2);
            button.FontSize = 12;
            return;
        }

        if (control is TextBlock textBlock)
        {
            textBlock.MinWidth = 44;
            textBlock.TextAlignment = TextAlignment.Center;
            textBlock.VerticalAlignment = VerticalAlignment.Center;
            textBlock.FontSize = 11.5;
        }
    }

    private static void SetText(Control control, string text)
    {
        switch (control)
        {
            case ToggleButton toggle:
                toggle.Content = text;
                break;
            case Button { Content: TextBlock label }:
                label.Text = text;
                break;
            case Button button:
                button.Content = text;
                break;
        }
    }

    private static string ButtonText(Button button)
        => button.Content switch
        {
            TextBlock text => text.Text ?? string.Empty,
            string text => text,
            _ => string.Empty
        };

    private static IBrush NeutralBrush(byte alpha)
        => new SolidColorBrush(Color.FromArgb(alpha, 128, 128, 128));

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= WindowOpened;
        _window.LayoutUpdated -= WindowLayoutUpdated;
        _window.Closed -= WindowClosed;
    }
}
