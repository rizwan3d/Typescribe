using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop;

/// <summary>
/// Keeps the navigator visually stable while high-frequency editor state changes occur.
/// The Binder has two legitimate owners: the base ObservableCollection and the filtered /
/// collapsed Scrivenings view. Ordinary state changes must not make those sources fight.
/// Bookmarks additionally use a permanent observable source so refreshing bookmark data
/// never tears down and recreates the whole ListBox visual tree.
/// </summary>
internal sealed class LeftPanelStability
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly Dictionary<ListBox, SourceSnapshot> _listSnapshots = [];
    private readonly HashSet<ListBox> _restoringLists = [];
    private readonly ObservableCollection<object?> _bookmarkItems = [];

    private ListBox? _binder;
    private ListBox? _bookmarkList;
    private TabControl? _leftTabs;
    private IEnumerable? _preferredBinderSource;
    private bool _restoringBinder;
    private bool _restoringBookmarks;
    private bool _disposed;

    private LeftPanelStability(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var guard = new LeftPanelStability(window, viewModel);
        window.Opened += guard.OnOpened;
        window.LayoutUpdated += guard.OnLayoutUpdated;
        window.Closed += guard.OnClosed;
        viewModel.BinderRows.CollectionChanged += guard.BinderRowsChanged;
        guard.TryAttach();
    }

    private void OnOpened(object? sender, EventArgs e) => TryAttach();

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        TryAttach();
        AttachVisibleLeftLists();
        AttachBookmarksList();
    }

    private void TryAttach()
    {
        if (_disposed) return;
        var controls = _window.GetVisualDescendants().OfType<Control>().ToArray();

        if (_binder is null)
        {
            var binder = controls.OfType<ListBox>().FirstOrDefault(static list => list.ContextMenu is not null);
            if (binder is not null)
            {
                _binder = binder;
                if (!ReferenceEquals(binder.ItemsSource, _viewModel.BinderRows))
                    _preferredBinderSource = binder.ItemsSource;
                binder.PropertyChanged += BinderPropertyChanged;
            }
        }

        if (_leftTabs is null)
        {
            _leftTabs = controls.OfType<TabControl>().FirstOrDefault(HasBinderTab);
            if (_leftTabs is not null)
            {
                _leftTabs.SelectionChanged += LeftTabSelectionChanged;
                _leftTabs.PropertyChanged += LeftTabsPropertyChanged;
            }
        }

        AttachVisibleLeftLists();
        AttachBookmarksList();
    }

    private static bool HasBinderTab(TabControl tabs)
    {
        if (tabs.ItemsSource is not IEnumerable source) return false;
        foreach (var item in source)
        {
            if (item is TabItem tab && string.Equals(tab.Header?.ToString(), "Binder", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private void LeftTabsPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ItemsControl.ItemsSourceProperty) return;
        Dispatcher.UIThread.Post(() =>
        {
            AttachBookmarksList();
            AttachVisibleLeftLists();
        }, DispatcherPriority.Background);
    }

    private void LeftTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            AttachVisibleLeftLists();
            AttachBookmarksList();
        }, DispatcherPriority.Background);

    private void AttachVisibleLeftLists()
    {
        if (_disposed || _leftTabs is null) return;
        foreach (var list in _leftTabs.GetVisualDescendants().OfType<ListBox>())
        {
            if (ReferenceEquals(list, _binder) || ReferenceEquals(list, _bookmarkList) || _listSnapshots.ContainsKey(list)) continue;
            _listSnapshots[list] = Snapshot(list.ItemsSource);
            list.PropertyChanged += LeftListPropertyChanged;
        }
    }

    private void AttachBookmarksList()
    {
        if (_disposed || _leftTabs is null || _bookmarkList is not null) return;
        var bookmarkTab = TabItems(_leftTabs)
            .FirstOrDefault(static tab => string.Equals(tab.Header?.ToString(), "Bookmarks", StringComparison.Ordinal));
        if (bookmarkTab?.Content is not Control content) return;

        var list = FindListBox(content);
        if (list is null || ReferenceEquals(list, _binder)) return;

        _bookmarkList = list;
        SyncBookmarkItems(list.ItemsSource);
        _restoringBookmarks = true;
        try { list.ItemsSource = _bookmarkItems; }
        finally { _restoringBookmarks = false; }
        list.PropertyChanged += BookmarkListPropertyChanged;
    }

    private void BookmarkListPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_disposed || _restoringBookmarks || _bookmarkList is null ||
            e.Property != ItemsControl.ItemsSourceProperty || ReferenceEquals(_bookmarkList.ItemsSource, _bookmarkItems))
            return;

        var incoming = _bookmarkList.ItemsSource;
        var selectedIndex = _bookmarkList.SelectedIndex;
        _restoringBookmarks = true;
        try
        {
            SyncBookmarkItems(incoming);
            _bookmarkList.ItemsSource = _bookmarkItems;
            if (_bookmarkItems.Count > 0 && selectedIndex >= 0)
                _bookmarkList.SelectedIndex = Math.Min(selectedIndex, _bookmarkItems.Count - 1);
        }
        finally
        {
            _restoringBookmarks = false;
        }
    }

    private void SyncBookmarkItems(IEnumerable? source)
    {
        if (ReferenceEquals(source, _bookmarkItems)) return;
        var incoming = source?.Cast<object?>().ToArray() ?? [];

        var shared = Math.Min(_bookmarkItems.Count, incoming.Length);
        for (var index = 0; index < shared; index++)
        {
            if (!Equals(_bookmarkItems[index], incoming[index]))
                _bookmarkItems[index] = incoming[index];
        }

        while (_bookmarkItems.Count > incoming.Length)
            _bookmarkItems.RemoveAt(_bookmarkItems.Count - 1);
        for (var index = _bookmarkItems.Count; index < incoming.Length; index++)
            _bookmarkItems.Add(incoming[index]);
    }

    private static ListBox? FindListBox(Control root)
    {
        if (root is ListBox direct) return direct;
        if (root is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                var found = FindListBox(child);
                if (found is not null) return found;
            }
        }
        if (root is ContentControl contentControl && contentControl.Content is Control content)
            return FindListBox(content);
        if (root is Decorator decorator && decorator.Child is Control childControl)
            return FindListBox(childControl);
        return null;
    }

    private static IEnumerable<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is IEnumerable source)
            return source.Cast<object?>().OfType<TabItem>();
        return tabs.Items.OfType<TabItem>();
    }

    private void BinderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_disposed || _restoringBinder || _binder is null || e.Property != ItemsControl.ItemsSourceProperty)
            return;

        var source = _binder.ItemsSource;
        if (source is null) return;

        if (!ReferenceEquals(source, _viewModel.BinderRows))
        {
            _preferredBinderSource = source;
            return;
        }

        if (_preferredBinderSource is null) return;

        var selected = _binder.SelectedItem;
        _restoringBinder = true;
        try
        {
            _binder.ItemsSource = _preferredBinderSource;
            if (selected is not null) _binder.SelectedItem = selected;
        }
        finally
        {
            _restoringBinder = false;
        }
    }

    private void LeftListPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_disposed || e.Property != ItemsControl.ItemsSourceProperty || sender is not ListBox list ||
            _restoringLists.Contains(list))
            return;

        var current = Snapshot(list.ItemsSource);
        if (!_listSnapshots.TryGetValue(list, out var previous))
        {
            _listSnapshots[list] = current;
            return;
        }

        if (ReferenceEquals(previous.Source, current.Source)) return;
        if (!string.Equals(previous.Signature, current.Signature, StringComparison.Ordinal))
        {
            _listSnapshots[list] = current;
            return;
        }

        var selected = list.SelectedItem;
        _restoringLists.Add(list);
        try
        {
            list.ItemsSource = previous.Source;
            if (selected is not null) list.SelectedItem = selected;
        }
        finally
        {
            _restoringLists.Remove(list);
        }
    }

    private void BinderRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => _preferredBinderSource = null;

    private static SourceSnapshot Snapshot(IEnumerable? source)
        => new(source, BuildSignature(source));

    private static string BuildSignature(IEnumerable? source)
    {
        if (source is null) return "<null>";

        var builder = new StringBuilder(256);
        var count = 0;
        foreach (var item in source)
        {
            if (count >= 512)
            {
                builder.Append("…");
                break;
            }

            switch (item)
            {
                case BinderRowViewModel row:
                    builder.Append(row.Node.PersistentId).Append(':').Append(row.Node.Title);
                    break;
                default:
                    builder.Append(item?.ToString() ?? "<null>");
                    break;
            }
            builder.Append('\u001f');
            count++;
        }

        builder.Insert(0, count).Insert(count.ToString().Length, ':');
        return builder.ToString();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        _viewModel.BinderRows.CollectionChanged -= BinderRowsChanged;

        if (_leftTabs is not null)
        {
            _leftTabs.SelectionChanged -= LeftTabSelectionChanged;
            _leftTabs.PropertyChanged -= LeftTabsPropertyChanged;
        }
        if (_binder is not null) _binder.PropertyChanged -= BinderPropertyChanged;
        if (_bookmarkList is not null) _bookmarkList.PropertyChanged -= BookmarkListPropertyChanged;
        foreach (var list in _listSnapshots.Keys)
            list.PropertyChanged -= LeftListPropertyChanged;

        _listSnapshots.Clear();
        _restoringLists.Clear();
        _bookmarkItems.Clear();
        _binder = null;
        _bookmarkList = null;
        _leftTabs = null;
        _preferredBinderSource = null;
    }

    private sealed record SourceSnapshot(IEnumerable? Source, string Signature);
}
