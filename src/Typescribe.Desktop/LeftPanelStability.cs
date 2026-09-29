using System.Collections;
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
/// Other left-pane lists also keep their existing source when a new source has identical
/// semantic contents, avoiding unnecessary item-container recreation.
/// </summary>
internal sealed class LeftPanelStability
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly Dictionary<ListBox, SourceSnapshot> _listSnapshots = [];
    private readonly HashSet<ListBox> _restoringLists = [];

    private ListBox? _binder;
    private TabControl? _leftTabs;
    private IEnumerable? _preferredBinderSource;
    private bool _restoringBinder;
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
        if (_binder is not null && _leftTabs is not null)
            _window.LayoutUpdated -= OnLayoutUpdated;
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
                _leftTabs.SelectionChanged += LeftTabSelectionChanged;
        }

        AttachVisibleLeftLists();
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

    private void LeftTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => Dispatcher.UIThread.Post(AttachVisibleLeftLists, DispatcherPriority.Background);

    private void AttachVisibleLeftLists()
    {
        if (_disposed || _leftTabs is null) return;
        foreach (var list in _leftTabs.GetVisualDescendants().OfType<ListBox>())
        {
            if (ReferenceEquals(list, _binder) || _listSnapshots.ContainsKey(list)) continue;
            _listSnapshots[list] = Snapshot(list.ItemsSource);
            list.PropertyChanged += LeftListPropertyChanged;
        }
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

        // If the hierarchy did not change, this assignment came from an unrelated
        // StateChanged notification (typing, word count, autosave, PDF preview, etc.).
        // Restore the filtered/collapsed source synchronously, before the next render.
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

        // Favorites / Recent / Search / Collections sometimes receive a fresh array or
        // list containing the exact same logical rows. Keep the old source so Avalonia
        // can reuse the existing containers and preserve scroll / selection state.
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
    {
        // Structural changes must be allowed through. The Scrivenings layer publishes a
        // fresh filtered source after add/delete/move/rename/collapse processing.
        _preferredBinderSource = null;
    }

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

        if (_leftTabs is not null) _leftTabs.SelectionChanged -= LeftTabSelectionChanged;
        if (_binder is not null) _binder.PropertyChanged -= BinderPropertyChanged;
        foreach (var list in _listSnapshots.Keys)
            list.PropertyChanged -= LeftListPropertyChanged;

        _listSnapshots.Clear();
        _restoringLists.Clear();
        _binder = null;
        _leftTabs = null;
        _preferredBinderSource = null;
    }

    private sealed record SourceSnapshot(IEnumerable? Source, string Signature);
}
