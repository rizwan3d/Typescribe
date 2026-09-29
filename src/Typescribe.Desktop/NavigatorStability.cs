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
/// Presents the Binder through one stable collection and coalesces incoming view changes to
/// approximately one display frame. StudioScriveningsFeatures remains the owner of collapse /
/// expand semantics; this class only prevents its array publications (and the base window's
/// transient BinderRows assignment) from tearing down ListBox containers between frames.
/// Bookmarks are not rebound here; only their selection is restored after their owner refreshes.
/// </summary>
internal sealed class NavigatorStability
{
    private static readonly TimeSpan BinderFrameInterval = TimeSpan.FromMilliseconds(1000d / 60d);

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly ObservableCollection<BinderRowViewModel> _frameRows = [];
    private readonly DispatcherTimer _binderFrameTimer;

    private ListBox? _binder;
    private TabControl? _leftTabs;
    private ListBox? _bookmarkList;
    private BinderRowViewModel[]? _pendingBinderRows;
    private string? _selectedPersistentId;
    private int _bookmarkSelectedIndex = -1;
    private int _bookmarkRestoreIndex = -1;
    private bool _restoringBinder;
    private bool _binderFrameScheduled;
    private bool _bookmarkRestorePending;
    private bool _discoverScheduled;
    private bool _disposed;

    private NavigatorStability(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
        _binderFrameTimer = new DispatcherTimer { Interval = BinderFrameInterval };
        _binderFrameTimer.Tick += BinderFrameTick;
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
        if (_binder is null || _leftTabs is null || _bookmarkList is null)
            ScheduleDiscover();
    }

    private void OnStateChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;

            CaptureLiveSelection();
            RestoreStableBinderSource();
            ScheduleBinderFrame();
            if (_bookmarkList is null) ScheduleDiscover();
        }, DispatcherPriority.Background);

    private void OnCanonicalBinderChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed) return;

        if (_selectedPersistentId is not null && !_viewModel.BinderRows.Any(row =>
                string.Equals(row.Node.PersistentId, _selectedPersistentId, StringComparison.Ordinal)))
            _selectedPersistentId = _viewModel.SelectedRow?.Node.PersistentId;

        // Do not publish BinderRows directly. The Scrivenings owner will publish the correct
        // collapsed/expanded projection; wait for that proposal and commit it on the next frame.
        ScheduleBinderFrame();
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
        _selectedPersistentId = (binder.SelectedItem as BinderRowViewModel)?.Node.PersistentId ??
                                _viewModel.SelectedRow?.Node.PersistentId;

        var initialRows = (binder.ItemsSource as IEnumerable<BinderRowViewModel>)?.ToArray() ??
                          _viewModel.BinderRows.ToArray();
        ReconcileBinderRows(initialRows);

        _restoringBinder = true;
        try { binder.ItemsSource = _frameRows; }
        finally { _restoringBinder = false; }

        binder.PropertyChanged += BinderPropertyChanged;
        binder.SelectionChanged += BinderSelectionChanged;
        SynchronizeBinderSelection();
    }

    private void BinderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_disposed || _restoringBinder || _binder is null || e.Property != ItemsControl.ItemsSourceProperty)
            return;

        var source = _binder.ItemsSource;
        if (ReferenceEquals(source, _frameRows)) return;

        CaptureLiveSelection();

        // The base window assigns BinderRows during every state refresh. That assignment is
        // transient and must never become the rendered source. Non-canonical enumerable sources
        // are the intentional collapse/expand projection published by Scrivenings.
        if (!ReferenceEquals(source, _viewModel.BinderRows) && source is IEnumerable<BinderRowViewModel> proposal)
            _pendingBinderRows = proposal.ToArray();

        // Restore the permanent source synchronously. Layout/render happens later, so Avalonia
        // never paints the intermediate array and the visible Binder does not flash.
        RestoreStableBinderSource();
        ScheduleBinderFrame();
    }

    private void BinderSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_disposed || _binder is null) return;
        if (_binder.SelectedItem is BinderRowViewModel selected)
        {
            _selectedPersistentId = selected.Node.PersistentId;
            return;
        }

        // Source churn can briefly clear selection inside the same UI operation. Recover on the
        // next frame rather than allowing that transient null to become navigation state.
        ScheduleBinderFrame();
    }

    private void CaptureLiveSelection()
    {
        if (_binder?.SelectedItem is BinderRowViewModel liveSelection)
            _selectedPersistentId = liveSelection.Node.PersistentId;
        else if (_viewModel.SelectedRow is { } selected)
            _selectedPersistentId = selected.Node.PersistentId;
        else if (_viewModel.BinderRows.Count == 0)
            _selectedPersistentId = null;
    }

    private void RestoreStableBinderSource()
    {
        if (_binder is null || ReferenceEquals(_binder.ItemsSource, _frameRows)) return;
        _restoringBinder = true;
        try { _binder.ItemsSource = _frameRows; }
        finally { _restoringBinder = false; }
    }

    private void ScheduleBinderFrame()
    {
        if (_disposed || _binderFrameScheduled) return;
        _binderFrameScheduled = true;
        _binderFrameTimer.Start();
    }

    private void BinderFrameTick(object? sender, EventArgs e)
    {
        _binderFrameTimer.Stop();
        _binderFrameScheduled = false;
        if (_disposed) return;

        if (_pendingBinderRows is { } pending)
        {
            _pendingBinderRows = null;
            ReconcileBinderRows(pending);
        }

        RestoreStableBinderSource();
        SynchronizeBinderSelection();
    }

    private void ReconcileBinderRows(IReadOnlyList<BinderRowViewModel> target)
    {
        // Incremental insert/move/remove operations preserve ListBox containers and scroll state.
        // Avoid Clear()/Reset: a reset is what produces the visible white flash on large Binders.
        for (var index = 0; index < target.Count; index++)
        {
            var desired = target[index];
            if (index < _frameRows.Count && SameNode(_frameRows[index], desired))
            {
                if (!ReferenceEquals(_frameRows[index], desired))
                    _frameRows[index] = desired;
                continue;
            }

            var existingIndex = FindRowIndex(desired.Node.PersistentId, index + 1);
            if (existingIndex >= 0)
            {
                _frameRows.Move(existingIndex, index);
                if (!ReferenceEquals(_frameRows[index], desired))
                    _frameRows[index] = desired;
            }
            else
            {
                _frameRows.Insert(index, desired);
            }
        }

        while (_frameRows.Count > target.Count)
            _frameRows.RemoveAt(_frameRows.Count - 1);
    }

    private int FindRowIndex(string persistentId, int startIndex)
    {
        for (var index = Math.Max(0, startIndex); index < _frameRows.Count; index++)
            if (string.Equals(_frameRows[index].Node.PersistentId, persistentId, StringComparison.Ordinal))
                return index;
        return -1;
    }

    private static bool SameNode(BinderRowViewModel left, BinderRowViewModel right)
        => string.Equals(left.Node.PersistentId, right.Node.PersistentId, StringComparison.Ordinal);

    private void SynchronizeBinderSelection()
    {
        if (_disposed || _binder is null) return;

        var targetId = _selectedPersistentId ?? _viewModel.SelectedRow?.Node.PersistentId;
        if (targetId is null) return;

        var target = _frameRows.FirstOrDefault(row =>
            string.Equals(row.Node.PersistentId, targetId, StringComparison.Ordinal));
        if (target is null) return;

        _selectedPersistentId = targetId;
        if (!ReferenceEquals(_binder.SelectedItem, target))
            _binder.SelectedItem = target;
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
        _bookmarkSelectedIndex = list.SelectedIndex;
        list.PropertyChanged += BookmarkPropertyChanged;
        list.SelectionChanged += BookmarkSelectionChanged;
    }

    private void BookmarkPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_disposed || _bookmarkList is null || e.Property != ItemsControl.ItemsSourceProperty) return;

        _bookmarkRestoreIndex = _bookmarkSelectedIndex >= 0
            ? _bookmarkSelectedIndex
            : _bookmarkList.SelectedIndex;
        if (_bookmarkRestoreIndex < 0) return;

        _bookmarkRestorePending = true;
        Dispatcher.UIThread.Post(RestoreBookmarkSelection, DispatcherPriority.Background);
    }

    private void BookmarkSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_bookmarkList is null) return;
        if (_bookmarkList.SelectedIndex >= 0)
            _bookmarkSelectedIndex = _bookmarkList.SelectedIndex;
    }

    private void RestoreBookmarkSelection()
    {
        if (_disposed || !_bookmarkRestorePending || _bookmarkList is null) return;
        _bookmarkRestorePending = false;
        if (_bookmarkList.ItemCount <= 0) return;

        if (_bookmarkList.SelectedIndex < 0 && _bookmarkRestoreIndex >= 0)
            _bookmarkList.SelectedIndex = Math.Clamp(_bookmarkRestoreIndex, 0, _bookmarkList.ItemCount - 1);
        if (_bookmarkList.SelectedIndex >= 0)
            _bookmarkSelectedIndex = _bookmarkList.SelectedIndex;
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
            foreach (var panelChild in panel.Children)
            {
                var found = FindListBox(panelChild);
                if (found is not null) return found;
            }
        }
        if (control is ContentControl content && content.Content is Control childContent)
            return FindListBox(childContent);
        if (control is Decorator decorator && decorator.Child is Control decoratedChild)
            return FindListBox(decoratedChild);
        return null;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _binderFrameTimer.Stop();
        _binderFrameTimer.Tick -= BinderFrameTick;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        _viewModel.StateChanged -= OnStateChanged;
        _viewModel.BinderRows.CollectionChanged -= OnCanonicalBinderChanged;
        if (_binder is not null)
        {
            _binder.PropertyChanged -= BinderPropertyChanged;
            _binder.SelectionChanged -= BinderSelectionChanged;
        }
        if (_bookmarkList is not null)
        {
            _bookmarkList.PropertyChanged -= BookmarkPropertyChanged;
            _bookmarkList.SelectionChanged -= BookmarkSelectionChanged;
        }
        if (_leftTabs is not null) _leftTabs.SelectionChanged -= LeftTabSelectionChanged;
    }
}
