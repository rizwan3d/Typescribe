using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Typescribe.Desktop;

/// <summary>
/// Rebuilds the studio's upper chrome to match the compact dark reference:
/// custom project title row, centered global search, compact menu row and IDE-like tab strips.
/// </summary>
internal sealed class ReferenceTopChrome
{
    private static readonly SolidColorBrush ChromeBrush = Brush("#0D151E");
    private static readonly SolidColorBrush MenuBrush = Brush("#101923");
    private static readonly SolidColorBrush SurfaceBrush = Brush("#172230");
    private static readonly SolidColorBrush SurfaceHoverBrush = Brush("#202D3C");
    private static readonly SolidColorBrush BorderBrush = Brush("#293748");
    private static readonly SolidColorBrush SearchBrush = Brush("#121D29");
    private static readonly SolidColorBrush TextBrush = Brush("#DCE5F0");
    private static readonly SolidColorBrush MutedTextBrush = Brush("#93A2B6");
    private static readonly SolidColorBrush AccentBrush = Brush("#8B5CF6");
    private static readonly SolidColorBrush AccentSoftBrush = Brush("#241E3C");
    private static readonly SolidColorBrush CloseHoverBrush = Brush("#C42B3D");

    private readonly StudioWorkspaceWindow _window;
    private readonly Grid _root;
    private readonly Menu _menu;
    private readonly Avalonia.Controls.TextBox _searchBox;
    private readonly TextBlock _titleText = new();
    private readonly HashSet<TabControl> _styledTabs = [];
    private bool _disposed;

    private ReferenceTopChrome(
        StudioWorkspaceWindow window,
        Grid root,
        Menu menu,
        Avalonia.Controls.TextBox searchBox)
    {
        _window = window;
        _root = root;
        _menu = menu;
        _searchBox = searchBox;
    }

    public static void Apply(StudioWorkspaceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.Content is not Grid root) return;

        var existing = root.Children
            .OfType<Grid>()
            .FirstOrDefault(grid => string.Equals(grid.Name, "TopMenuSearchBar", StringComparison.Ordinal));
        if (existing is null) return;

        var menu = existing.Children.OfType<Menu>().FirstOrDefault();
        var search = existing.Children
            .OfType<Avalonia.Controls.TextBox>()
            .FirstOrDefault(box => string.Equals(box.Name, "ProjectSearchBox", StringComparison.Ordinal));
        if (menu is null || search is null) return;

        existing.Children.Remove(menu);
        existing.Children.Remove(search);
        root.Children.Remove(existing);

        var chrome = new ReferenceTopChrome(window, root, menu, search);
        chrome.Install();
    }

    private void Install()
    {
        _window.WindowDecorations = WindowDecorations.BorderOnly;

        _root.RowDefinitions[0].Height = GridLength.Auto;
        if (_root.RowDefinitions.Count > 1)
            _root.RowDefinitions[1].Height = new GridLength(0);

        var shell = new Grid
        {
            Name = "ReferenceTopChrome",
            RowDefinitions = new RowDefinitions("42,30"),
            Background = ChromeBrush,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var titleRow = BuildTitleRow();
        shell.Children.Add(titleRow);

        var menuRow = BuildMenuRow();
        Grid.SetRow(menuRow, 1);
        shell.Children.Add(menuRow);

        Grid.SetRow(shell, 0);
        _root.Children.Add(shell);

        StyleWorkspaceTabs();

        _window.PropertyChanged += WindowPropertyChanged;
        _window.Opened += WindowOpened;
        _window.LayoutUpdated += WindowLayoutUpdated;
        _window.Closed += WindowClosed;

        Dispatcher.UIThread.Post(StyleWorkspaceTabs, DispatcherPriority.Background);
    }

    private Control BuildTitleRow()
    {
        _titleText.Text = DisplayTitle(_window.Title);
        _titleText.FontSize = 12;
        _titleText.Foreground = TextBrush;
        _titleText.VerticalAlignment = VerticalAlignment.Center;
        _titleText.Margin = new Thickness(9, 0, 0, 0);

        var mark = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1),
            BorderBrush = BorderBrush,
            Background = SurfaceBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "T",
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = TextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var titleHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
            Children = { mark, _titleText }
        };
        titleHost.PointerPressed += TitleHostPointerPressed;

        ConfigureSearchBox();

        var windowButtons = BuildWindowButtons();

        var titleGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Background = ChromeBrush
        };
        titleGrid.Children.Add(titleHost);

        var searchHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        searchHost.Children.Add(_searchBox);
        Grid.SetColumn(searchHost, 1);
        titleGrid.Children.Add(searchHost);

        Grid.SetColumn(windowButtons, 2);
        titleGrid.Children.Add(windowButtons);
        return titleGrid;
    }

    private void ConfigureSearchBox()
    {
        _searchBox.Width = 430;
        _searchBox.MinWidth = 280;
        _searchBox.MaxWidth = 430;
        _searchBox.Height = 34;
        _searchBox.MinHeight = 34;
        _searchBox.Margin = new Thickness(12, 4);
        _searchBox.Padding = new Thickness(12, 5);
        _searchBox.HorizontalAlignment = HorizontalAlignment.Center;
        _searchBox.VerticalAlignment = VerticalAlignment.Center;
        _searchBox.Background = SearchBrush;
        _searchBox.Foreground = TextBrush;
        _searchBox.BorderBrush = BorderBrush;
        _searchBox.BorderThickness = new Thickness(1);
        _searchBox.CornerRadius = new CornerRadius(8);
        _searchBox.FontSize = 12;
    }

    private StackPanel BuildWindowButtons()
    {
        var minimize = ChromeButton("—", "Minimize");
        var maximize = ChromeButton("□", "Maximize");
        var close = ChromeButton("×", "Close");

        minimize.Click += (_, _) => _window.WindowState = WindowState.Minimized;
        maximize.Click += (_, _) => ToggleMaximized();
        close.Click += (_, _) => _window.Close();

        close.PointerEntered += (_, _) => close.Background = CloseHoverBrush;
        close.PointerExited += (_, _) => close.Background = Brushes.Transparent;

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Children = { minimize, maximize, close }
        };
    }

    private Button ChromeButton(string glyph, string tip)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontSize = 13,
                Foreground = TextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Width = 36,
            Height = 30,
            MinWidth = 36,
            MinHeight = 30,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, tip);
        button.PointerEntered += (_, _) =>
        {
            if (!string.Equals(tip, "Close", StringComparison.Ordinal))
                button.Background = SurfaceHoverBrush;
        };
        button.PointerExited += (_, _) =>
        {
            if (!string.Equals(tip, "Close", StringComparison.Ordinal))
                button.Background = Brushes.Transparent;
        };
        return button;
    }

    private Control BuildMenuRow()
    {
        _menu.HorizontalAlignment = HorizontalAlignment.Left;
        _menu.VerticalAlignment = VerticalAlignment.Stretch;
        _menu.Background = MenuBrush;
        _menu.Foreground = TextBrush;
        _menu.Padding = new Thickness(12, 0, 0, 0);
        _menu.FontSize = 12;

        foreach (var item in TopLevelMenuItems(_menu))
        {
            item.Padding = new Thickness(10, 4);
            item.Margin = new Thickness(0);
            item.Foreground = TextBrush;
            item.Background = Brushes.Transparent;
        }

        var theme = UtilityButton("◔", "Toggle light/dark theme");
        theme.Click += (_, _) =>
        {
            if (Avalonia.Application.Current is not { } app) return;
            app.RequestedThemeVariant =
                _window.ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
        };

        var project = UtilityButton("⚙", "Project menu");
        project.Click += (_, _) =>
        {
            var projectMenu = TopLevelMenuItems(_menu)
                .FirstOrDefault(item => string.Equals(
                    NormalizeHeader(item.Header?.ToString()),
                    "Project",
                    StringComparison.OrdinalIgnoreCase));
            projectMenu?.Focus();
        };

        var utilities = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Children = { theme, project }
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Background = MenuBrush
        };
        row.Children.Add(_menu);
        Grid.SetColumn(utilities, 1);
        row.Children.Add(utilities);

        return new Border
        {
            Background = MenuBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row
        };
    }

    private static Button UtilityButton(string glyph, string tip)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontSize = 14,
                Foreground = MutedTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Width = 34,
            Height = 28,
            MinWidth = 34,
            MinHeight = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, tip);
        button.PointerEntered += (_, _) => button.Background = SurfaceHoverBrush;
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        return button;
    }

    private void StyleWorkspaceTabs()
    {
        if (_disposed) return;

        var workspace = _root.Children
            .OfType<Grid>()
            .FirstOrDefault(grid => Grid.GetRow(grid) == 2);
        if (workspace is null) return;

        workspace.Background = ChromeBrush;
        workspace.Margin = new Thickness(20, 0, 20, 0);

        if (workspace.ColumnDefinitions.Count >= 5)
        {
            workspace.ColumnDefinitions[0].Width = new GridLength(232);
            workspace.ColumnDefinitions[4].Width = new GridLength(428);
        }

        foreach (var child in workspace.Children.OfType<Control>())
        {
            if (child is Border border && border.Child is TabControl borderedTabs)
            {
                border.Background = SurfaceBrush;
                border.BorderBrush = BorderBrush;
                border.BorderThickness = new Thickness(1);
                border.CornerRadius = new CornerRadius(8);
                border.ClipToBounds = true;
                StyleTabControl(borderedTabs);
            }
            else if (child is TabControl tabs)
            {
                tabs.Background = SurfaceBrush;
                StyleTabControl(tabs);
            }
        }
    }

    private void StyleTabControl(TabControl tabs)
    {
        tabs.Background = SurfaceBrush;
        tabs.Margin = new Thickness(0);

        if (_styledTabs.Add(tabs))
            tabs.SelectionChanged += (_, _) => UpdateTabSelection(tabs);

        foreach (var item in TabItems(tabs))
        {
            item.MinHeight = 34;
            item.Padding = new Thickness(11, 7);
            item.Margin = new Thickness(1, 0);
            item.FontSize = 11.5;
            item.HorizontalContentAlignment = HorizontalAlignment.Center;
        }

        UpdateTabSelection(tabs);
    }

    private static void UpdateTabSelection(TabControl tabs)
    {
        foreach (var item in TabItems(tabs))
        {
            var selected = ReferenceEquals(item, tabs.SelectedItem);
            item.Background = selected ? AccentSoftBrush : Brushes.Transparent;
            item.Foreground = selected ? AccentBrush : MutedTextBrush;
            item.FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }

    private static IEnumerable<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is IEnumerable source)
            return source.Cast<object?>().OfType<TabItem>();
        return tabs.Items.OfType<TabItem>();
    }

    private static IEnumerable<MenuItem> TopLevelMenuItems(Menu menu)
    {
        if (menu.ItemsSource is IEnumerable source)
            return source.Cast<object?>().OfType<MenuItem>();
        return menu.Items.OfType<MenuItem>();
    }

    private void TitleHostPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint((Control)sender!).Properties.IsLeftButtonPressed) return;

        if (e.ClickCount >= 2)
        {
            ToggleMaximized();
            e.Handled = true;
            return;
        }

        _window.BeginMoveDrag(e);
    }

    private void ToggleMaximized()
        => _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void WindowOpened(object? sender, EventArgs e)
    {
        _window.WindowDecorations = WindowDecorations.BorderOnly;
        StyleWorkspaceTabs();
    }

    private void WindowLayoutUpdated(object? sender, EventArgs e)
    {
        if (_styledTabs.Count < 3)
            StyleWorkspaceTabs();
    }

    private void WindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.TitleProperty)
            _titleText.Text = DisplayTitle(_window.Title);
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.PropertyChanged -= WindowPropertyChanged;
        _window.Opened -= WindowOpened;
        _window.LayoutUpdated -= WindowLayoutUpdated;
        _window.Closed -= WindowClosed;
    }

    private static string DisplayTitle(string? title)
    {
        var value = string.IsNullOrWhiteSpace(title) ? "Typescribe" : title.Trim();
        return string.Equals(value, "Typescribe — Typescribe", StringComparison.Ordinal)
            ? "Typescribe"
            : value;
    }

    private static string NormalizeHeader(string? header)
        => (header ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal);

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}
