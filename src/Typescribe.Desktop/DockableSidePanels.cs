using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Typescribe.Desktop;

/// <summary>
/// Adds Visual Studio-like tool-window headers and auto-hide rails to the left and right
/// workspace panes. Pinned panes stay open; auto-hidden panes fold to the edge and reopen
/// from a narrow rail until the user clicks elsewhere or pins them again.
/// </summary>
internal sealed class DockableSidePanels
{
    private static readonly SolidColorBrush ChromeBrush = Brush("#181818");
    private static readonly SolidColorBrush SurfaceBrush = Brush("#252526");
    private static readonly SolidColorBrush HoverBrush = Brush("#2A2D2E");
    private static readonly SolidColorBrush BorderBrush = Brush("#3F3F46");
    private static readonly SolidColorBrush TextBrush = Brush("#CCCCCC");
    private static readonly SolidColorBrush MutedTextBrush = Brush("#969696");
    private static readonly SolidColorBrush AccentBrush = Brush("#007ACC");

    private readonly StudioWorkspaceWindow _window;
    private readonly Grid _workspace;
    private PaneState? _left;
    private PaneState? _right;
    private bool _disposed;

    private DockableSidePanels(StudioWorkspaceWindow window, Grid workspace)
    {
        _window = window;
        _workspace = workspace;
    }

    public static void Apply(StudioWorkspaceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.Content is not Grid root) return;

        var workspace = root.Children
            .OfType<Grid>()
            .FirstOrDefault(grid => Grid.GetRow(grid) == 2);
        if (workspace is null || workspace.ColumnDefinitions.Count < 5) return;

        var docking = new DockableSidePanels(window, workspace);
        docking.Install();
    }

    private void Install()
    {
        _left = CreatePane(columnIndex: 0, splitterColumnIndex: 1, title: "Project", railText: "P", foldsLeft: true, defaultWidth: 250);
        _right = CreatePane(columnIndex: 4, splitterColumnIndex: 3, title: "Inspector", railText: "I", foldsLeft: false, defaultWidth: 360);

        if (_left is null && _right is null) return;

        _window.AddHandler(
            InputElement.PointerPressedEvent,
            WindowPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        _window.Closed += WindowClosed;
    }

    private PaneState? CreatePane(
        int columnIndex,
        int splitterColumnIndex,
        string title,
        string railText,
        bool foldsLeft,
        double defaultWidth)
    {
        var pane = _workspace.Children
            .OfType<Control>()
            .FirstOrDefault(control => Grid.GetColumn(control) == columnIndex && control is not GridSplitter);
        if (pane is null) return null;

        var column = _workspace.ColumnDefinitions[columnIndex];
        var splitterColumn = _workspace.ColumnDefinitions[splitterColumnIndex];
        var splitter = _workspace.Children
            .OfType<GridSplitter>()
            .FirstOrDefault(control => Grid.GetColumn(control) == splitterColumnIndex);

        var expandedWidth = column.Width.Value >= 80
            ? column.Width
            : new GridLength(defaultWidth);
        var expandedMinWidth = column.MinWidth >= 80
            ? column.MinWidth
            : Math.Min(defaultWidth, 180);
        var splitterWidth = splitterColumn.Width.Value > 0
            ? splitterColumn.Width
            : new GridLength(4);

        var pinButton = ToolButton("AUTO", 38, "Auto-hide this panel");
        var foldButton = ToolButton(foldsLeft ? "‹" : "›", 24, "Fold panel to the side");

        if (pane is Border border && border.Child is Control panelContent)
        {
            border.Child = null;
            border.Padding = new Thickness(0);
            border.CornerRadius = new CornerRadius(0);
            border.BorderBrush = BorderBrush;
            border.BorderThickness = foldsLeft
                ? new Thickness(0, 0, 1, 0)
                : new Thickness(1, 0, 0, 0);

            var header = new Grid
            {
                Height = 26,
                MinHeight = 26,
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                Background = SurfaceBrush,
                Margin = new Thickness(0),
                Children =
                {
                    new TextBlock
                    {
                        Text = title.ToUpperInvariant(),
                        FontSize = 10,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = MutedTextBrush,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 4, 0)
                    }
                }
            };

            Grid.SetColumn(pinButton, 1);
            header.Children.Add(pinButton);
            Grid.SetColumn(foldButton, 2);
            header.Children.Add(foldButton);

            var host = new Grid
            {
                RowDefinitions = new RowDefinitions("26,*"),
                Background = SurfaceBrush
            };
            host.Children.Add(header);
            Grid.SetRow(panelContent, 1);
            host.Children.Add(panelContent);
            border.Child = host;
        }

        var rail = new Button
        {
            Content = new TextBlock
            {
                Text = railText,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = MutedTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            MinWidth = 28,
            MinHeight = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Top,
            Background = ChromeBrush,
            BorderBrush = BorderBrush,
            BorderThickness = foldsLeft
                ? new Thickness(0, 0, 1, 0)
                : new Thickness(1, 0, 0, 0),
            CornerRadius = new CornerRadius(0),
            IsVisible = false
        };
        ToolTip.SetTip(rail, $"Show {title} (auto-hidden)");
        Grid.SetColumn(rail, columnIndex);
        _workspace.Children.Add(rail);

        var state = new PaneState(
            pane,
            rail,
            pinButton,
            foldButton,
            splitter,
            column,
            splitterColumn,
            expandedWidth,
            expandedMinWidth,
            splitterWidth,
            title,
            foldsLeft);

        pinButton.Click += (_, _) => TogglePinned(state);
        foldButton.Click += (_, _) =>
        {
            state.IsPinned = false;
            UpdatePinButton(state);
            Collapse(state);
        };
        rail.Click += (_, _) =>
        {
            state.IsPinned = false;
            UpdatePinButton(state);
            Expand(state);
        };

        rail.PointerEntered += (_, _) => rail.Background = HoverBrush;
        rail.PointerExited += (_, _) => rail.Background = ChromeBrush;
        UpdatePinButton(state);
        return state;
    }

    private static Button ToolButton(string text, double width, string tip)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = text.Length > 1 ? 8.5 : 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = MutedTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var button = new Button
        {
            Content = label,
            Width = width,
            MinWidth = width,
            Height = 24,
            MinHeight = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 1, 1, 1),
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

    private static void TogglePinned(PaneState state)
    {
        if (state.IsPinned)
        {
            state.IsPinned = false;
            UpdatePinButton(state);
            Collapse(state);
            return;
        }

        state.IsPinned = true;
        UpdatePinButton(state);
        Expand(state);
    }

    private static void UpdatePinButton(PaneState state)
    {
        if (state.PinButton.Content is TextBlock label)
        {
            label.Text = state.IsPinned ? "AUTO" : "PIN";
            label.Foreground = state.IsPinned ? MutedTextBrush : AccentBrush;
        }

        ToolTip.SetTip(
            state.PinButton,
            state.IsPinned ? $"Auto-hide {state.Title}" : $"Pin {state.Title} open");
    }

    private static void Collapse(PaneState state)
    {
        if (!state.IsExpanded) return;

        if (state.Column.Width.Value >= 80)
            state.ExpandedWidth = state.Column.Width;

        state.Pane.IsVisible = false;
        state.Rail.IsVisible = true;
        state.Column.MinWidth = 28;
        state.Column.Width = new GridLength(28);
        state.SplitterColumn.Width = new GridLength(0);
        if (state.Splitter is not null)
            state.Splitter.IsVisible = false;
        state.IsExpanded = false;
    }

    private static void Expand(PaneState state)
    {
        if (state.IsExpanded) return;

        state.Column.MinWidth = state.ExpandedMinWidth;
        state.Column.Width = state.ExpandedWidth;
        state.SplitterColumn.Width = state.ExpandedSplitterWidth;
        if (state.Splitter is not null)
            state.Splitter.IsVisible = true;
        state.Rail.IsVisible = false;
        state.Pane.IsVisible = true;
        state.IsExpanded = true;
    }

    private void WindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_disposed) return;

        AutoHideIfOutside(_left, e.Source);
        AutoHideIfOutside(_right, e.Source);
    }

    private static void AutoHideIfOutside(PaneState? state, object? source)
    {
        if (state is null || state.IsPinned || !state.IsExpanded) return;
        if (IsInside(source, state.Pane) || IsInside(source, state.Rail)) return;
        Collapse(state);
    }

    private static bool IsInside(object? source, Control target)
    {
        for (var current = source as Control; current is not null; current = current.Parent as Control)
        {
            if (ReferenceEquals(current, target)) return true;
        }
        return false;
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.RemoveHandler(InputElement.PointerPressedEvent, WindowPointerPressed);
        _window.Closed -= WindowClosed;
    }

    private sealed class PaneState(
        Control pane,
        Button rail,
        Button pinButton,
        Button foldButton,
        GridSplitter? splitter,
        ColumnDefinition column,
        ColumnDefinition splitterColumn,
        GridLength expandedWidth,
        double expandedMinWidth,
        GridLength expandedSplitterWidth,
        string title,
        bool foldsLeft)
    {
        public Control Pane { get; } = pane;
        public Button Rail { get; } = rail;
        public Button PinButton { get; } = pinButton;
        public Button FoldButton { get; } = foldButton;
        public GridSplitter? Splitter { get; } = splitter;
        public ColumnDefinition Column { get; } = column;
        public ColumnDefinition SplitterColumn { get; } = splitterColumn;
        public GridLength ExpandedWidth { get; set; } = expandedWidth;
        public double ExpandedMinWidth { get; } = expandedMinWidth;
        public GridLength ExpandedSplitterWidth { get; } = expandedSplitterWidth;
        public string Title { get; } = title;
        public bool FoldsLeft { get; } = foldsLeft;
        public bool IsPinned { get; set; } = true;
        public bool IsExpanded { get; set; } = true;
    }

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}
