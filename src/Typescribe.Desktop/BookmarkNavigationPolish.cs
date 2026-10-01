using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.Editing;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop;

/// <summary>
/// Keeps the injected Bookmarks tab useful and self-explanatory while preserving
/// StudioScriveningsFeatures' bookmark persistence and add/remove behavior.
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
    private bool _navigatingSelection;
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
        => Dispatcher.UIThread.Post(UpdateState);

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
        _bookmarkList.SelectionChanged += BookmarkSelectionChanged;

        ToolTip.SetTip(_addButton, "Add a bookmark at the current editor caret and enter a bookmark name");
        ToolTip.SetTip(_removeButton, "Remove the selected bookmark");
        ToolTip.SetTip(_bookmarkList, "Select a bookmark to jump to its saved line");

        _emptyState = new TextBlock
        {
            Text = "No bookmarks yet.\nPlace the editor caret on a line, choose Add Bookmark, then enter a name.",
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

    private async void BookmarkSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateState();
        if (_disposed || _navigatingSelection || _bookmarkList?.SelectedItem is not { } bookmark) return;

        _navigatingSelection = true;
        try
        {
            await NavigateToBookmarkAsync(bookmark);
        }
        catch
        {
            // Bookmark navigation should never make the workspace unusable.
        }
        finally
        {
            _navigatingSelection = false;
        }
    }

    private async Task NavigateToBookmarkAsync(object bookmark)
    {
        if (!TryReadBookmark(bookmark, out var persistentId, out var line, out var column)) return;

        var row = _viewModel.BinderRows.FirstOrDefault(candidate =>
            string.Equals(candidate.Node.PersistentId, persistentId, StringComparison.Ordinal));
        if (row is null) return;

        await _viewModel.SelectAsync(row);

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;

            foreach (var tabs in _window.GetVisualDescendants().OfType<TabControl>())
            {
                var editorTab = TabItems(tabs).FirstOrDefault(item =>
                    string.Equals(item.Header?.ToString(), "Editor", StringComparison.OrdinalIgnoreCase));
                if (editorTab is null) continue;
                tabs.SelectedItem = editorTab;
                break;
            }

            var editors = _window.GetVisualDescendants().OfType<ManuscriptEditor>().ToArray();
            var editor = editors.FirstOrDefault(candidate =>
                string.Equals(candidate.DocumentIdentity, persistentId, StringComparison.Ordinal))
                ?? editors.FirstOrDefault();
            if (editor is null) return;

            editor.NavigateToLine(Math.Max(1, line), Math.Max(1, column));
            editor.Focus();
        }, DispatcherPriority.Background);
    }

    private static bool TryReadBookmark(object bookmark, out string persistentId, out int line, out int column)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = bookmark.GetType();
        persistentId = type.GetProperty("PersistentId", flags)?.GetValue(bookmark) as string ?? string.Empty;
        line = ReadInt(type.GetProperty("Line", flags)?.GetValue(bookmark), 1);
        column = ReadInt(type.GetProperty("Column", flags)?.GetValue(bookmark), 1);
        return !string.IsNullOrWhiteSpace(persistentId);
    }

    private static int ReadInt(object? value, int fallback)
        => value is int number && number > 0 ? number : fallback;

    private void UpdateState()
    {
        if (_disposed || !_wired) return;

        var canAdd = _viewModel.SelectedRow?.Node.IsDocument == true;
        if (_addButton is not null)
        {
            _addButton.IsEnabled = canAdd;
            ToolTip.SetTip(_addButton, canAdd
                ? "Add a bookmark at the current editor caret and enter a bookmark name"
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
        if (_bookmarkList is not null)
            _bookmarkList.SelectionChanged -= BookmarkSelectionChanged;
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
