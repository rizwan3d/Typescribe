using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop;

/// <summary>
/// Keeps the left project pane focused on navigation and exposes project search in the
/// top menu row, similar to an IDE command/search box.
/// </summary>
internal sealed class StudioNavigationPolish
{
    private static readonly HashSet<string> HiddenLeftTabs = new(StringComparer.Ordinal)
    {
        "Search",
        "Collections",
        "Favorites"
    };

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly TextBox _quickSearch = new()
    {
        Name = "ProjectSearchBox",
        Width = 360,
        MinWidth = 240,
        Height = 30,
        Watermark = "Search project (Ctrl+F)",
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(10, 2, 8, 2)
    };

    private Grid? _root;
    private Menu? _menu;
    private StackPanel? _topBar;
    private bool _findMenuHooked;
    private bool _disposed;

    private StudioNavigationPolish(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var polish = new StudioNavigationPolish(window, viewModel);
        polish.Attach();
    }

    private void Attach()
    {
        _quickSearch.KeyDown += QuickSearchKeyDown;
        _window.AddHandler(InputElement.KeyDownEvent, WindowPreviewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        _window.Opened += WindowOpened;
        _window.LayoutUpdated += WindowLayoutUpdated;
        _window.Closed += WindowClosed;
        ApplyLayout();
    }

    private void WindowOpened(object? sender, EventArgs e) => ApplyLayout();

    private void WindowLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_disposed) ApplyLayout();
    }

    private void ApplyLayout()
    {
        EnsureTopSearch();
        RemoveLegacyLeftTabs();
        HookFindMenuItem();
    }

    private void EnsureTopSearch()
    {
        if (_topBar is not null || _window.Content is not Grid root) return;

        _root = root;
        _menu = root.Children
            .OfType<Menu>()
            .FirstOrDefault(control => Grid.GetRow(control) == 0);
        if (_menu is null) return;

        // Put the native Menu and the search box in one horizontal container. StackPanel measures
        // the Menu at its natural content width, so the search box is guaranteed to render directly
        // after Help instead of competing with a full-width Menu or relying on overlay coordinates.
        root.Children.Remove(_menu);
        _menu.HorizontalAlignment = HorizontalAlignment.Left;
        _menu.VerticalAlignment = VerticalAlignment.Center;

        _topBar = new StackPanel
        {
            Name = "TopMenuSearchBar",
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _menu, _quickSearch }
        };

        Grid.SetRow(_topBar, 0);
        root.Children.Add(_topBar);
    }

    private void RemoveLegacyLeftTabs()
    {
        var leftTabs = _window.GetVisualDescendants()
            .OfType<TabControl>()
            .FirstOrDefault(tab => GetTabItems(tab).Any(IsBinderTab));
        if (leftTabs is null) return;

        var items = GetTabItems(leftTabs);
        var filtered = items
            .Where(item => !HiddenLeftTabs.Contains(item.Header?.ToString() ?? string.Empty))
            .ToArray();

        if (filtered.Length != items.Count)
            leftTabs.ItemsSource = filtered;

        if (leftTabs.SelectedIndex < 0 && filtered.Length > 0)
            leftTabs.SelectedIndex = 0;
    }

    private void HookFindMenuItem()
    {
        if (_findMenuHooked || _menu is null) return;

        var findItem = EnumerateMenuItems(_menu.ItemsSource)
            .FirstOrDefault(item => string.Equals(
                NormalizeHeader(item.Header?.ToString()),
                "Find in Project",
                StringComparison.OrdinalIgnoreCase));
        if (findItem is null) return;

        // StudioWorkspaceWindow originally binds Ctrl+F to the removed left-panel Search tab.
        // Remove that accelerator so the tunnel handler below exclusively owns Ctrl+F.
        findItem.InputGesture = null;
        findItem.Click += (_, _) => FocusQuickSearchDeferred();
        _findMenuHooked = true;
    }

    private async void QuickSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunQuickSearchAsync();
    }

    private void WindowPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || (e.KeyModifiers & KeyModifiers.Control) == 0) return;

        e.Handled = true;
        FocusQuickSearchDeferred();
    }

    private void FocusQuickSearchDeferred()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            _quickSearch.Focus();
            _quickSearch.SelectAll();
        });
    }

    private async Task RunQuickSearchAsync()
    {
        var query = _quickSearch.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;

        await _viewModel.SearchAsync(query);
        var results = _viewModel.SearchResults.ToArray();
        if (results.Length == 0)
        {
            _quickSearch.Watermark = "No results — search project";
            return;
        }

        _quickSearch.Watermark = "Search project (Ctrl+F)";
        if (results.Length == 1)
        {
            await _viewModel.GoToSearchHitAsync(results[0]);
            return;
        }

        var rows = results
            .Select(static hit => hit.Line == 0
                ? $"{hit.Title} [title]  {hit.Preview}"
                : $"{hit.Title}:{hit.Line}  {hit.Preview}")
            .ToArray();

        var list = new ListBox
        {
            ItemsSource = rows,
            SelectedIndex = 0,
            MinHeight = 260
        };
        var open = new Button { Content = "Open", MinWidth = 88 };
        var cancel = new Button { Content = "Cancel", MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { open, cancel }
        };

        var dialog = new Window
        {
            Title = $"Search — {query}",
            Width = 760,
            Height = 480,
            MinWidth = 520,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                Margin = new Thickness(12),
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{results.Length} result(s) for ‘{query}’",
                        Margin = new Thickness(0, 0, 0, 8)
                    },
                    list
                }
            }
        };
        Grid.SetRow(list, 1);
        Grid.SetRow(buttons, 2);
        ((Grid)dialog.Content!).Children.Add(buttons);

        open.Click += (_, _) => dialog.Close(list.SelectedIndex >= 0 ? list.SelectedIndex : null);
        cancel.Click += (_, _) => dialog.Close(null);
        list.DoubleTapped += (_, _) => dialog.Close(list.SelectedIndex >= 0 ? list.SelectedIndex : null);

        var selectedIndex = await dialog.ShowDialog<int?>(_window);
        if (selectedIndex is int index && index >= 0 && index < results.Length)
            await _viewModel.GoToSearchHitAsync(results[index]);
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _quickSearch.KeyDown -= QuickSearchKeyDown;
        _window.RemoveHandler(InputElement.KeyDownEvent, WindowPreviewKeyDown);
        _window.Opened -= WindowOpened;
        _window.LayoutUpdated -= WindowLayoutUpdated;
        _window.Closed -= WindowClosed;
    }

    private static bool IsBinderTab(TabItem item)
        => string.Equals(item.Header?.ToString(), "Binder", StringComparison.Ordinal);

    private static List<TabItem> GetTabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is IEnumerable source)
            return source.Cast<object>().OfType<TabItem>().ToList();

        return tabs.Items.Cast<object>().OfType<TabItem>().ToList();
    }

    private static IEnumerable<MenuItem> EnumerateMenuItems(object? source)
    {
        if (source is not IEnumerable enumerable) yield break;

        foreach (var entry in enumerable)
        {
            if (entry is not MenuItem item) continue;
            yield return item;
            foreach (var child in EnumerateMenuItems(item.ItemsSource))
                yield return child;
        }
    }

    private static string NormalizeHeader(string? header)
        => (header ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal);
}
