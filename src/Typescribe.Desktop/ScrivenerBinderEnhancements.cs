using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.Editing;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;
using Typescribe.Infrastructure.Services;

namespace Typescribe.Desktop;

/// <summary>
/// Adds a manuscript-oriented tree experience on top of the existing Binder without
/// replacing its data source or drag/drop ownership. Real Binder nodes remain the only
/// draggable project objects; Markdown headings are projected as navigation-only children.
/// </summary>
internal sealed class ScrivenerBinderEnhancements
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly TrackingProjectRepository _repository;
    private readonly Dictionary<string, OutlineItemViewModel[]> _headingIndex = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedHeadingDocuments = new(StringComparer.Ordinal);
    private readonly Dictionary<ListBoxItem, BinderVisualState> _visualStates = [];

    private ListBox? _binder;
    private TabControl? _centerTabs;
    private TabItem? _editorTab;
    private TextBox? _titleEditor;
    private string? _loadedProjectRoot;
    private string? _binderStatePath;
    private CancellationTokenSource? _indexCts;
    private bool _applyScheduled;
    private bool _titleCommitRunning;
    private bool _disposed;

    private ScrivenerBinderEnhancements(
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

        var host = new ScrivenerBinderEnhancements(window, viewModel, repository);
        window.Opened += host.OnOpened;
        window.LayoutUpdated += host.OnLayoutUpdated;
        window.Closed += host.OnClosed;
        viewModel.StateChanged += host.OnStateChanged;
        host.ScheduleApply();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        ScheduleApply();
        _ = EnsureProjectIndexAsync();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => ScheduleApply();

    private void OnStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (_disposed) return;
            await EnsureProjectIndexAsync();
            RefreshSelectedHeadingCache();
            SynchronizeTitleEditor();
            ScheduleApply();
        }, DispatcherPriority.Background);
    }

    private void ScheduleApply()
    {
        if (_disposed || _applyScheduled) return;
        _applyScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _applyScheduled = false;
            if (_disposed) return;
            DiscoverControls();
            UpgradeVisibleBinderRows();
            SynchronizeTitleEditor();
        }, DispatcherPriority.Background);
    }

    private void DiscoverControls()
    {
        var controls = _window.GetVisualDescendants().OfType<Control>().ToArray();
        _binder ??= controls.OfType<ListBox>().FirstOrDefault(static list => list.ContextMenu is not null);
        if (_binder is not null && !_binder.Classes.Contains("scrivener-binder"))
            _binder.Classes.Add("scrivener-binder");

        if (_centerTabs is null)
        {
            foreach (var tabs in controls.OfType<TabControl>())
            {
                var items = TabItems(tabs);
                var editor = items.FirstOrDefault(item => string.Equals(item.Header?.ToString(), "Editor", StringComparison.Ordinal));
                if (editor is null || items.All(item => !string.Equals(item.Header?.ToString(), "Corkboard", StringComparison.Ordinal)))
                    continue;
                _centerTabs = tabs;
                _editorTab = editor;
                break;
            }
        }

        EnsureEditableDocumentTitle();
    }

    private void EnsureEditableDocumentTitle()
    {
        if (_titleEditor is not null || _editorTab?.Content is not Grid editorPanel) return;
        var header = editorPanel.Children
            .OfType<Grid>()
            .FirstOrDefault(static grid => Grid.GetRow(grid) == 0);
        if (header is null) return;

        var original = header.Children
            .OfType<TextBlock>()
            .FirstOrDefault(block => Grid.GetColumn(block) == 0 && block.FontSize >= 17);
        if (original is null) return;

        var title = new TextBox
        {
            Text = _viewModel.HasSelection ? _viewModel.SelectedTitle : string.Empty,
            Watermark = "Document title",
            FontSize = Math.Max(18, original.FontSize),
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 1),
            IsEnabled = _viewModel.HasSelection
        };
        title.Classes.Add("document-title-editor");
        title.KeyDown += TitleEditorKeyDown;
        title.LostFocus += async (_, _) => await CommitTitleAsync();

        var index = header.Children.IndexOf(original);
        var row = Grid.GetRow(original);
        var column = Grid.GetColumn(original);
        var rowSpan = Grid.GetRowSpan(original);
        var columnSpan = Grid.GetColumnSpan(original);
        header.Children.RemoveAt(index);
        header.Children.Insert(index, title);
        Grid.SetRow(title, row);
        Grid.SetColumn(title, column);
        Grid.SetRowSpan(title, rowSpan);
        Grid.SetColumnSpan(title, columnSpan);
        _titleEditor = title;
    }

    private async void TitleEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitTitleAsync();
            _window.GetVisualDescendants().OfType<ManuscriptEditor>().FirstOrDefault()?.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            SynchronizeTitleEditor(force: true);
            _window.GetVisualDescendants().OfType<ManuscriptEditor>().FirstOrDefault()?.Focus();
        }
    }

    private async Task CommitTitleAsync()
    {
        if (_titleCommitRunning || _titleEditor is null || !_viewModel.HasSelection) return;
        var requested = (_titleEditor.Text ?? string.Empty).Trim();
        if (requested.Length == 0)
        {
            SynchronizeTitleEditor(force: true);
            return;
        }
        if (string.Equals(requested, _viewModel.SelectedTitle, StringComparison.Ordinal)) return;

        _titleCommitRunning = true;
        try
        {
            await _viewModel.RenameSelectedAsync(requested);
            SynchronizeTitleEditor(force: true);
        }
        finally
        {
            _titleCommitRunning = false;
        }
    }

    private void SynchronizeTitleEditor(bool force = false)
    {
        if (_titleEditor is null) return;
        _titleEditor.IsEnabled = _viewModel.HasSelection;
        if (!force && _titleEditor.IsKeyboardFocusWithin) return;
        var title = _viewModel.HasSelection ? _viewModel.SelectedTitle : string.Empty;
        if (!string.Equals(_titleEditor.Text, title, StringComparison.Ordinal))
            _titleEditor.Text = title;
    }

    private async Task EnsureProjectIndexAsync()
    {
        var project = _repository.CurrentProject;
        if (project is null)
        {
            _loadedProjectRoot = null;
            _headingIndex.Clear();
            _expandedHeadingDocuments.Clear();
            return;
        }
        if (string.Equals(_loadedProjectRoot, project.RootPath, StringComparison.Ordinal)) return;

        _loadedProjectRoot = project.RootPath;
        _headingIndex.Clear();
        _expandedHeadingDocuments.Clear();
        _visualStates.Clear();
        _binderStatePath = Path.Combine(project.RootPath, ".typescribe", "binder-ui.tsv");
        LoadBinderState();

        _indexCts?.Cancel();
        _indexCts?.Dispose();
        _indexCts = new CancellationTokenSource();
        var token = _indexCts.Token;

        try
        {
            foreach (var row in _viewModel.BinderRows.Where(static row => row.Node.IsDocument))
            {
                token.ThrowIfCancellationRequested();
                var text = await _repository.ReadDocumentAsync(project, row.Node, token);
                var headings = ParseHeadings(text);
                _headingIndex[row.Node.PersistentId] = headings;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            // The Binder must remain usable even when one source document cannot be indexed.
        }

        RefreshSelectedHeadingCache();
        ScheduleApply();
    }

    private void RefreshSelectedHeadingCache()
    {
        var selected = _viewModel.SelectedRow;
        if (selected?.Node.IsDocument != true) return;
        var headings = _viewModel.OutlineItems.ToArray();
        var id = selected.Node.PersistentId;
        var changed = !_headingIndex.TryGetValue(id, out var existing) || !HeadingSetsEqual(existing, headings);
        _headingIndex[id] = headings;

        // Opening a document exposes its structure immediately, but users can collapse it.
        if (!_expandedHeadingDocuments.Contains(id) && headings.Length > 0 && changed)
            _expandedHeadingDocuments.Add(id);

        if (changed) UpdateVisibleDocumentRow(id);
    }

    private void UpgradeVisibleBinderRows()
    {
        if (_binder is null) return;
        var liveItems = _binder.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
        var liveSet = liveItems.ToHashSet();
        foreach (var stale in _visualStates.Keys.Where(item => !liveSet.Contains(item)).ToArray())
            _visualStates.Remove(stale);

        foreach (var item in liveItems)
        {
            if (item.Content is not BinderRowViewModel row) continue;
            if (!_visualStates.TryGetValue(item, out var state) || !string.Equals(state.PersistentId, row.Node.PersistentId, StringComparison.Ordinal))
            {
                state = CreateVisualState(item, row);
                if (state is not null) _visualStates[item] = state;
            }
            if (state is not null) UpdateVisualState(state, row);
        }
    }

    private BinderVisualState? CreateVisualState(ListBoxItem item, BinderRowViewModel row)
    {
        var grid = item.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(static candidate => candidate.Children.OfType<Button>().Any() && candidate.ColumnDefinitions.Count >= 5);
        if (grid is null) return null;

        var toggle = grid.Children.OfType<Button>().FirstOrDefault();
        if (toggle is null) return null;

        StackPanel? headings = null;
        if (row.Node.IsDocument)
        {
            if (grid.RowDefinitions.Count < 2)
                grid.RowDefinitions = new RowDefinitions("Auto,Auto");

            headings = new StackPanel
            {
                Spacing = 1,
                Margin = new Thickness(36, 1, 2, 4),
                IsVisible = false
            };
            headings.Classes.Add("binder-heading-group");
            Grid.SetRow(headings, 1);
            Grid.SetColumn(headings, 0);
            Grid.SetColumnSpan(headings, Math.Max(1, grid.ColumnDefinitions.Count));
            grid.Children.Add(headings);

            if (!toggle.Classes.Contains("heading-toggle-wired"))
            {
                toggle.Classes.Add("heading-toggle-wired");
                toggle.Click += HeadingToggleClicked;
            }
        }

        return new BinderVisualState(row.Node.PersistentId, grid, toggle, headings);
    }

    private void UpdateVisualState(BinderVisualState state, BinderRowViewModel row)
    {
        if (!row.Node.IsDocument || state.Headings is null) return;
        state.Toggle.DataContext = row.Node.PersistentId;
        var headings = _headingIndex.GetValueOrDefault(row.Node.PersistentId) ?? [];
        var expanded = headings.Length > 0 && _expandedHeadingDocuments.Contains(row.Node.PersistentId);
        state.Toggle.IsEnabled = headings.Length > 0;
        state.Toggle.Content = headings.Length == 0 ? string.Empty : expanded ? "▾" : "▸";
        state.Headings.IsVisible = expanded;
        state.Headings.Children.Clear();
        if (!expanded) return;

        foreach (var heading in headings)
            state.Headings.Children.Add(BuildHeadingRow(row, heading));
    }

    private Control BuildHeadingRow(BinderRowViewModel owner, OutlineItemViewModel heading)
    {
        var level = Math.Clamp(heading.Level, 1, 6);
        var badge = new TextBlock
        {
            Text = $"H{level}",
            Width = 25,
            FontSize = 9.5,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center
        };
        var title = new TextBlock
        {
            Text = heading.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = Math.Max(10.5, 12.5 - ((level - 1) * 0.35)),
            Opacity = Math.Max(0.58, 0.9 - ((level - 1) * 0.06)),
            VerticalAlignment = VerticalAlignment.Center
        };
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness((level - 1) * 11, 0, 0, 0)
        };
        row.Children.Add(badge);
        Grid.SetColumn(title, 1);
        row.Children.Add(title);

        var button = new Button
        {
            Content = row,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            DataContext = new HeadingNavigation(owner.Node.PersistentId, heading)
        };
        button.Classes.Add("binder-heading");
        button.PointerPressed += static (_, e) => e.Handled = true;
        button.Click += HeadingClicked;
        ToolTip.SetTip(button, $"Go to {heading.Title} — line {heading.SourceLine}");
        return button;
    }

    private void HeadingToggleClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not string id) return;
        if (!_expandedHeadingDocuments.Add(id)) _expandedHeadingDocuments.Remove(id);
        SaveBinderState();
        UpdateVisibleDocumentRow(id);
        e.Handled = true;
    }

    private async void HeadingClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not HeadingNavigation navigation) return;
        e.Handled = true;
        var owner = _viewModel.BinderRows.FirstOrDefault(row => string.Equals(row.Node.PersistentId, navigation.PersistentId, StringComparison.Ordinal));
        if (owner is null) return;

        try
        {
            if (!ReferenceEquals(_viewModel.SelectedRow, owner))
                await _viewModel.SelectAsync(owner);

            var liveHeading = _viewModel.OutlineItems.FirstOrDefault(item =>
                item.SourceLine == navigation.Heading.SourceLine &&
                string.Equals(item.Title, navigation.Heading.Title, StringComparison.Ordinal)) ?? navigation.Heading;
            _viewModel.SelectOutline(liveHeading);
            if (_centerTabs is not null && _editorTab is not null)
                _centerTabs.SelectedItem = _editorTab;
        }
        catch
        {
            // Navigation should never destabilize the Binder.
        }
    }

    private void UpdateVisibleDocumentRow(string persistentId)
    {
        foreach (var pair in _visualStates.ToArray())
        {
            if (!string.Equals(pair.Value.PersistentId, persistentId, StringComparison.Ordinal)) continue;
            if (pair.Key.Content is BinderRowViewModel row) UpdateVisualState(pair.Value, row);
        }
    }

    private void LoadBinderState()
    {
        if (string.IsNullOrWhiteSpace(_binderStatePath) || !File.Exists(_binderStatePath)) return;
        try
        {
            foreach (var line in File.ReadLines(_binderStatePath, Encoding.UTF8))
            {
                var parts = line.Split('\t');
                if (parts.Length >= 2 && string.Equals(parts[0], "headings-open", StringComparison.Ordinal))
                    _expandedHeadingDocuments.Add(parts[1]);
            }
        }
        catch
        {
        }
    }

    private void SaveBinderState()
    {
        if (string.IsNullOrWhiteSpace(_binderStatePath)) return;
        try
        {
            var directory = Path.GetDirectoryName(_binderStatePath)!;
            Directory.CreateDirectory(directory);
            var builder = new StringBuilder("# Typescribe Binder UI v1\n");
            foreach (var id in _expandedHeadingDocuments.Order(StringComparer.Ordinal))
                builder.Append("headings-open\t").Append(id).Append('\n');
            File.WriteAllText(_binderStatePath, builder.ToString(), new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    private static OutlineItemViewModel[] ParseHeadings(string text)
    {
        var pending = new List<(string Title, int Level, int SourceLine)>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var inFence = false;
        char fenceChar = '\0';
        var fenceLength = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].TrimStart();
            if (TryFence(trimmed, out var marker, out var markerLength))
            {
                if (!inFence)
                {
                    inFence = true;
                    fenceChar = marker;
                    fenceLength = markerLength;
                }
                else if (marker == fenceChar && markerLength >= fenceLength)
                {
                    inFence = false;
                }
                continue;
            }
            if (inFence) continue;

            var level = 0;
            while (level < trimmed.Length && level < 6 && trimmed[level] == '#') level++;
            if (level == 0 || level >= trimmed.Length || !char.IsWhiteSpace(trimmed[level])) continue;
            var title = trimmed[level..].Trim();
            while (title.EndsWith('#')) title = title[..^1].TrimEnd();
            if (title.Length == 0) continue;
            pending.Add((title, level, index + 1));
        }

        var result = new OutlineItemViewModel[pending.Count];
        for (var index = 0; index < pending.Count; index++)
        {
            var current = pending[index];
            var endLine = lines.Length;
            for (var next = index + 1; next < pending.Count; next++)
            {
                if (pending[next].Level > current.Level) continue;
                endLine = Math.Max(current.SourceLine, pending[next].SourceLine - 1);
                break;
            }
            result[index] = new OutlineItemViewModel(current.Title, current.Level, current.SourceLine, endLine);
        }
        return result;
    }

    private static bool TryFence(string line, out char marker, out int length)
    {
        marker = '\0';
        length = 0;
        if (line.Length < 3 || line[0] is not ('`' or '~')) return false;
        marker = line[0];
        while (length < line.Length && line[length] == marker) length++;
        return length >= 3;
    }

    private static bool HeadingSetsEqual(IReadOnlyList<OutlineItemViewModel> left, IReadOnlyList<OutlineItemViewModel> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (left[index].Level != right[index].Level || left[index].SourceLine != right[index].SourceLine ||
                !string.Equals(left[index].Title, right[index].Title, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static List<TabItem> TabItems(TabControl tabs)
        => tabs.ItemsSource is IEnumerable<object> source
            ? source.OfType<TabItem>().ToList()
            : tabs.Items.OfType<TabItem>().ToList();

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _indexCts?.Cancel();
        _indexCts?.Dispose();
        _viewModel.StateChanged -= OnStateChanged;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        if (_titleEditor is not null)
        {
            _titleEditor.KeyDown -= TitleEditorKeyDown;
        }
        _visualStates.Clear();
        SaveBinderState();
    }

    private sealed record BinderVisualState(
        string PersistentId,
        Grid Grid,
        Button Toggle,
        StackPanel? Headings);

    private sealed record HeadingNavigation(string PersistentId, OutlineItemViewModel Heading);
}
