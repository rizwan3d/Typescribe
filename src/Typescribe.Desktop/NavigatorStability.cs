using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop;

/// <summary>
/// Coordinates the left navigator without becoming a second owner of its data.
/// StudioScriveningsFeatures owns the filtered Binder view and Bookmarks contents; the base
/// workspace still briefly rebinds the Binder to BinderRows during state refreshes. This
/// guard remembers the Scrivenings view, immediately restores it after those transient
/// rebinds, and keeps selection stable by persistent document id. Bookmarks are never
/// rebound here; only their selection is restored after their owner refreshes them.
/// </summary>
internal sealed class NavigatorStability
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;

    private ListBox? _binder;
    private TabControl? _leftTabs;
    private ListBox? _bookmarkList;
    private IEnumerable<BinderRowViewModel>? _preferredBinderSource;
    private string? _selectedPersistentId;
    private int _bookmarkSelectedIndex = -1;
    private int _bookmarkRestoreIndex = -1;
    private bool _restoringBinder;
    private bool _bookmarkRestorePending;
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
        if (_binder is null || _leftTabs is null || _bookmarkList is null)
            ScheduleDiscover();
    }

    private void OnStateChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;

            // Keep the user's live ListBox selection authoritative while an async SelectAsync
            // operation is completing. Once the ListBox has temporarily lost its selection,
            // fall back to the view-model's persistent id.
            if (_binder?.SelectedItem is BinderRowViewModel liveSelection)
                _selectedPersistentId = liveSelection.Node.PersistentId;
            else if (_viewModel.SelectedRow is { } selected)
                _selectedPersistentId = selected.Node.PersistentId;
            else if (_viewModel.BinderRows.Count == 0)
                _selectedPersistentId = null;

            RestorePreferredBinderSource();
            SynchronizeBinderSelection();
            if (_bookmarkList is null) ScheduleDiscover();
        }, DispatcherPriority.Background);

    private void OnCanonicalBinderChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed) return;

        if (_selectedPersistentId is not null && !_viewModel.BinderRows.Any(row =>
                string.Equals(row.Node.PersistentId, _selectedPersistentId, StringComparison.Ordinal)))
            _selectedPersistentId = _viewModel.SelectedRow?.Node.PersistentId;

        // Do not manufacture another Binder source here. The Scrivenings feature will publish
        // the correct filtered source after structural/collapse changes; preserving its source
        // object between updates avoids container teardown and visible flashing.
        Dispatcher.UIThread.Post(SynchronizeBinderSelection, DispatcherPriority.Background);
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
        _preferredBinderSource = binder.ItemsSource as IEnumerable<BinderRowViewModel> ?? _viewModel.BinderRows;
        _selectedPersistentId = (binder.SelectedItem as BinderRowViewModel)?.Node.PersistentId ??
                                _viewModel.SelectedRow?.Node.PersistentId;
        binder.PropertyChanged += BinderPropertyChanged;
        binder.SelectionChanged += BinderSelectionChanged;
        RestorePreferredBinderSource();
        SynchronizeBinderSelection();
    }

    private void BinderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_disposed || _restoringBinder || _binder is null || e.Property != ItemsControl.ItemsSourceProperty)
            return;

        var source = _binder.ItemsSource;
        if (ReferenceEquals(source, _preferredBinderSource)) return;

        if (ReferenceEquals(source, _viewModel.BinderRows))
        {
            // StudioWorkspaceWindow performs this assignment on every state refresh. It is not
            // an Expand All request and must not tear down the filtered Scrivenings view.
            if (_preferredBinderSource is not null && !ReferenceEquals(_preferredBinderSource, _viewModel.BinderRows))
                RestorePreferredBinderSource();
            else
                _preferredBinderSource = _viewModel.BinderRows;

            SynchronizeBinderSelection();
            return;
        }

        if (source is IEnumerable<BinderRowViewModel> proposal)
        {
            // A non-canonical source is the view intentionally published by
            // StudioScriveningsFeatures (collapse/expand/structure). Adopt that exact object so
            // the feature sees its own source on the next update and does not rebuild the Binder.
            _preferredBinderSource = proposal;
            SynchronizeBinderSelection();
            return;
        }

        RestorePreferredBinderSource();
        SynchronizeBinderSelection();
    }

    private void BinderSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_disposed || _binder is null) return;
        if (_binder.SelectedItem is BinderRowViewModel selected)
        {
            _selectedPersistentId = selected.Node.PersistentId;
            return;
        }

        // ItemsSource swaps can briefly clear selection. Do not let that transient null become
        // the user's navigation state; restore after the current UI operation completes.
        Dispatcher.UIThread.Post(SynchronizeBinderSelection, DispatcherPriority.Background);
    }

    private void RestorePreferredBinderSource()
    {
        if (_binder is null || _preferredBinderSource is null ||
            ReferenceEquals(_binder.ItemsSource, _preferredBinderSource))
            return;

        _restoringBinder = true;
        try { _binder.ItemsSource = _preferredBinderSource; }
        finally { _restoringBinder = false; }
    }

    private void SynchronizeBinderSelection()
    {
        if (_disposed || _binder is null) return;

        var targetId = (_binder.SelectedItem as BinderRowViewModel)?.Node.PersistentId ??
                       _selectedPersistentId ??
                       _viewModel.SelectedRow?.Node.PersistentId;
        if (targetId is null) return;

        var rows = (_binder.ItemsSource as IEnumerable<BinderRowViewModel>) ??
                   _preferredBinderSource ??
                   _viewModel.BinderRows;
        var target = rows.FirstOrDefault(row =>
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

        // The Bookmarks owner is allowed to replace its array. Rebinding it again here was the
        // source of the old double-refresh flicker. Preserve only the selection position.
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
