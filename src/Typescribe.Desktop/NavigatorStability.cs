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
/// Gives the left navigator a single stable item source. The base workspace and the
/// Scrivenings feature both legitimately compute Binder views, but swapping ListBox
/// ItemsSource instances during editor/autosave updates tears down item containers and can
/// make rows appear to disappear. This coordinator accepts those incoming views, updates a
/// permanent ObservableCollection in place, and keeps selection/collapse semantics intact.
/// It also gives Bookmarks a stable source to eliminate visible flicker.
/// </summary>
internal sealed class NavigatorStability
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly ObservableCollection<BinderRowViewModel> _binderItems = [];
    private readonly ObservableCollection<object?> _bookmarkItems = [];
    private readonly DispatcherTimer _masterReconcileTimer;

    private ListBox? _binder;
    private ListBox? _bookmarks;
    private TabControl? _leftTabs;
    private bool _restoringBinder;
    private bool _restoringBookmarks;
    private bool _discoverScheduled;
    private bool _disposed;

    private NavigatorStability(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
        _masterReconcileTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _masterReconcileTimer.Tick += MasterReconcileTick;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var coordinator = new NavigatorStability(window, viewModel);
        window.Opened += coordinator.OnOpened;
        window.LayoutUpdated += coordinator.OnLayoutUpdated;
        window.Closed += coordinator.OnClosed;
        viewModel.BinderRows.CollectionChanged += coordinator.MasterBinderChanged;
        coordinator.ScheduleDiscover();
    }

    private void OnOpened(object? sender, EventArgs e) => ScheduleDiscover();

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_binder is null || _leftTabs is null || _bookmarks is null)
            ScheduleDiscover();
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
            var candidate = controls.OfType<ListBox>().FirstOrDefault(static list => list.ContextMenu is not null);
            if (candidate is not null) AttachBinder(candidate);
        }

        if (_leftTabs is null)
        {
            _leftTabs = controls.OfType<TabControl>().FirstOrDefault(HasBinderTab);
            if (_leftTabs is not null)
            {
                _leftTabs.SelectionChanged += LeftTabsSelectionChanged;
                _leftTabs.PropertyChanged += LeftTabsPropertyChanged;
            }
        }

        AttachBookmarks();

        if (_binder is not null && _leftTabs is not null && _bookmarks is not null)
            _window.LayoutUpdated -= OnLayoutUpdated;
    }

    private void AttachBinder(ListBox binder)
    {
        _binder = binder;
        var initial = Rows(binder.ItemsSource);
        SyncBinder(initial.Length > 0 ? initial : _viewModel.BinderRows.ToArray());
        _restoringBinder = true;
        try
        {
            binder.ItemsSource = _binderItems;
            RestoreBinderSelection();
        }
        finally { _restoringBinder = false; }
        binder.PropertyChanged += BinderPropertyChanged;
    }

    private void BinderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_disposed || _restoringBinder || _binder is null || e.Property != ItemsControl.ItemsSourceProperty)
            return;

        var incomingSource = _binder.ItemsSource;
        if (ReferenceEquals(incomingSource, _binderItems)) return;

        // A base-window rebind to BinderRows is not a request to expand the entire tree;
        // it is ordinary state churn. Preserve the current collapsed view and only reconcile
        // structural changes against the master list.
        if (ReferenceEquals(incomingSource, _viewModel.BinderRows))
            ReconcileWithMasterPreservingCollapse();
        else
        {
            var incoming = Rows(incomingSource);
            if (incoming.Length > 0 || _viewModel.BinderRows.Count == 0)
                SyncBinder(incoming);
        }

        RestoreStableBinderSource();
    }

    private void RestoreStableBinderSource()
    {
        if (_binder is null) return;
        _restoringBinder = true;
        try
        {
            _binder.ItemsSource = _binderItems;
            RestoreBinderSelection();
        }
        finally { _restoringBinder = false; }
    }

    private void MasterBinderChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed) return;
        _masterReconcileTimer.Stop();
        _masterReconcileTimer.Start();
    }

    private void MasterReconcileTick(object? sender, EventArgs e)
    {
        _masterReconcileTimer.Stop();
        if (_disposed || _binder is null) return;

        // Give StudioScriveningsFeatures first chance to publish its newly-computed visible
        // source. If it already did, consume that source directly. Otherwise reconcile the
        // stable view with the master tree so add/delete/move operations never lose rows.
        if (!ReferenceEquals(_binder.ItemsSource, _binderItems) &&
            !ReferenceEquals(_binder.ItemsSource, _viewModel.BinderRows))
        {
            var incoming = Rows(_binder.ItemsSource);
            if (incoming.Length > 0 || _viewModel.BinderRows.Count == 0)
                SyncBinder(incoming);
            RestoreStableBinderSource();
            return;
        }

        ReconcileWithMasterPreservingCollapse();
        RestoreBinderSelection();
    }

    private void ReconcileWithMasterPreservingCollapse()
    {
        var master = _viewModel.BinderRows.ToArray();
        if (master.Length == 0)
        {
            SyncBinder([]);
            return;
        }

        if (_binderItems.Count == 0)
        {
            SyncBinder(master);
            return;
        }

        var visibleIds = _binderItems.Select(static row => row.Node.PersistentId).ToHashSet(StringComparer.Ordinal);
        var collapsed = InferCollapsedContainers(master, visibleIds);
        var visible = new List<BinderRowViewModel>(master.Length);
        int? hiddenBelowDepth = null;

        foreach (var row in master)
        {
            if (hiddenBelowDepth is { } hiddenDepth)
            {
                if (row.Depth > hiddenDepth) continue;
                hiddenBelowDepth = null;
            }

            visible.Add(row);
            if (row.Node.IsContainer && collapsed.Contains(row.Node.PersistentId))
                hiddenBelowDepth = row.Depth;
        }

        SyncBinder(visible);
    }

    private static HashSet<string> InferCollapsedContainers(
        IReadOnlyList<BinderRowViewModel> master,
        IReadOnlySet<string> visibleIds)
    {
        var collapsed = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < master.Count; index++)
        {
            var row = master[index];
            if (!row.Node.IsContainer || !visibleIds.Contains(row.Node.PersistentId)) continue;

            var hasDescendant = false;
            var hasVisibleDescendant = false;
            for (var cursor = index + 1; cursor < master.Count; cursor++)
            {
                var child = master[cursor];
                if (child.Depth <= row.Depth) break;
                hasDescendant = true;
                if (visibleIds.Contains(child.Node.PersistentId))
                {
                    hasVisibleDescendant = true;
                    break;
                }
            }

            if (hasDescendant && !hasVisibleDescendant)
                collapsed.Add(row.Node.PersistentId);
        }
        return collapsed;
    }

    private void SyncBinder(IEnumerable<BinderRowViewModel> source)
    {
        var incoming = source.ToArray();
        var shared = Math.Min(_binderItems.Count, incoming.Length);
        for (var index = 0; index < shared; index++)
        {
            if (!ReferenceEquals(_binderItems[index], incoming[index]))
                _binderItems[index] = incoming[index];
        }
        while (_binderItems.Count > incoming.Length)
            _binderItems.RemoveAt(_binderItems.Count - 1);
        for (var index = _binderItems.Count; index < incoming.Length; index++)
            _binderItems.Add(incoming[index]);
    }

    private void RestoreBinderSelection()
    {
        if (_binder is null) return;
        var selectedId = _viewModel.SelectedRow?.Node.PersistentId ??
                         (_binder.SelectedItem as BinderRowViewModel)?.Node.PersistentId;
        if (selectedId is null) return;
        var match = _binderItems.FirstOrDefault(row =>
            string.Equals(row.Node.PersistentId, selectedId, StringComparison.Ordinal));
        if (match is not null) _binder.SelectedItem = match;
    }

    private void LeftTabsSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => Dispatcher.UIThread.Post(AttachBookmarks, DispatcherPriority.Background);

    private void LeftTabsPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ItemsControl.ItemsSourceProperty)
            Dispatcher.UIThread.Post(AttachBookmarks, DispatcherPriority.Background);
    }

    private void AttachBookmarks()
    {
        if (_disposed || _leftTabs is null || _bookmarks is not null) return;
        var tab = TabItems(_leftTabs).FirstOrDefault(static item =>
            string.Equals(item.Header?.ToString(), "Bookmarks", StringComparison.Ordinal));
        if (tab?.Content is not Control content) return;

        var list = content as ListBox ?? content.GetVisualDescendants().OfType<ListBox>().FirstOrDefault();
        if (list is null || ReferenceEquals(list, _binder)) return;

        _bookmarks = list;
        SyncBookmarks(list.ItemsSource);
        _restoringBookmarks = true;
        try { list.ItemsSource = _bookmarkItems; }
        finally { _restoringBookmarks = false; }
        list.PropertyChanged += BookmarksPropertyChanged;
    }

    private void BookmarksPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_disposed || _restoringBookmarks || _bookmarks is null ||
            e.Property != ItemsControl.ItemsSourceProperty || ReferenceEquals(_bookmarks.ItemsSource, _bookmarkItems))
            return;

        var selected = _bookmarks.SelectedItem;
        SyncBookmarks(_bookmarks.ItemsSource);
        _restoringBookmarks = true;
        try
        {
            _bookmarks.ItemsSource = _bookmarkItems;
            if (selected is not null)
                _bookmarks.SelectedItem = _bookmarkItems.FirstOrDefault(item => Equals(item, selected));
        }
        finally { _restoringBookmarks = false; }
    }

    private void SyncBookmarks(IEnumerable? source)
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

    private static BinderRowViewModel[] Rows(IEnumerable? source)
        => source?.Cast<object?>().OfType<BinderRowViewModel>().ToArray() ?? [];

    private static bool HasBinderTab(TabControl tabs)
        => TabItems(tabs).Any(static tab => string.Equals(tab.Header?.ToString(), "Binder", StringComparison.Ordinal));

    private static IEnumerable<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is IEnumerable source)
            return source.Cast<object?>().OfType<TabItem>();
        return tabs.Items.OfType<TabItem>();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _masterReconcileTimer.Stop();
        _masterReconcileTimer.Tick -= MasterReconcileTick;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        _viewModel.BinderRows.CollectionChanged -= MasterBinderChanged;
        if (_binder is not null) _binder.PropertyChanged -= BinderPropertyChanged;
        if (_bookmarks is not null) _bookmarks.PropertyChanged -= BookmarksPropertyChanged;
        if (_leftTabs is not null)
        {
            _leftTabs.SelectionChanged -= LeftTabsSelectionChanged;
            _leftTabs.PropertyChanged -= LeftTabsPropertyChanged;
        }
    }
}
