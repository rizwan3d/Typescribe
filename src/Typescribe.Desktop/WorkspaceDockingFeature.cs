using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Typescribe.Desktop;

/// <summary>
/// Gives every tabbed workspace surface a Visual Studio-like float/dock command. The real
/// content stays alive while it is moved into a separate owned window, then returns to the
/// exact tab it came from when the floating window is closed or Dock is pressed.
/// </summary>
internal sealed class WorkspaceDockingFeature
{
    private static readonly SolidColorBrush ChromeBrush = Brush("#181818");
    private static readonly SolidColorBrush SurfaceBrush = Brush("#252526");
    private static readonly SolidColorBrush HoverBrush = Brush("#2A2D2E");
    private static readonly SolidColorBrush BorderBrush = Brush("#3F3F46");
    private static readonly SolidColorBrush TextBrush = Brush("#CCCCCC");
    private static readonly SolidColorBrush MutedTextBrush = Brush("#969696");
    private static readonly SolidColorBrush AccentBrush = Brush("#007ACC");

    private readonly StudioWorkspaceWindow _window;
    private readonly Dictionary<TabItem, DockTabState> _states = [];
    private bool _scanQueued;
    private bool _disposed;

    private WorkspaceDockingFeature(StudioWorkspaceWindow window)
    {
        _window = window;
    }

    public static void Apply(StudioWorkspaceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var feature = new WorkspaceDockingFeature(window);
        window.Opened += feature.WindowOpened;
        window.LayoutUpdated += feature.WindowLayoutUpdated;
        window.Closed += feature.WindowClosed;
        feature.QueueScan();
    }

    private void WindowOpened(object? sender, EventArgs e) => QueueScan();

    private void WindowLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_disposed) QueueScan();
    }

    private void QueueScan()
    {
        if (_disposed || _scanQueued) return;
        _scanQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scanQueued = false;
            if (!_disposed) InstallAvailableTabs();
        }, DispatcherPriority.Background);
    }

    private void InstallAvailableTabs()
    {
        foreach (var tabs in _window.GetVisualDescendants().OfType<TabControl>().ToArray())
        {
            foreach (var tab in TabItems(tabs).ToArray())
                InstallTab(tabs, tab);
        }
    }

    private void InstallTab(TabControl tabs, TabItem tab)
    {
        if (_states.TryGetValue(tab, out var known))
        {
            if (known.FloatingWindow is not null || ReferenceEquals(tab.Content, known.Host))
                return;

            // A later feature replaced this tab's content. Forget the stale wrapper and wrap
            // the new surface instead of restoring an obsolete visual tree.
            _states.Remove(tab);
        }

        if (tab.Content is not Control originalContent) return;
        if (originalContent.Classes.Contains("workspace-dock-host") ||
            originalContent.Classes.Contains("workspace-dock-placeholder"))
            return;

        var title = HeaderText(tab);
        if (string.IsNullOrWhiteSpace(title)) return;

        tab.Content = null;

        var floatButton = DockButton("FLOAT", $"Move {title} to a separate window");
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = MutedTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 4, 0)
        };
        var grip = new TextBlock
        {
            Text = "⋮⋮",
            FontSize = 10,
            Foreground = MutedTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };

        var toolbarGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Height = 24,
            MinHeight = 24,
            Background = SurfaceBrush
        };
        toolbarGrid.Children.Add(grip);
        Grid.SetColumn(titleText, 1);
        toolbarGrid.Children.Add(titleText);
        Grid.SetColumn(floatButton, 2);
        toolbarGrid.Children.Add(floatButton);

        var toolbar = new Border
        {
            Height = 24,
            MinHeight = 24,
            Background = SurfaceBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = toolbarGrid
        };

        var host = new Grid
        {
            RowDefinitions = new RowDefinitions("24,*"),
            Background = SurfaceBrush
        };
        host.Classes.Add("workspace-dock-host");
        host.Children.Add(toolbar);
        Grid.SetRow(originalContent, 1);
        host.Children.Add(originalContent);

        var state = new DockTabState(tabs, tab, originalContent, host, floatButton, title);
        floatButton.Click += (_, _) => ToggleFloat(state);
        tab.Content = host;
        _states[tab] = state;
    }

    private void ToggleFloat(DockTabState state)
    {
        if (_disposed) return;
        if (state.FloatingWindow is null)
            Float(state);
        else
            state.FloatingWindow.Close();
    }

    private void Float(DockTabState state)
    {
        if (state.FloatingWindow is not null)
        {
            state.FloatingWindow.Activate();
            return;
        }
        if (!ReferenceEquals(state.Tab.Content, state.Host)) return;

        state.Tab.Content = null;
        var dockBack = new Button
        {
            Content = $"Dock {state.Title} back into Typescribe",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 210,
            MinHeight = 34,
            Padding = new Thickness(12, 5),
            Background = SurfaceBrush,
            Foreground = TextBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };
        var placeholder = new Grid { Background = ChromeBrush };
        placeholder.Classes.Add("workspace-dock-placeholder");
        placeholder.Children.Add(dockBack);
        state.Placeholder = placeholder;
        state.Tab.Content = placeholder;

        SetDockButton(state.FloatButton, "DOCK", $"Dock {state.Title} back into Typescribe", AccentBrush);

        var (width, height, minWidth, minHeight) = FloatingSize(state.Tabs);
        var floating = new Window
        {
            Title = $"{state.Title} — Typescribe",
            Width = width,
            Height = height,
            MinWidth = minWidth,
            MinHeight = minHeight,
            Background = SurfaceBrush,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = state.Host
        };
        state.FloatingWindow = floating;

        dockBack.Click += (_, _) =>
        {
            if (state.FloatingWindow is { } active)
                active.Close();
        };
        floating.Closed += (_, _) => RestoreDocked(state, floating);

        try
        {
            floating.Show(_window);
        }
        catch
        {
            // If a platform refuses an owned tool window, still allow a normal floating window.
            floating.Show();
        }
    }

    private void RestoreDocked(DockTabState state, Window floating)
    {
        if (!ReferenceEquals(state.FloatingWindow, floating)) return;

        floating.Content = null;
        state.FloatingWindow = null;
        state.Placeholder = null;
        SetDockButton(state.FloatButton, "FLOAT", $"Move {state.Title} to a separate window", MutedTextBrush);

        if (_disposed) return;
        state.Tab.Content = state.Host;
        state.Tabs.SelectedItem = state.Tab;
        QueueScan();
    }

    private static (double Width, double Height, double MinWidth, double MinHeight) FloatingSize(TabControl tabs)
    {
        var headers = TabItems(tabs).Select(HeaderText).ToArray();
        if (headers.Any(static header => string.Equals(header, "Editor", StringComparison.OrdinalIgnoreCase)) ||
            headers.Any(static header => string.Equals(header, "Corkboard", StringComparison.OrdinalIgnoreCase)) ||
            headers.Any(static header => string.Equals(header, "Outliner", StringComparison.OrdinalIgnoreCase)))
            return (1120, 760, 620, 420);

        if (headers.Any(static header => string.Equals(header, "Inspector", StringComparison.OrdinalIgnoreCase)) ||
            headers.Any(static header => string.Equals(header, "Comments", StringComparison.OrdinalIgnoreCase)) ||
            headers.Any(static header => string.Equals(header, "Snapshots", StringComparison.OrdinalIgnoreCase)))
            return (520, 740, 360, 360);

        return (460, 720, 320, 340);
    }

    private static Button DockButton(string text, string tip)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 8.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = MutedTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var button = new Button
        {
            Content = label,
            Width = 46,
            MinWidth = 46,
            Height = 22,
            MinHeight = 22,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 1, 2, 1),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(2),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, tip);
        button.PointerEntered += (_, _) => button.Background = HoverBrush;
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        return button;
    }

    private static void SetDockButton(Button button, string text, string tip, IBrush foreground)
    {
        if (button.Content is TextBlock label)
        {
            label.Text = text;
            label.Foreground = foreground;
        }
        ToolTip.SetTip(button, tip);
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= WindowOpened;
        _window.LayoutUpdated -= WindowLayoutUpdated;
        _window.Closed -= WindowClosed;

        foreach (var state in _states.Values.ToArray())
        {
            if (state.FloatingWindow is not { } floating) continue;
            floating.Content = null;
            state.FloatingWindow = null;
            floating.Close();
        }
        _states.Clear();
    }

    private static IEnumerable<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is IEnumerable source)
            return source.Cast<object?>().OfType<TabItem>();
        return tabs.Items.OfType<TabItem>();
    }

    private static string HeaderText(TabItem tab)
        => tab.Header switch
        {
            string text => text.Replace("_", string.Empty, StringComparison.Ordinal).Trim(),
            TextBlock block => block.Text?.Trim() ?? string.Empty,
            Avalonia.Controls.TextBlock block => block.Text?.Trim() ?? string.Empty,
            _ => tab.Header?.ToString()?.Trim() ?? string.Empty
        };

    private sealed class DockTabState(
        TabControl tabs,
        TabItem tab,
        Control originalContent,
        Grid host,
        Button floatButton,
        string title)
    {
        public TabControl Tabs { get; } = tabs;
        public TabItem Tab { get; } = tab;
        public Control OriginalContent { get; } = originalContent;
        public Grid Host { get; } = host;
        public Button FloatButton { get; } = floatButton;
        public string Title { get; } = title;
        public Control? Placeholder { get; set; }
        public Window? FloatingWindow { get; set; }
    }

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}
