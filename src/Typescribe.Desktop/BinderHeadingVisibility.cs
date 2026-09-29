using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop;

/// <summary>
/// Ensures the selected manuscript document exposes its Markdown heading tree once the
/// outline and recycled Binder row are both ready. ScrivenerBinderEnhancements remains the
/// owner of heading rendering and persistence; this class only closes the timing gap where
/// a document could be selected before its H1-H6 index or ListBoxItem existed.
/// </summary>
internal sealed class BinderHeadingVisibility
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;

    private ListBox? _binder;
    private string? _lastSelectedDocumentId;
    private string? _pendingDocumentId;
    private bool _applyScheduled;
    private bool _disposed;

    private BinderHeadingVisibility(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var host = new BinderHeadingVisibility(window, viewModel);
        window.Opened += host.OnOpened;
        window.LayoutUpdated += host.OnLayoutUpdated;
        window.Closed += host.OnClosed;
        viewModel.StateChanged += host.OnStateChanged;
        host.CaptureSelection();
        host.ScheduleApply();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        CaptureSelection();
        ScheduleApply();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_pendingDocumentId is not null || _binder is null)
            ScheduleApply();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            CaptureSelection();
            ScheduleApply();
        }, DispatcherPriority.Background);
    }

    private void CaptureSelection()
    {
        var selected = _viewModel.SelectedRow;
        var id = selected?.Node.IsDocument == true ? selected.Node.PersistentId : null;
        if (string.Equals(id, _lastSelectedDocumentId, StringComparison.Ordinal)) return;

        _lastSelectedDocumentId = id;
        _pendingDocumentId = id;
    }

    private void ScheduleApply()
    {
        if (_disposed || _applyScheduled) return;
        _applyScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _applyScheduled = false;
            if (!_disposed) TryRevealSelectedHeadings();
        }, DispatcherPriority.Background);
    }

    private void TryRevealSelectedHeadings()
    {
        _binder ??= _window.GetVisualDescendants()
            .OfType<ListBox>()
            .FirstOrDefault(static list => list.ContextMenu is not null);

        var id = _pendingDocumentId;
        if (_binder is null || id is null) return;
        if (_viewModel.SelectedRow?.Node.IsDocument != true ||
            !string.Equals(_viewModel.SelectedRow.Node.PersistentId, id, StringComparison.Ordinal))
        {
            _pendingDocumentId = null;
            return;
        }

        // OutlineItems is the live semantic H1-H6 list. If it is still empty, keep the request
        // pending; the view model will raise StateChanged again when parsing completes.
        if (_viewModel.OutlineItems.Count == 0) return;

        var item = _binder.GetVisualDescendants()
            .OfType<ListBoxItem>()
            .FirstOrDefault(candidate => candidate.Content is BinderRowViewModel row &&
                string.Equals(row.Node.PersistentId, id, StringComparison.Ordinal));
        if (item is null)
        {
            _binder.ScrollIntoView(_viewModel.SelectedRow);
            return;
        }

        var toggle = item.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(static button => button.Classes.Contains("heading-toggle-wired"));
        var group = item.GetVisualDescendants()
            .OfType<StackPanel>()
            .FirstOrDefault(static panel => panel.Classes.Contains("binder-heading-group"));
        if (toggle is null || group is null) return;

        if (group.IsVisible && group.Children.Count > 0)
        {
            _pendingDocumentId = null;
            return;
        }

        var state = toggle.Content?.ToString() ?? string.Empty;
        if (string.Equals(state, "▸", StringComparison.Ordinal))
        {
            RaiseClick(toggle);
        }
        else if (string.Equals(state, "▾", StringComparison.Ordinal) && group.Children.Count == 0)
        {
            // The row says it is expanded but its recycled visual was created before the
            // heading cache arrived. Cycling the existing owner-controlled toggle rebuilds the
            // children without introducing another Binder data source.
            RaiseClick(toggle);
            RaiseClick(toggle);
        }

        if (group.IsVisible && group.Children.Count > 0)
            _pendingDocumentId = null;
    }

    private static void RaiseClick(Button button)
        => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        _viewModel.StateChanged -= OnStateChanged;
    }
}
