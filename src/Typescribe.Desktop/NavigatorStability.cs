using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop;

/// <summary>
/// Gives the left navigator one stable ItemsSource. BinderRows always remains the complete,
/// canonical flattened project tree; collapse is only a projection over that tree. Incoming
/// Binder views are used only to infer which visible containers are collapsed, so a stale or
/// incomplete array can never become authoritative and permanently hide unrelated rows.
/// Bookmarks likewise keep a permanent observable source to avoid container flicker.
/// </summary>
internal sealed class NavigatorStability
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly ObservableCollection<BinderRowViewModel> _visibleBinderRows = [];
    private readonly ObservableCollection<object?> _bookmarkItems = [];
    private readonly HashSet<string> _collapsedContainers = new(StringComparer.Ordinal);

    private ListBox? _binder;
    private TabControl? _leftTabs;
    private ListBox? _bookmarkList;
    private bool _restoringBinder;
    private bool _restoringBookmarks;
    private bool _discoverScheduled;
    private bool _disposed;

    private NavigatorStability(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var host = new NavigatorStability(window, viewModel);
        window.Opened += host.OnOpened;
        window.LayoutUpdated += host.OnLayoutUpdated;
        window.Closed += host.OnClosed;
        viewModel.StateChanged += host.OnStateChanged;
        viewModel.BinderRows.CollectionChanged += host.OnCanonicalBinderChanged;
        host.ScheduleDiscover();
    }

    private void OnOpened(object? sender, EventArgs e) => ScheduleDiscover();

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_binder is null || _bookmarkList is null) ScheduleDiscover();
    }

    private void OnStateChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            SynchronizeBinderProjection();
            RestoreBinderSource();
            SynchronizeBinderSelection();
            if (_bookmarkList is null) ScheduleDiscover();
        }, DispatcherPriority.Background);

    private void OnCanonicalBinderChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed) return;

        var validIds = _viewModel.BinderRows
            .Select(static row => row.Node.PersistentId)
            .ToHashSet(StringComparer.Ordinal);
        _collapsedContainers.RemoveWhere(id => !validIds.Contains(id));

        SynchronizeBinderProjection();
        RestoreBinderSource();
        SynchronizeBinderSelection();
    }

    private void ScheduleDiscover()
    {
        if (_disposed || _discoverScheduled) return;
        _discoverScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _discoverScheduled = false;
            if (!_disposed) Discover();
        }, DispatcherPriority.Background);
    }

    private void Discover()
    {
        var controls = _window.GetVisualDescendants().OfType<Control>().ToArray();

        if (_binder is null)
        {
            var binder = controls.OfType<ListBox>().FirstOrDefault(static list => list.ContextMenu is not null);
            if (binder is not null) AttachBinder(binder);
        }

        if (_leftTabs is null)
        {
            _leftTabs = controls.OfType<TabControl>().FirstOrDefault(HasBinderTab);
            if (_leftTabs is not null)
                _leftTabs.SelectionChanged += LeftTabSelectionChanged;
        }

        AttachBookmarksList();

        if (_binder is not null && _leftTabs is not null && _bookmarkList is not null)
            _window.LayoutUpdated -= OnLayoutUpdated;
    }

    private void AttachBinder(ListBox binder)
    {
        _binder = binder;
        if (binder.ItemsSource is IEnumerable<BinderRowViewModel> proposal &&
            !ReferenceEquals(binder.ItemsSource, _viewModel.BinderRows))
            CaptureCollapseProposal(proposal);

        SynchronizeBinderProjection();
        _restoringBinder = true;
        try { binder.ItemsSource = _visibleBinderRows; }
        finally { _restoringBinder = false; }
        binder.PropertyChanged += BinderPropertyChanged;
        SynchronizeBinderSelection();
    }

    private void BinderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_disposed || _restoringBinder || _binder is null || e.Property != ItemsControl.ItemsSourceProperty)
            return;
        if (ReferenceEquals(_binder.ItemsSource, _visibleBinderRows)) return;

        var proposedSource = _binder.ItemsSource;

        // The base workspace assigns the full BinderRows collection during ordinary state
        // updates. That is not an Expand All request, so never erase collapse state for it.
        if (!ReferenceEquals(proposedSource, _viewModel.BinderRows) &&
            proposedSource is IEnumerable<BinderRowViewModel> proposal)
            CaptureCollapseProposal(proposal);

        SynchronizeBinderProjection();
        RestoreBinderSource();
        SynchronizeBinderSelection();
    }

    private void CaptureCollapseProposal(IEnumerable<BinderRowViewModel> proposal)
    {
        var canonical = _viewModel.BinderRows;
        if (canonical.Count == 0) return;

        var proposed = proposal.ToArray();
        if (proposed.Length == canonical.Count &&
            proposed.Select(static row => row.Node.PersistentId)
                .SequenceEqual(canonical.Select(static row => row.Node.PersistentId), StringComparer.Ordinal))
        {
            _collapsedContainers.Clear();
            return;
        }

        var visibleIds = proposed
            .Select(static row => row.Node.PersistentId)
            .ToHashSet(StringComparer.Ordinal);

        // Only containers that are themselves visible can communicate a collapse/expand
        // decision. Containers hidden under an ancestor keep their previous state.
        for (var index = 0; index < canonical.Count; index++)
        {
            var row = canonical[index];
            if (!row.Node.IsContainer || !visibleIds.Contains(row.Node.PersistentId)) continue;

            var hasDescendants = index + 1 < canonical.Count && canonical[index + 1].Depth > row.Depth;
            if (!hasDescendants)
            {
                _collapsedContainers.Remove(row.Node.PersistentId);
                continue;
            }

            var anyVisibleDescendant = false;
            for (var cursor = index + 1; cursor < canonical.Count; cursor++)
            {
                var descendant = canonical[cursor];
                if (descendant.Depth <= row.Depth) break;
                if (!visibleIds.Contains(descendant.Node.PersistentId)) continue;
                anyVisibleDescendant = true;
                break;
            }

            if (anyVisibleDescendant)
                _collapsedContainers.Remove(row.Node.PersistentId);
            else
                _collapsedContainers.Add(row.Node.PersistentId);
        }
    }

    private void SynchronizeBinderProjection()
    {
        var target = BuildVisibleBinderRows().ToArray();
        var shared = Math.Min(_visibleBinderRows.Count, target.Length);

        for (var index = 0; index < shared; index++)
        {
            var current = _visibleBinderRows[index];
            var desired = target[index];
            if (!ReferenceEquals(current, desired)) _visibleBinderRows[index] = desired;
        }

        while (_visibleBinderRows.Count > target.Length)
            _visibleBinderRows.RemoveAt(_visibleBinderRows.Count - 1);
        for (var index = _visibleBinderRows.Count; index < target.Length; index++)
            _visibleBinderRows.Add(target[index]);
    }

    private IEnumerable<BinderRowViewModel> BuildVisibleBinderRows()
    {
        int? hiddenBelowDepth = null;
        foreach (var row in _viewModel.BinderRows)
        {
            if (hiddenBelowDepth is { } depth)
            {
                if (row.Depth > depth) continue;
                hiddenBelowDepth = null;
            }

            yield return row;
            if (row.Node.IsContainer && _collapsedContainers.Contains(row.Node.PersistentId))
                hiddenBelowDepth = row.Depth;
        }
    }

    private void RestoreBinderSource()
    {
        if (_binder is null || ReferenceEquals(_binder.ItemsSource, _visibleBinderRows)) return;
        _restoringBinder = true;
        try { _binder.ItemsSource = _visibleBinderRows; }
        finally { _restoringBinder = false; }
    }

    private void SynchronizeBinderSelection()
    {
        if (_binder is null) return;

        var selectedId = _viewModel.SelectedRow?.Node.PersistentId;
        if (selectedId is null)
        {
            _binder.SelectedItem = null;
            return;
        }

        var visible = _visibleBinderRows.FirstOrDefault(row =>
            string.Equals(row.Node.PersistentId, selectedId, StringComparison.Ordinal));
        if (visible is not null && !ReferenceEquals(_binder.SelectedItem, visible))
            _binder.SelectedItem = visible;
    }

    private void LeftTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => Dispatcher.UIThread.Post(AttachBookmarksList, DispatcherPriority.Background);

    private void AttachBookmarksList()
    {
        if (_disposed || _leftTabs is null || _bookmarkList is not null) return;
        var tab = TabItems(_leftTabs).FirstOrDefault(static item =>
            string.Equals(item.Header?.ToString(), "Bookmarks", StringComparison.Ordinal));
        if (tab?.Content is not Control content) return;

        var list = FindListBox(content);
        if (list is null || ReferenceEquals(list, _binder)) return;

        _bookmarkList = list;
        SynchronizeBookmarks(list.ItemsSource);
        _restoringBookmarks = true;
        try { list.ItemsSource = _bookmarkItems; }
        finally { _restoringBookmarks = false; }
        list.PropertyChanged += BookmarkPropertyChanged;
    }

    private void BookmarkPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_disposed || _restoringBookmarks || _bookmarkList is null ||
            e.Property != ItemsControl.ItemsSourceProperty || ReferenceEquals(_bookmarkList.ItemsSource, _bookmarkItems))
            return;

        var selected = _bookmarkList.SelectedItem;
        SynchronizeBookmarks(_bookmarkList.ItemsSource);
        _restoringBookmarks = true;
        try
        {
            _bookmarkList.ItemsSource = _bookmarkItems;
            if (selected is not null)
                _bookmarkList.SelectedItem = _bookmarkItems.FirstOrDefault(item => Equals(item, selected));
        }
        finally { _restoringBookmarks = false; }
    }

    private void SynchronizeBookmarks(IEnumerable? source)
    {
        if (ReferenceEquals(source, _bookmarkItems)) return;
        var incoming = source?.Cast<object?>().ToArray() ?? [];
        var shared = Math.Min(_bookmarkItems.Count, incoming.Length);

        for (var index = 0; index < shared; index++)
            if (!Equals(_bookmarkItems[index], incoming[index])) _bookmarkItems[index] = incoming[index];
        while (_bookmarkItems.Count > incoming.Length)
            _bookmarkItems.RemoveAt(_bookmarkItems.Count - 1);
        for (var index = _bookmarkItems.Count; index < incoming.Length; index++)
            _bookmarkItems.Add(incoming[index]);
    }

    private static bool HasBinderTab(TabControl tabs)
        => TabItems(tabs).Any(static item => string.Equals(item.Header?.ToString(), "Binder", StringComparison.Ordinal));

    private static IEnumerable<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is IEnumerable source) return source.Cast<object?>().OfType<TabItem>();
        return tabs.Items.OfType<TabItem>();
    }

    private static ListBox? FindListBox(Control control)
    {
        if (control is ListBox list) return list;
        if (control is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                var found = FindListBox(child);
                if (found is not null) return found;
            }
        }
        if (control is ContentControl content && content.Content is Control childContent)
            return FindListBox(childContent);
        if (control is Decorator decorator && decorator.Child is Control child)
            return FindListBox(child);
        return null;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        _viewModel.StateChanged -= OnStateChanged;
        _viewModel.BinderRows.CollectionChanged -= OnCanonicalBinderChanged;
        if (_binder is not null) _binder.PropertyChanged -= BinderPropertyChanged;
        if (_bookmarkList is not null) _bookmarkList.PropertyChanged -= BookmarkPropertyChanged;
        if (_leftTabs is not null) _leftTabs.SelectionChanged -= LeftTabSelectionChanged;
    }
}
