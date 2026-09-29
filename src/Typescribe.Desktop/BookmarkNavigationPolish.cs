using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop;

/// <summary>
/// Keeps the injected Bookmarks tab useful and self-explanatory without changing
/// StudioScriveningsFeatures' bookmark persistence/navigation behavior.
/// </summary>
internal sealed class BookmarkNavigationPolish
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;

    private TabItem? _bookmarksTab;
    private ListBox? _bookmarkList;
    private Button? _addButton;
    private Button? _removeButton;
    private TextBlock? _emptyState;
    private bool _wired;
    private bool _disposed;

    private BookmarkNavigationPolish(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var polish = new BookmarkNavigationPolish(window, viewModel);
        window.Opened += polish.OnOpened;
        window.LayoutUpdated += polish.OnLayoutUpdated;
        window.Closed += polish.OnClosed;
        viewModel.StateChanged += polish.OnStateChanged;
        polish.TryInstall();
    }

    private void OnOpened(object? sender, EventArgs e) => TryInstall();

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_wired) TryInstall();
        UpdateState();
    }

    private void OnStateChanged(object? sender, EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateState);

    private void TryInstall()
    {
        if (_disposed || _wired) return;

        var leftTabs = _window.GetVisualDescendants()
            .OfType<TabControl>()
            .FirstOrDefault(tabs => TabItems(tabs)
                .Any(item => string.Equals(item.Header?.ToString(), "Bookmarks", StringComparison.Ordinal)));
        if (leftTabs is null) return;

        _bookmarksTab = TabItems(leftTabs)
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), "Bookmarks", StringComparison.Ordinal));
        if (_bookmarksTab?.Content is not Grid panel) return;

        _bookmarkList = panel.Children.OfType<ListBox>().FirstOrDefault();
        var buttons = panel.Children.OfType<StackPanel>()
            .FirstOrDefault(stack => stack.Orientation == Orientation.Horizontal);
        if (_bookmarkList is null || buttons is null) return;

        _addButton = buttons.Children.OfType<Button>()
            .FirstOrDefault(button => string.Equals(button.Content?.ToString(), "Add Bookmark", StringComparison.Ordinal));
        _removeButton = buttons.Children.OfType<Button>()
            .FirstOrDefault(button => string.Equals(button.Content?.ToString(), "Remove", StringComparison.Ordinal));
        if (_addButton is null || _removeButton is null) return;

        buttons.Margin = new Thickness(8, 8, 8, 6);
        _addButton.MinWidth = 112;
        _removeButton.MinWidth = 74;
        _bookmarkList.Margin = new Thickness(8, 0, 8, 8);
        _bookmarkList.SelectionChanged += (_, _) => UpdateState();

        ToolTip.SetTip(_addButton, "Add a bookmark at the caret in the selected document");
        ToolTip.SetTip(_removeButton, "Remove the selected bookmark");
        ToolTip.SetTip(_bookmarkList, "Double-click a bookmark to jump to it");

        _emptyState = new TextBlock
        {
            Text = "No bookmarks yet.\nSelect a document, place the caret, then choose Add Bookmark.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 220,
            Margin = new Thickness(18),
            Opacity = 0.62,
            IsHitTestVisible = false
        };
        Grid.SetRow(_emptyState, 1);
        panel.Children.Add(_emptyState);

        _wired = true;
        UpdateState();
    }

    private void UpdateState()
    {
        if (_disposed || !_wired) return;

        var canAdd = _viewModel.SelectedRow?.Node.IsDocument == true;
        if (_addButton is not null)
        {
            _addButton.IsEnabled = canAdd;
            ToolTip.SetTip(_addButton, canAdd
                ? "Add a bookmark at the caret in the selected document"
                : "Select a document before adding a bookmark");
        }

        if (_removeButton is not null)
            _removeButton.IsEnabled = _bookmarkList?.SelectedItem is not null;

        if (_emptyState is not null && _bookmarkList is not null)
            _emptyState.IsVisible = _bookmarkList.ItemCount == 0;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        _viewModel.StateChanged -= OnStateChanged;
    }

    private static List<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is IEnumerable source)
            return source.Cast<object>().OfType<TabItem>().ToList();

        return tabs.Items.Cast<object>().OfType<TabItem>().ToList();
    }
}
