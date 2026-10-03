using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Makes the editor document title directly editable and adds H2/H3 creation
/// commands to the native Project Explorer without reflecting over its private node type.
/// </summary>
internal sealed class EditorTitleAndExplorerHeadingFeature
{
    private const string HeadingMenuClass = "project-heading-create";
    private const string HeadingButtonClass = "project-heading-create-button";

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;

    private TextBox? _titleEditor;
    private TreeView? _tree;
    private Button? _h2Button;
    private Button? _h3Button;
    private bool _scanQueued;
    private bool _syncingTitle;
    private bool _disposed;

    private EditorTitleAndExplorerHeadingFeature(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var feature = new EditorTitleAndExplorerHeadingFeature(window, viewModel);
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
            UpdateHeadingActionState();
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
            InstallExplorerHeadingActions();
            SyncTitleFromSelection();
            UpdateHeadingActionState();
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

    private void InstallExplorerHeadingActions()
    {
        var tree = _window.GetVisualDescendants()
            .OfType<TreeView>()
            .FirstOrDefault(static candidate => candidate.Classes.Contains("project-explorer-tree"));
        if (tree is null) return;

        _tree = tree;
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

        var existing = header.Children
            .OfType<Button>()
            .Where(static button => button.Classes.Contains(HeadingButtonClass))
            .ToArray();
        if (existing.Length >= 2)
        {
            _h2Button = existing.FirstOrDefault(button => string.Equals(button.Content?.ToString(), "H2", StringComparison.Ordinal));
            _h3Button = existing.FirstOrDefault(button => string.Equals(button.Content?.ToString(), "H3", StringComparison.Ordinal));
            return;
        }

        foreach (var child in header.Children.OfType<Control>().ToArray())
        {
            var column = Grid.GetColumn(child);
            if (column >= 1) Grid.SetColumn(child, column + 2);
        }
        header.ColumnDefinitions.Insert(1, new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Insert(2, new ColumnDefinition { Width = GridLength.Auto });

        _h2Button = HeadingButton("H2", "Add a level-2 heading to the active document", 2);
        _h3Button = HeadingButton("H3", "Add a level-3 heading to the active document", 3);
        Grid.SetColumn(_h2Button, 1);
        Grid.SetColumn(_h3Button, 2);
        header.Children.Add(_h2Button);
        header.Children.Add(_h3Button);
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
        if (items.OfType<MenuItem>().Any(static item => item.Classes.Contains(HeadingMenuClass))) return;

        var h2 = HeadingMenuItem("Add H2…", 2);
        var h3 = HeadingMenuItem("Add H3…", 3);
        var firstSeparator = items.FindIndex(static item => item is Separator);
        var insertAt = firstSeparator >= 0 ? firstSeparator : items.Count;
        items.Insert(insertAt, h2);
        items.Insert(insertAt + 1, h3);
        menu.ItemsSource = items.ToArray();
    }

    private MenuItem HeadingMenuItem(string header, int level)
    {
        var item = new MenuItem { Header = header };
        item.Classes.Add(HeadingMenuClass);
        item.Click += async (_, _) => await AddHeadingAsync(level);
        return item;
    }

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

    private void UpdateHeadingActionState()
    {
        var enabled = _viewModel.HasDocument;
        if (_h2Button is not null) _h2Button.IsEnabled = enabled;
        if (_h3Button is not null) _h3Button.IsEnabled = enabled;
    }

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
    }
}
