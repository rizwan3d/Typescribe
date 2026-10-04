using System.Collections;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.Editing;
using Typescribe.Desktop.ViewModels;
using ToolTip = Avalonia.Controls.ToolTip;

namespace Typescribe.Desktop;

/// <summary>
/// Keeps bookmark creation/navigation clear, adds durable bookmark renaming, and
/// preserves StudioScriveningsFeatures' existing bookmark storage and navigation data.
/// </summary>
internal sealed class BookmarkNavigationPolish
{
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly Dictionary<string, string> _renamedLabels = new(StringComparer.Ordinal);

    private TabItem? _bookmarksTab;
    private ListBox? _bookmarkList;
    private Button? _addButton;
    private Button? _editButton;
    private Button? _removeButton;
    private TextBlock? _emptyState;
    private string? _loadedRenameRoot;
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

        _editButton = buttons.Children.OfType<Button>()
            .FirstOrDefault(button => string.Equals(button.Content?.ToString(), "Rename", StringComparison.Ordinal));
        if (_editButton is null)
        {
            _editButton = new Button
            {
                Content = "Rename",
                MinWidth = 78,
                Margin = new Thickness(6, 0, 0, 0)
            };
            var removeIndex = buttons.Children.IndexOf(_removeButton);
            buttons.Children.Insert(removeIndex >= 0 ? removeIndex : buttons.Children.Count, _editButton);
        }

        buttons.Margin = new Thickness(8, 8, 8, 6);
        _addButton.MinWidth = 112;
        _removeButton.MinWidth = 74;
        _bookmarkList.Margin = new Thickness(8, 0, 8, 8);
        _bookmarkList.SelectionChanged += BookmarkSelectionChanged;
        _editButton.Click += EditButtonClicked;

        ToolTip.SetTip(_addButton, "Add a bookmark at the current editor caret and enter a bookmark name");
        ToolTip.SetTip(_editButton, "Rename the selected bookmark");
        ToolTip.SetTip(_removeButton, "Remove the selected bookmark");
        ToolTip.SetTip(_bookmarkList, "Select a bookmark to jump to its saved line");

        _emptyState = new TextBlock
        {
            Text = "No bookmarks yet.\nPlace the editor caret on a line, choose Add Bookmark, then enter a name.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
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

    private async void EditButtonClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await EditSelectedBookmarkAsync();
    }

    private async Task EditSelectedBookmarkAsync()
    {
        if (_bookmarkList?.SelectedItem is not { } bookmark) return;
        if (!TryReadBookmark(bookmark, out var id, out _, out _, out var currentLabel)) return;

        EnsureRenameMapLoaded();
        if (_renamedLabels.TryGetValue(id, out var renamed) && !string.IsNullOrWhiteSpace(renamed))
            currentLabel = renamed;

        var updated = await DesktopDialogService.PromptAsync(
            _window,
            "Edit Bookmark",
            "Bookmark name",
            currentLabel);
        if (string.IsNullOrWhiteSpace(updated)) return;

        _renamedLabels[id] = updated.Trim();
        SaveRenameMap();
        PatchWorkspaceBookmarkName(id, updated.Trim());
        ApplyRenamedLabels(refreshList: true);
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
        if (!TryReadBookmark(bookmark, out var persistentId, out var line, out var column, out _, persistentIdMode: true)) return;

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

    private static bool TryReadBookmark(
        object bookmark,
        out string idOrPersistentId,
        out int line,
        out int column,
        out string label,
        bool persistentIdMode = false)
    {
        var type = bookmark.GetType();
        idOrPersistentId = type.GetProperty(persistentIdMode ? "PersistentId" : "Id", InstanceFlags)?.GetValue(bookmark) as string ?? string.Empty;
        line = ReadInt(type.GetProperty("Line", InstanceFlags)?.GetValue(bookmark), 1);
        column = ReadInt(type.GetProperty("Column", InstanceFlags)?.GetValue(bookmark), 1);
        label = type.GetProperty("Label", InstanceFlags)?.GetValue(bookmark) as string ?? string.Empty;
        return !string.IsNullOrWhiteSpace(idOrPersistentId);
    }

    private static int ReadInt(object? value, int fallback)
        => value is int number && number > 0 ? number : fallback;

    private void EnsureRenameMapLoaded()
    {
        var root = CurrentProjectRoot();
        if (string.IsNullOrWhiteSpace(root) || string.Equals(root, _loadedRenameRoot, StringComparison.Ordinal)) return;

        _loadedRenameRoot = root;
        _renamedLabels.Clear();
        var path = RenameMapPath(root);
        if (File.Exists(path))
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split('\t');
                if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0])) continue;
                try
                {
                    var name = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
                    if (!string.IsNullOrWhiteSpace(name)) _renamedLabels[parts[0]] = name;
                }
                catch
                {
                }
            }
        }

        PatchAllWorkspaceBookmarkNames();
    }

    private void ApplyRenamedLabels(bool refreshList = false)
    {
        if (_bookmarkList?.ItemsSource is not IEnumerable source || _renamedLabels.Count == 0) return;
        var items = source.Cast<object>().ToArray();
        var changed = false;

        foreach (var item in items)
        {
            var type = item.GetType();
            var id = type.GetProperty("Id", InstanceFlags)?.GetValue(item) as string;
            if (string.IsNullOrWhiteSpace(id) || !_renamedLabels.TryGetValue(id, out var name)) continue;

            var labelProperty = type.GetProperty("Label", InstanceFlags);
            var displayProperty = type.GetProperty("DisplayTitle", InstanceFlags);
            var previousDisplay = displayProperty?.GetValue(item) as string;
            try { labelProperty?.SetValue(item, name); } catch { }

            if (displayProperty is not null)
            {
                var prefix = previousDisplay;
                if (!string.IsNullOrWhiteSpace(prefix))
                {
                    var separator = prefix.IndexOf("  •  ", StringComparison.Ordinal);
                    if (separator >= 0) prefix = prefix[..separator];
                }
                try { displayProperty.SetValue(item, string.IsNullOrWhiteSpace(prefix) ? name : $"{prefix}  •  {name}"); } catch { }
            }
            changed = true;
        }

        if (changed && refreshList)
        {
            var selected = _bookmarkList.SelectedItem;
            _bookmarkList.ItemsSource = null;
            _bookmarkList.ItemsSource = items;
            _bookmarkList.SelectedItem = selected;
        }
    }

    private void SaveRenameMap()
    {
        var root = CurrentProjectRoot();
        if (string.IsNullOrWhiteSpace(root)) return;
        var path = RenameMapPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = _renamedLabels
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}\t{Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Value))}")
            .ToArray();
        File.WriteAllLines(path, lines);
    }

    private void PatchAllWorkspaceBookmarkNames()
    {
        foreach (var pair in _renamedLabels)
            PatchWorkspaceBookmarkName(pair.Key, pair.Value);
    }

    private void PatchWorkspaceBookmarkName(string bookmarkId, string name)
    {
        var root = CurrentProjectRoot();
        if (string.IsNullOrWhiteSpace(root)) return;
        var path = Path.Combine(root, ".typescribe", "workspace.tsv");
        if (!File.Exists(path)) return;

        var lines = File.ReadAllLines(path);
        var changed = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var parts = lines[index].Split('\t');
            if (parts.Length < 9 || !string.Equals(parts[0], "bookmark", StringComparison.Ordinal) ||
                !string.Equals(parts[1], bookmarkId, StringComparison.Ordinal)) continue;
            parts[6] = Convert.ToBase64String(Encoding.UTF8.GetBytes(name));
            lines[index] = string.Join('\t', parts);
            changed = true;
        }

        if (changed) File.WriteAllLines(path, lines);
    }

    private string? CurrentProjectRoot()
    {
        var projectField = typeof(WorkspaceViewModel).GetField("_project", BindingFlags.Instance | BindingFlags.NonPublic);
        var project = projectField?.GetValue(_viewModel);
        return project?.GetType().GetProperty("RootPath", InstanceFlags)?.GetValue(project) as string;
    }

    private static string RenameMapPath(string root)
        => Path.Combine(root, ".typescribe", "bookmark-names.tsv");

    private void UpdateState()
    {
        if (_disposed || !_wired) return;

        EnsureRenameMapLoaded();
        ApplyRenamedLabels();

        var canAdd = _viewModel.SelectedRow?.Node.IsDocument == true;
        if (_addButton is not null)
        {
            _addButton.IsEnabled = canAdd;
            ToolTip.SetTip(_addButton, canAdd
                ? "Add a bookmark at the current editor caret and enter a bookmark name"
                : "Select a document before adding a bookmark");
        }

        var hasSelection = _bookmarkList?.SelectedItem is not null;
        if (_editButton is not null) _editButton.IsEnabled = hasSelection;
        if (_removeButton is not null) _removeButton.IsEnabled = hasSelection;

        if (_emptyState is not null && _bookmarkList is not null)
            _emptyState.IsVisible = _bookmarkList.ItemCount == 0;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // StudioScriveningsFeatures is registered before this feature, so its final state save
        // runs first. Reapply durable bookmark names after that save to keep workspace.tsv aligned.
        try { PatchAllWorkspaceBookmarkNames(); } catch { }

        _disposed = true;
        if (_bookmarkList is not null)
            _bookmarkList.SelectionChanged -= BookmarkSelectionChanged;
        if (_editButton is not null)
            _editButton.Click -= EditButtonClicked;
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
