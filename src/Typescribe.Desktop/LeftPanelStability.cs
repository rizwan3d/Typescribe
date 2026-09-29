using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop;

/// <summary>
/// Prevents ordinary workspace state notifications from visually rebuilding the Binder.
/// StudioScriveningsFeatures may temporarily own a filtered/collapsed Binder source; the
/// base window also publishes the full ObservableCollection. This guard keeps the active
/// source stable until the Binder hierarchy itself changes.
/// </summary>
internal sealed class LeftPanelStability
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private ListBox? _binder;
    private object? _preferredSource;
    private bool _restoring;
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
        if (_binder is null) TryAttach();
        else _window.LayoutUpdated -= OnLayoutUpdated;
    }

    private void TryAttach()
    {
        if (_disposed || _binder is not null) return;
        var binder = _window.GetVisualDescendants()
            .OfType<ListBox>()
            .FirstOrDefault(static list => list.ContextMenu is not null);
        if (binder is null) return;

        _binder = binder;
        if (!ReferenceEquals(binder.ItemsSource, _viewModel.BinderRows))
            _preferredSource = binder.ItemsSource;
        binder.PropertyChanged += BinderPropertyChanged;
    }

    private void BinderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_disposed || _restoring || _binder is null || e.Property != ItemsControl.ItemsSourceProperty)
            return;

        var source = _binder.ItemsSource;
        if (source is null) return;

        if (!ReferenceEquals(source, _viewModel.BinderRows))
        {
            _preferredSource = source;
            return;
        }

        // If the Binder hierarchy did not change, this is the base window rebinding the
        // full collection because of an unrelated StateChanged event (typing, preview,
        // autosave, word count, etc.). Restore the active filtered source synchronously,
        // before another render pass can occur.
        if (_preferredSource is null) return;

        var selected = _binder.SelectedItem;
        _restoring = true;
        try
        {
            _binder.ItemsSource = _preferredSource;
            if (selected is not null) _binder.SelectedItem = selected;
        }
        finally
        {
            _restoring = false;
        }
    }

    private void BinderRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Structural changes must be allowed through. The Scrivenings layer will publish
        // a new filtered source after add/delete/move/rename/collapse processing.
        _preferredSource = null;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        _viewModel.BinderRows.CollectionChanged -= BinderRowsChanged;
        if (_binder is not null) _binder.PropertyChanged -= BinderPropertyChanged;
        _binder = null;
        _preferredSource = null;
    }
}
