using System.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

/// <summary>
/// Keeps Project Explorer deletion consistent for real binder items and parsed document headings.
/// Binder nodes continue through WorkspaceViewModel/TransactionalProjectRepository so chapter,
/// heading, note, research, section, folder, part, and scene deletions use the project Trash.
/// Parsed outline headings are editor ranges rather than ProjectNode instances, so they are
/// snapshot-protected and removed from the owning document directly.
///
/// Delete target resolution intentionally uses WorkspaceViewModel state rather than reflection
/// against ProjectExplorerFeature's private ExplorerNode type. Typescribe ships as Native AOT,
/// where reflection metadata for that private implementation type can be trimmed.
/// </summary>
internal sealed class ProjectItemDeletionFeature
{
    private const string DeleteCommandClass = "project-delete-coordinator";

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;

    private TreeView? _tree;
    private ContextMenu? _menu;
    private bool _scanQueued;
    private bool _disposed;

    private ProjectItemDeletionFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var feature = new ProjectItemDeletionFeature(window, viewModel);
        window.Opened += feature.WindowOpened;
        window.LayoutUpdated += feature.WindowLayoutUpdated;
        window.Closed += feature.WindowClosed;
        viewModel.StateChanged += feature.ViewModelStateChanged;
        feature.QueueScan();
    }

    private void WindowOpened(object? sender, EventArgs e) => QueueScan();

    private void WindowLayoutUpdated(object? sender, EventArgs e) => QueueScan();

    private void ViewModelStateChanged(object? sender, EventArgs e) => QueueScan();

    private void QueueScan()
    {
        if (_disposed || _scanQueued) return;
        _scanQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scanQueued = false;
            if (!_disposed) Scan();
        }, DispatcherPriority.Background);
    }

    private void Scan()
    {
        var tree = _window.GetVisualDescendants()
            .OfType<TreeView>()
            .FirstOrDefault(static candidate => candidate.Classes.Contains("project-explorer-tree"));
        if (tree is null) return;

        if (!ReferenceEquals(_tree, tree))
        {
            if (_tree is not null)
                _tree.RemoveHandler(InputElement.KeyDownEvent, TreeKeyDown);

            _tree = tree;
            _menu = null;
            _tree.AddHandler(
                InputElement.KeyDownEvent,
                TreeKeyDown,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
        }

        InstallDeleteCommand();
    }

    private void InstallDeleteCommand()
    {
        if (_tree?.ContextMenu is not { } menu) return;

        var items = MenuItems(menu.ItemsSource);
        if (ReferenceEquals(_menu, menu) &&
            items.OfType<MenuItem>().Any(static item => item.Classes.Contains(DeleteCommandClass)))
            return;

        items.RemoveAll(static item => item is MenuItem menuItem && menuItem.Classes.Contains(DeleteCommandClass));

        var existingDeleteIndex = items.FindIndex(static item =>
            item is MenuItem menuItem && HeaderEquals(menuItem, "Delete"));
        if (existingDeleteIndex >= 0 && items[existingDeleteIndex] is MenuItem existingDelete)
            existingDelete.IsVisible = false;

        var delete = new MenuItem { Header = "Delete…" };
        delete.Classes.Add(DeleteCommandClass);
        delete.Click += async (_, _) => await DeleteSelectedSafelyAsync();

        var insertAt = existingDeleteIndex >= 0
            ? existingDeleteIndex + 1
            : Math.Max(0, items.FindIndex(static item => item is Separator));
        if (insertAt < 0) insertAt = items.Count;
        items.Insert(insertAt, delete);

        menu.ItemsSource = items.ToArray();
        _menu = menu;
    }

    private async void TreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || !HasDeletableSelection()) return;

        e.Handled = true;
        await DeleteSelectedSafelyAsync();
    }

    private async Task DeleteSelectedSafelyAsync()
    {
        if (_disposed || !HasDeletableSelection()) return;

        try
        {
            // ProjectExplorerFeature synchronizes TreeView selection into WorkspaceViewModel.
            // Use that strongly typed state so Native AOT trimming cannot break delete target lookup.
            var selectedRow = _viewModel.SelectedRow;
            var selectedOutline = _viewModel.SelectedOutline;

            if (selectedOutline is not null && selectedRow?.Node.IsDocument == true)
                await DeleteOutlineHeadingAsync(selectedRow, selectedOutline);
            else if (selectedRow is not null && selectedRow.Node.Kind != NodeKind.Book)
                await DeleteProjectNodeAsync(selectedRow);
        }
        catch (Exception ex)
        {
            _ = await DesktopDialogService.ConfirmAsync(
                _window,
                "Delete failed",
                ex.Message,
                "Close");
        }
    }

    private async Task DeleteProjectNodeAsync(BinderRowViewModel selectedRow)
    {
        if (selectedRow.Node.Kind == NodeKind.Book) return;

        var row = _viewModel.BinderRows.FirstOrDefault(candidate =>
            string.Equals(candidate.Node.PersistentId, selectedRow.Node.PersistentId, StringComparison.Ordinal));
        if (row is null) return;

        var confirmed = await DesktopDialogService.ConfirmAsync(
            _window,
            "Delete Project Item",
            $"Delete '{row.Node.Title}' and its on-disk content? It will be moved to Typescribe Trash.",
            "Delete");
        if (!confirmed) return;

        if (!string.Equals(
                _viewModel.SelectedRow?.Node.PersistentId,
                row.Node.PersistentId,
                StringComparison.Ordinal))
            await _viewModel.SelectAsync(row);

        await _viewModel.DeleteSelectedAsync();
    }

    private async Task DeleteOutlineHeadingAsync(
        BinderRowViewModel selectedOwnerRow,
        OutlineItemViewModel selectedHeading)
    {
        var ownerRow = _viewModel.BinderRows.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Node.PersistentId,
                selectedOwnerRow.Node.PersistentId,
                StringComparison.Ordinal));
        if (ownerRow is null || !ownerRow.Node.IsDocument) return;

        var confirmed = await DesktopDialogService.ConfirmAsync(
            _window,
            "Delete Heading Section",
            $"Delete heading '{selectedHeading.Title}' and all content in its section? A snapshot of the document will be created first.",
            "Delete");
        if (!confirmed) return;

        if (!string.Equals(
                _viewModel.SelectedRow?.Node.PersistentId,
                ownerRow.Node.PersistentId,
                StringComparison.Ordinal))
            await _viewModel.SelectAsync(ownerRow);

        var liveHeading = _viewModel.OutlineItems.FirstOrDefault(candidate =>
            candidate.Level == selectedHeading.Level &&
            candidate.SourceLine == selectedHeading.SourceLine &&
            string.Equals(candidate.Title, selectedHeading.Title, StringComparison.Ordinal))
            ?? _viewModel.OutlineItems.FirstOrDefault(candidate =>
                candidate.Level == selectedHeading.Level &&
                string.Equals(candidate.Title, selectedHeading.Title, StringComparison.Ordinal));
        if (liveHeading is null) return;

        await _viewModel.CreateSnapshotAsync($"Before deleting heading: {liveHeading.Title}");

        var updated = RemoveLineRange(
            _viewModel.EditorText,
            liveHeading.SourceLine,
            liveHeading.EndLine);
        if (string.Equals(updated, _viewModel.EditorText, StringComparison.Ordinal)) return;

        _viewModel.UpdateEditorText(updated);
        await _viewModel.SaveNowAsync();
    }

    private bool HasDeletableSelection()
    {
        if (_tree?.SelectedItem is null || IsProjectRootSelected()) return false;

        if (_viewModel.SelectedOutline is not null && _viewModel.SelectedRow?.Node.IsDocument == true)
            return true;

        return _viewModel.SelectedRow is { } row && row.Node.Kind != NodeKind.Book;
    }

    private bool IsProjectRootSelected()
    {
        if (_tree?.SelectedItem is not { } selected || _tree.ItemsSource is not IEnumerable source)
            return false;

        var root = source.Cast<object?>().FirstOrDefault(static item => item is not null);
        return ReferenceEquals(selected, root);
    }

    private static string RemoveLineRange(string text, int startLine, int endLine)
    {
        if (string.IsNullOrEmpty(text)) return text;

        startLine = Math.Max(1, startLine);
        endLine = Math.Max(startLine, endLine);

        var start = LineStartIndex(text, startLine);
        if (start < 0 || start >= text.Length) return text;

        var after = LineStartIndex(text, endLine + 1);
        if (after < 0) after = text.Length;
        return text.Remove(start, Math.Max(0, after - start));
    }

    private static int LineStartIndex(string text, int line)
    {
        if (line <= 1) return 0;

        var currentLine = 1;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n') continue;
            currentLine++;
            if (currentLine == line) return index + 1;
        }

        return -1;
    }

    private static List<object> MenuItems(object? source)
    {
        if (source is not IEnumerable enumerable) return [];
        return enumerable.Cast<object?>().Where(static item => item is not null).Cast<object>().ToList();
    }

    private static bool HeaderEquals(MenuItem item, string text)
        => string.Equals(
            (item.Header?.ToString() ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal).TrimEnd('…'),
            text,
            StringComparison.OrdinalIgnoreCase);

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        if (_tree is not null)
            _tree.RemoveHandler(InputElement.KeyDownEvent, TreeKeyDown);
        _window.Opened -= WindowOpened;
        _window.LayoutUpdated -= WindowLayoutUpdated;
        _window.Closed -= WindowClosed;
        _viewModel.StateChanged -= ViewModelStateChanged;
    }
}
