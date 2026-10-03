using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Makes the editor document title directly editable, adds H2/H3 creation commands,
/// and provides AOT-safe inline naming for Project Explorer items. Parsed document
/// headings remain intentionally read-only.
/// </summary>
internal sealed class EditorTitleAndExplorerHeadingFeature
{
    private const string HeadingMenuClass = "project-heading-create";
    private const string HeadingButtonClass = "project-heading-create-button";
    private const string NameMenuClass = "project-name-edit";
    private const string NameButtonClass = "project-name-edit-button";

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly TrackingProjectRepository _repository;

    private TextBox? _titleEditor;
    private TreeView? _tree;
    private Button? _renameButton;
    private Button? _h2Button;
    private Button? _h3Button;

    private TextBox? _explorerNameEditor;
    private TextBlock? _explorerNameLabel;
    private Grid? _explorerNameHost;
    private string? _editingPersistentId;
    private string _editingOriginalName = string.Empty;
    private bool _editingBookRoot;

    private bool _scanQueued;
    private bool _syncingTitle;
    private bool _committingExplorerName;
    private bool _treeHandlersInstalled;
    private bool _disposed;

    private EditorTitleAndExplorerHeadingFeature(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository)
    {
        _window = window;
        _viewModel = viewModel;
        _repository = repository;
    }

    public static void Apply(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(repository);

        var feature = new EditorTitleAndExplorerHeadingFeature(window, viewModel, repository);
        window.Opened += feature.WindowOpened;
        window.LayoutUpdated += feature.WindowLayoutUpdated;
        window.Closed += feature.WindowClosed;
        viewModel.StateChanged += feature.ViewModelStateChanged;
        feature.QueueScan();
    }

    private void WindowOpened(object? sender, EventArgs e) => QueueScan();

    private void WindowLayoutUpdated(object? sender, EventArgs e)
    {
        if (_titleEditor is null || _tree is null) QueueScan();
    }

    private void ViewModelStateChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        Dispatcher.UIThread.Post(() =>
        {
            SyncTitleFromSelection();
            UpdateExplorerActionState();
            QueueScan();
        }, DispatcherPriority.Background);
    }

    private void QueueScan()
    {
        if (_disposed || _scanQueued) return;
        _scanQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scanQueued = false;
            if (_disposed) return;
            InstallEditableTitle();
            InstallExplorerActions();
            SyncTitleFromSelection();
            UpdateExplorerActionState();
        }, DispatcherPriority.Background);
    }

    private void InstallEditableTitle()
    {
        if (_titleEditor is not null) return;

        var centerTabs = _window.GetVisualDescendants()
            .OfType<TabControl>()
            .FirstOrDefault(static tabs =>
            {
                var items = TabItems(tabs).ToArray();
                return items.Any(item => HeaderEquals(item, "Editor")) &&
                       items.Any(item => HeaderEquals(item, "Corkboard"));
            });
        if (centerTabs is null) return;

        var editorTab = TabItems(centerTabs).FirstOrDefault(item => HeaderEquals(item, "Editor"));
        if (editorTab?.Content is not Grid editorPanel) return;

        var header = editorPanel.Children
            .OfType<Grid>()
            .FirstOrDefault(static grid => Grid.GetRow(grid) == 0 &&
                grid.Children.OfType<TextBlock>().Any(block => Grid.GetColumn(block) == 0));
        if (header is null) return;

        var oldTitle = header.Children
            .OfType<TextBlock>()
            .FirstOrDefault(static block => Grid.GetColumn(block) == 0 && block.FontSize >= 16);
        if (oldTitle is null) return;

        var titleEditor = new TextBox
        {
            Name = "EditableDocumentTitle",
            FontSize = oldTitle.FontSize,
            FontWeight = FontWeight.SemiBold,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 2),
            MinHeight = 30,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Watermark = "Document title"
        };
        ToolTip.SetTip(titleEditor, "Edit document title. Press Enter to save; Escape to cancel.");

        titleEditor.KeyDown += TitleEditorKeyDown;
        titleEditor.LostFocus += async (_, _) => await CommitTitleAsync();

        header.Children.Remove(oldTitle);
        Grid.SetColumn(titleEditor, 0);
        header.Children.Add(titleEditor);
        _titleEditor = titleEditor;
    }

    private async void TitleEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (_titleEditor is null) return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitTitleAsync();
            FocusManuscriptEditor();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            SyncTitleFromSelection(force: true);
            FocusManuscriptEditor();
        }
    }

    private async Task CommitTitleAsync()
    {
        if (_titleEditor is null || _syncingTitle || !_viewModel.HasDocument) return;

        var proposed = _titleEditor.Text?.Trim();
        if (string.IsNullOrWhiteSpace(proposed))
        {
            SyncTitleFromSelection(force: true);
            return;
        }

        if (string.Equals(proposed, _viewModel.SelectedTitle, StringComparison.Ordinal)) return;

        _syncingTitle = true;
        try
        {
            await _viewModel.RenameSelectedAsync(proposed);
            ToolTip.SetTip(_titleEditor, "Document title saved. Press Enter to save future changes.");
        }
        catch (Exception ex)
        {
            ToolTip.SetTip(_titleEditor, $"Could not rename document: {ex.Message}");
        }
        finally
        {
            _syncingTitle = false;
            SyncTitleFromSelection(force: true);
        }
    }

    private void SyncTitleFromSelection(bool force = false)
    {
        if (_titleEditor is null || _syncingTitle) return;

        _titleEditor.IsEnabled = _viewModel.HasDocument;
        _titleEditor.Watermark = _viewModel.HasSelection ? "Document title" : "No document selected";

        if (!force && _titleEditor.IsFocused) return;

        var title = _viewModel.HasSelection ? _viewModel.SelectedTitle : string.Empty;
        if (!string.Equals(_titleEditor.Text, title, StringComparison.Ordinal))
            _titleEditor.Text = title;
    }

    private void FocusManuscriptEditor()
    {
        _window.GetVisualDescendants()
            .OfType<ManuscriptEditor>()
            .FirstOrDefault(candidate => candidate.DocumentIdentity is not null)
            ?.Focus();
    }

    private void InstallExplorerActions()
    {
        var tree = _window.GetVisualDescendants()
            .OfType<TreeView>()
            .FirstOrDefault(static candidate => candidate.Classes.Contains("project-explorer-tree"));
        if (tree is null) return;

        _tree = tree;
        if (!_treeHandlersInstalled)
        {
            tree.SelectionChanged += ExplorerSelectionChanged;
            tree.DoubleTapped += ExplorerDoubleTapped;
            _treeHandlersInstalled = true;
        }

        InstallExplorerToolbarButtons(tree);
        InstallExplorerContextMenu(tree);
    }

    private void InstallExplorerToolbarButtons(TreeView tree)
    {
        var surface = tree.GetVisualAncestors()
            .OfType<Grid>()
            .FirstOrDefault(static grid => grid.Classes.Contains("project-explorer-surface"));
        if (surface is null) return;

        var header = surface.Children
            .OfType<Grid>()
            .FirstOrDefault(static grid => grid.Classes.Contains("project-explorer-toolbar"));
        if (header is null) return;

        _renameButton ??= header.Children.OfType<Button>()
            .FirstOrDefault(static button => button.Classes.Contains(NameButtonClass));
        var existingHeadingButtons = header.Children
            .OfType<Button>()
            .Where(static button => button.Classes.Contains(HeadingButtonClass))
            .ToArray();
        _h2Button ??= existingHeadingButtons.FirstOrDefault(button => string.Equals(button.Content?.ToString(), "H2", StringComparison.Ordinal));
        _h3Button ??= existingHeadingButtons.FirstOrDefault(button => string.Equals(button.Content?.ToString(), "H3", StringComparison.Ordinal));

        if (_renameButton is not null && _h2Button is not null && _h3Button is not null) return;
        if (_renameButton is not null || _h2Button is not null || _h3Button is not null) return;

        foreach (var child in header.Children.OfType<Control>().ToArray())
        {
            var column = Grid.GetColumn(child);
            if (column >= 1) Grid.SetColumn(child, column + 3);
        }
        header.ColumnDefinitions.Insert(1, new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Insert(2, new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Insert(3, new ColumnDefinition { Width = GridLength.Auto });

        _renameButton = NameButton();
        _h2Button = HeadingButton("H2", "Add a level-2 heading to the active document", 2);
        _h3Button = HeadingButton("H3", "Add a level-3 heading to the active document", 3);
        Grid.SetColumn(_renameButton, 1);
        Grid.SetColumn(_h2Button, 2);
        Grid.SetColumn(_h3Button, 3);
        header.Children.Add(_renameButton);
        header.Children.Add(_h2Button);
        header.Children.Add(_h3Button);
    }

    private Button NameButton()
    {
        var button = new Button
        {
            Content = "✎",
            Width = 28,
            Height = 24,
            MinWidth = 28,
            MinHeight = 24,
            Padding = new Thickness(2, 0),
            Margin = new Thickness(2, 0),
            FontSize = 11,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        button.Classes.Add(NameButtonClass);
        ToolTip.SetTip(button, "Edit selected book or project item name. Outline headings are read-only.");
        button.Click += async (_, _) => await BeginInlineRenameSelectedAsync();
        return button;
    }

    private Button HeadingButton(string text, string toolTip, int level)
    {
        var button = new Button
        {
            Content = text,
            Width = 30,
            Height = 24,
            MinWidth = 30,
            MinHeight = 24,
            Padding = new Thickness(3, 0),
            Margin = new Thickness(2, 0),
            FontSize = 10.5,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        button.Classes.Add(HeadingButtonClass);
        ToolTip.SetTip(button, toolTip);
        button.Click += async (_, _) => await AddHeadingAsync(level);
        return button;
    }

    private void InstallExplorerContextMenu(TreeView tree)
    {
        if (tree.ContextMenu is not { } menu || menu.ItemsSource is not IEnumerable source) return;

        var items = source.Cast<object?>().Where(static item => item is not null).Cast<object>().ToList();
        var firstSeparator = items.FindIndex(static item => item is Separator);
        var insertAt = firstSeparator >= 0 ? firstSeparator : items.Count;

        if (!items.OfType<MenuItem>().Any(static item => item.Classes.Contains(HeadingMenuClass)))
        {
            items.Insert(insertAt, HeadingMenuItem("Add H2…", 2));
            items.Insert(insertAt + 1, HeadingMenuItem("Add H3…", 3));
            insertAt += 2;
        }

        if (!items.OfType<MenuItem>().Any(static item => item.Classes.Contains(NameMenuClass)))
        {
            var editName = new MenuItem { Header = "Edit Name Inline" };
            editName.Classes.Add(NameMenuClass);
            editName.Click += async (_, _) => await BeginInlineRenameSelectedAsync();
            items.Insert(insertAt, editName);
        }

        menu.ItemsSource = items.ToArray();
    }

    private MenuItem HeadingMenuItem(string header, int level)
    {
        var item = new MenuItem { Header = header };
        item.Classes.Add(HeadingMenuClass);
        item.Click += async (_, _) => await AddHeadingAsync(level);
        return item;
    }

    private async void ExplorerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_explorerNameEditor is not null && !_committingExplorerName)
            await CommitExplorerNameAsync(cancel: false);
        UpdateExplorerActionState();
    }

    private async void ExplorerDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is TextBox || _tree is null) return;
        var item = FindTreeViewItem(e.Source);
        if (item is null || IsHeadingItem(item)) return;
        e.Handled = true;
        await BeginInlineRenameAsync(item);
    }

    private async Task BeginInlineRenameSelectedAsync()
    {
        var item = SelectedTreeItem();
        if (item is null || IsHeadingItem(item)) return;
        await BeginInlineRenameAsync(item);
    }

    private async Task BeginInlineRenameAsync(TreeViewItem item)
    {
        if (_tree is null || IsHeadingItem(item)) return;
        if (_explorerNameEditor is not null)
            await CommitExplorerNameAsync(cancel: false);

        if (!item.IsSelected && item.DataContext is not null)
            _tree.SelectedItem = item.DataContext;

        var host = FindExplorerHeader(item);
        var label = host?.Children.OfType<TextBlock>()
            .FirstOrDefault(static block => Grid.GetColumn(block) == 2);
        if (host is null || label is null) return;

        var isBookRoot = IsBookRootItem(item);
        string? persistentId = null;
        if (!isBookRoot)
        {
            await WaitForProjectSelectionAsync(label.Text ?? string.Empty);
            persistentId = _viewModel.SelectedRow?.Node.PersistentId;
            if (string.IsNullOrWhiteSpace(persistentId) || _viewModel.SelectedOutline is not null) return;
        }

        var editor = new TextBox
        {
            Text = label.Text,
            FontSize = label.FontSize,
            FontWeight = label.FontWeight,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(3, 0),
            MinHeight = 23,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(editor, isBookRoot
            ? "Edit book name. Enter saves; Escape cancels."
            : "Edit project item name. Enter saves; Escape cancels.");
        Grid.SetColumn(editor, 2);

        _editingBookRoot = isBookRoot;
        _editingPersistentId = persistentId;
        _editingOriginalName = label.Text ?? string.Empty;
        _explorerNameEditor = editor;
        _explorerNameLabel = label;
        _explorerNameHost = host;

        label.IsVisible = false;
        host.Children.Add(editor);
        editor.KeyDown += ExplorerNameKeyDown;
        editor.LostFocus += ExplorerNameLostFocus;
        editor.Focus();
        editor.SelectAll();
    }

    private async Task WaitForProjectSelectionAsync(string expectedTitle)
    {
        if (_viewModel.SelectedOutline is null &&
            string.Equals(_viewModel.SelectedRow?.Node.Title, expectedTitle, StringComparison.Ordinal))
            return;

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, EventArgs e)
        {
            if (_viewModel.SelectedOutline is null &&
                string.Equals(_viewModel.SelectedRow?.Node.Title, expectedTitle, StringComparison.Ordinal))
                completion.TrySetResult(true);
        }

        _viewModel.StateChanged += Changed;
        try
        {
            await Task.WhenAny(completion.Task, Task.Delay(800));
        }
        finally
        {
            _viewModel.StateChanged -= Changed;
        }
    }

    private async void ExplorerNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitExplorerNameAsync(cancel: false);
            _tree?.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            await CommitExplorerNameAsync(cancel: true);
            _tree?.Focus();
        }
    }

    private async void ExplorerNameLostFocus(object? sender, RoutedEventArgs e)
        => await CommitExplorerNameAsync(cancel: false);

    private async Task CommitExplorerNameAsync(bool cancel)
    {
        if (_explorerNameEditor is null || _committingExplorerName) return;
        _committingExplorerName = true;

        var editor = _explorerNameEditor;
        var proposed = editor.Text?.Trim() ?? string.Empty;
        try
        {
            if (!cancel && proposed.Length > 0 &&
                !string.Equals(proposed, _editingOriginalName, StringComparison.Ordinal))
            {
                if (_editingBookRoot)
                {
                    await RenameBookAsync(proposed);
                }
                else if (!string.IsNullOrWhiteSpace(_editingPersistentId))
                {
                    var row = _viewModel.BinderRows.FirstOrDefault(candidate =>
                        string.Equals(candidate.Node.PersistentId, _editingPersistentId, StringComparison.Ordinal));
                    if (row is not null)
                    {
                        if (!string.Equals(_viewModel.SelectedRow?.Node.PersistentId, row.Node.PersistentId, StringComparison.Ordinal))
                            await _viewModel.SelectAsync(row);
                        await _viewModel.RenameSelectedAsync(proposed);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ToolTip.SetTip(_tree, $"Could not rename item: {ex.Message}");
        }
        finally
        {
            CleanupExplorerNameEditor();
            _committingExplorerName = false;
            UpdateExplorerActionState();
        }
    }

    private void CleanupExplorerNameEditor()
    {
        var editor = _explorerNameEditor;
        if (editor is not null)
        {
            editor.KeyDown -= ExplorerNameKeyDown;
            editor.LostFocus -= ExplorerNameLostFocus;
            if (_explorerNameHost?.Children.Contains(editor) == true)
                _explorerNameHost.Children.Remove(editor);
        }

        if (_explorerNameLabel is not null)
            _explorerNameLabel.IsVisible = true;

        _explorerNameEditor = null;
        _explorerNameLabel = null;
        _explorerNameHost = null;
        _editingPersistentId = null;
        _editingOriginalName = string.Empty;
        _editingBookRoot = false;
    }

    private async Task RenameBookAsync(string newTitle)
    {
        var project = _repository.CurrentProject;
        if (project is null) return;

        var manifestPath = Path.Combine(project.RootPath, "typescribe.yaml");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("The project manifest could not be found.", manifestPath);

        var lines = (await File.ReadAllLinesAsync(manifestPath)).ToList();
        var projectIndex = lines.FindIndex(static line => string.Equals(line.Trim(), "project:", StringComparison.Ordinal));
        if (projectIndex < 0)
            throw new InvalidDataException("The project manifest does not contain a project section.");

        var titleLine = -1;
        for (var index = projectIndex + 1; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.Length > 0 && !char.IsWhiteSpace(line[0])) break;
            if (line.TrimStart().StartsWith("title:", StringComparison.Ordinal))
            {
                titleLine = index;
                break;
            }
        }

        var encoded = $"  title: {QuoteYaml(newTitle)}";
        if (titleLine >= 0) lines[titleLine] = encoded;
        else lines.Insert(projectIndex + 1, encoded);

        var tempPath = manifestPath + ".rename-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllLinesAsync(tempPath, lines);
            File.Move(tempPath, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        // Reopen through the normal repository path so the immutable project identity,
        // book root, title bar, explorer root, preview, and publishing metadata all agree.
        await _viewModel.OpenProjectAsync(project.RootPath);
    }

    private static string QuoteYaml(string value)
        => "\"" + value.Trim().Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private async Task AddHeadingAsync(int level)
    {
        var selectedRow = _viewModel.SelectedRow;
        if (selectedRow?.Node.IsDocument != true) return;

        var ownerId = selectedRow.Node.PersistentId;
        var selectedOutline = _viewModel.SelectedOutline;
        var title = await DesktopDialogService.PromptAsync(
            _window,
            $"Add H{level}",
            "Heading title",
            $"New H{level}");
        if (title is null) return;

        var ownerRow = _viewModel.BinderRows.FirstOrDefault(candidate =>
            string.Equals(candidate.Node.PersistentId, ownerId, StringComparison.Ordinal));
        if (ownerRow is null || !ownerRow.Node.IsDocument) return;

        if (!string.Equals(_viewModel.SelectedRow?.Node.PersistentId, ownerId, StringComparison.Ordinal))
            await _viewModel.SelectAsync(ownerRow);

        var updated = InsertHeading(
            _viewModel.EditorText,
            level,
            title,
            selectedOutline?.EndLine);
        if (string.Equals(updated, _viewModel.EditorText, StringComparison.Ordinal)) return;

        _viewModel.UpdateEditorText(updated);
        await _viewModel.SaveNowAsync();
    }

    private static string InsertHeading(string text, int level, string title, int? afterLine)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var heading = new string('#', Math.Clamp(level, 1, 6)) + " " + title.Trim();
        if (string.IsNullOrEmpty(text)) return heading + newline;

        var index = afterLine is int line
            ? LineStartIndex(text, line + 1)
            : text.Length;
        index = Math.Clamp(index, 0, text.Length);

        var left = text[..index].TrimEnd('\r', '\n');
        var right = text[index..].TrimStart('\r', '\n');
        var result = left.Length == 0
            ? heading
            : left + newline + newline + heading;
        if (right.Length > 0)
            result += newline + newline + right;
        else
            result += newline;
        return result;
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
        return text.Length;
    }

    private void UpdateExplorerActionState()
    {
        var enabled = _viewModel.HasDocument;
        if (_h2Button is not null) _h2Button.IsEnabled = enabled;
        if (_h3Button is not null) _h3Button.IsEnabled = enabled;

        if (_renameButton is not null)
        {
            var item = SelectedTreeItem();
            _renameButton.IsEnabled = item is not null && !IsHeadingItem(item);
        }
    }

    private TreeViewItem? SelectedTreeItem()
        => _tree?.GetVisualDescendants().OfType<TreeViewItem>().FirstOrDefault(static item => item.IsSelected);

    private static TreeViewItem? FindTreeViewItem(object? source)
    {
        if (source is TreeViewItem item) return item;
        if (source is not Control control) return null;
        return control.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
    }

    private static Grid? FindExplorerHeader(TreeViewItem item)
        => item.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(static grid =>
                grid.Children.OfType<TextBlock>().Any(block => Grid.GetColumn(block) == 1) &&
                grid.Children.OfType<TextBlock>().Any(block => Grid.GetColumn(block) == 2));

    private static bool IsHeadingItem(TreeViewItem item)
    {
        var header = FindExplorerHeader(item);
        var icon = header?.Children.OfType<TextBlock>()
            .FirstOrDefault(static block => Grid.GetColumn(block) == 1)?.Text;
        return icon is { Length: >= 2 } &&
               icon[0] == 'H' &&
               int.TryParse(icon[1..], out var level) &&
               level is >= 1 and <= 6;
    }

    private static bool IsBookRootItem(TreeViewItem item)
        => !item.GetVisualAncestors().OfType<TreeViewItem>().Any();

    private static IEnumerable<TabItem> TabItems(TabControl tabs)
        => tabs.ItemsSource is IEnumerable source
            ? source.Cast<object?>().OfType<TabItem>()
            : tabs.Items.OfType<TabItem>();

    private static bool HeaderEquals(TabItem item, string text)
        => string.Equals(item.Header?.ToString(), text, StringComparison.OrdinalIgnoreCase);

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= WindowOpened;
        _window.LayoutUpdated -= WindowLayoutUpdated;
        _window.Closed -= WindowClosed;
        _viewModel.StateChanged -= ViewModelStateChanged;
        if (_titleEditor is not null)
            _titleEditor.KeyDown -= TitleEditorKeyDown;
        if (_treeHandlersInstalled && _tree is not null)
        {
            _tree.SelectionChanged -= ExplorerSelectionChanged;
            _tree.DoubleTapped -= ExplorerDoubleTapped;
        }
        CleanupExplorerNameEditor();
    }
}
