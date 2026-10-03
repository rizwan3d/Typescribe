using System.IO.Compression;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

/// <summary>
/// Long-form writing workspace features inspired by professional authoring tools.
/// Binder rendering and navigation are owned exclusively by ProjectExplorerFeature.
/// </summary>
internal sealed class StudioScriveningsFeatures
{
    private static readonly string[] LabelPalette =
    [
        "#64748B", "#3B82F6", "#8B5CF6", "#F43F5E",
        "#F59E0B", "#10B981", "#06B6D4", "#EC4899"
    ];

    private static readonly string[] RevisionPalette =
    [
        "#94A3B8", "#2563EB", "#7C3AED", "#DB2777", "#EA580C", "#059669"
    ];

    private static readonly SolidColorBrush NeutralBorderBrush = new(Color.Parse("#3F3F46"));

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly TrackingProjectRepository _repository;
    private readonly DispatcherTimer _backupTimer;
    private readonly DispatcherTimer _stateSaveTimer;
    private readonly Dictionary<string, ScriveningDocumentState> _scriveningEditors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _saveTimers = new(StringComparer.Ordinal);
    private readonly List<BookmarkEntry> _bookmarks = [];
    private readonly Dictionary<string, Dictionary<int, int>> _revisionLines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _labelColors = new(StringComparer.Ordinal);

    private TextBox? _mainEditor;
    private TabControl? _leftTabs;
    private TabControl? _centerTabs;
    private WrapPanel? _corkboardPanel;
    private Menu? _menu;
    private TabItem? _scriveningsTab;
    private StackPanel? _scriveningsStack;
    private Grid? _scriveningsBody;
    private Border? _splitPane;
    private ComboBox? _splitPicker;
    private TextBox? _splitEditor;
    private TextBlock? _splitTitle;
    private TextBlock? _scopeLabel;
    private ComboBox? _revisionPicker;
    private CheckBox? _includedOnly;
    private ListBox? _bookmarkList;

    private ScriveningDocumentState? _activeScrivening;
    private BookProject? _splitProject;
    private ProjectNode? _splitNode;
    private string _splitSavedText = string.Empty;
    private CancellationTokenSource? _splitSaveCts;
    private CancellationTokenSource? _previewRefreshCts;

    private string? _loadedProjectRoot;
    private string? _workspaceStatePath;
    private string? _lastSelectionId;
    private string? _loadedScopeKey;
    private int _revisionLevel;
    private DateTime _lastBackupUtc = DateTime.MinValue;

    private bool _splitVisible;
    private bool _splitVertical = true;
    private bool _syncingEditors;
    private bool _menuInjected;
    private bool _bookmarksInjected;
    private bool _scriveningsInjected;
    private bool _discoverScheduled;
    private bool _discoveryComplete;
    private bool _disposed;
    private bool _backupRunning;
    private bool _stateSavePending;

    private StudioScriveningsFeatures(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository)
    {
        _window = window;
        _viewModel = viewModel;
        _repository = repository;

        _backupTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _backupTimer.Tick += async (_, _) => await BackupProjectAsync(force: false);

        _stateSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _stateSaveTimer.Tick += (_, _) =>
        {
            _stateSaveTimer.Stop();
            _stateSavePending = false;
            SaveWorkspaceState();
        };
    }

    public static void Apply(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(repository);

        var host = new StudioScriveningsFeatures(window, viewModel, repository);
        window.Opened += host.OnOpened;
        window.LayoutUpdated += host.OnLayoutUpdated;
        window.KeyDown += host.OnKeyDown;
        window.Closed += host.OnClosed;
        viewModel.StateChanged += host.OnViewModelStateChanged;
        host.ScheduleDiscover();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _backupTimer.Start();
        ScheduleDiscover();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_discoveryComplete) ScheduleDiscover();
    }

    private void OnViewModelStateChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(OnStateChangedAfterUi, DispatcherPriority.Background);

    private void ScheduleDiscover()
    {
        if (_disposed || _discoverScheduled) return;
        _discoverScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _discoverScheduled = false;
            if (!_disposed) DiscoverAndApply();
        }, DispatcherPriority.Background);
    }

    private void DiscoverAndApply()
    {
        if (_disposed) return;
        var controls = _window.GetVisualDescendants().OfType<Control>().ToArray();

        _mainEditor ??= controls.OfType<TextBox>().FirstOrDefault(static box => box.AcceptsReturn && box.AcceptsTab);
        _menu ??= controls.OfType<Menu>().FirstOrDefault();

        foreach (var tabs in controls.OfType<TabControl>())
        {
            var headers = TabItems(tabs).Select(static item => item.Header?.ToString() ?? string.Empty).ToArray();
            if (_leftTabs is null && headers.Contains("Binder", StringComparer.Ordinal)) _leftTabs = tabs;
            if (_centerTabs is null && headers.Contains("Editor", StringComparer.Ordinal) && headers.Contains("Corkboard", StringComparer.Ordinal)) _centerTabs = tabs;
        }

        _corkboardPanel ??= controls.OfType<WrapPanel>().FirstOrDefault(panel =>
            panel.Children.OfType<Border>().Any(border => border.Width >= 230 && border.MinHeight >= 150));

        if (_mainEditor is not null && !_mainEditor.Classes.Contains("revision-tracking-wired"))
        {
            _mainEditor.Classes.Add("revision-tracking-wired");
            _mainEditor.TextChanged += MainEditorTextChanged;
        }

        InjectScriveningsTab();
        InjectBookmarksTab();
        InjectMenus();
        EnsureProjectWorkspace();
        ApplyLabelColorsToCorkboard();
        UpdateMainRevisionAccent();

        _discoveryComplete = _mainEditor is not null && _leftTabs is not null &&
                             _centerTabs is not null && _menu is not null &&
                             _scriveningsInjected && _bookmarksInjected && _menuInjected;
        if (_discoveryComplete) _window.LayoutUpdated -= OnLayoutUpdated;
    }

    private async void OnStateChangedAfterUi()
    {
        if (_disposed) return;
        await EnsureProjectWorkspaceAsync();
        ApplyLabelColorsToCorkboard();
        UpdateMainRevisionAccent();

        var selectedId = _viewModel.SelectedRow?.Node.PersistentId;
        if (!string.Equals(selectedId, _lastSelectionId, StringComparison.Ordinal))
        {
            _lastSelectionId = selectedId;
            RefreshBookmarks();
            if (_viewModel.SelectedIsContainer && _scriveningsTab is not null && _centerTabs is not null)
            {
                _centerTabs.SelectedItem = _scriveningsTab;
                await ReloadScriveningsAsync(force: true);
            }
            else if (_centerTabs?.SelectedItem == _scriveningsTab)
            {
                await ReloadScriveningsAsync(force: true);
            }
        }
    }

    private void EnsureProjectWorkspace()
    {
        if (_repository.CurrentProject is null) return;
        _ = EnsureProjectWorkspaceAsync();
    }

    private async Task EnsureProjectWorkspaceAsync()
    {
        var project = _repository.CurrentProject;
        if (project is null || string.Equals(project.RootPath, _loadedProjectRoot, StringComparison.Ordinal)) return;

        await FlushAllScriveningSavesAsync();
        _loadedProjectRoot = project.RootPath;
        var metadata = Path.Combine(project.RootPath, ".typescribe");
        Directory.CreateDirectory(metadata);
        _workspaceStatePath = Path.Combine(metadata, "workspace.tsv");
        LoadWorkspaceState();
        RefreshBookmarks();
        _loadedScopeKey = null;
        _splitProject = null;
        _splitNode = null;
        _splitSavedText = string.Empty;
        _lastBackupUtc = NewestBackupUtc(project.RootPath);
    }

    private void InjectScriveningsTab()
    {
        if (_scriveningsInjected || _centerTabs is null) return;
        var items = TabItems(_centerTabs);
        var existing = items.FirstOrDefault(item => string.Equals(item.Header?.ToString(), "Scrivenings", StringComparison.Ordinal));
        if (existing is not null)
        {
            _scriveningsTab = existing;
            _scriveningsInjected = true;
            return;
        }

        _scopeLabel = new TextBlock
        {
            Text = "Scrivenings",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };

        _revisionPicker = new ComboBox
        {
            ItemsSource = new[] { "Revisions Off", "Revision 1", "Revision 2", "Revision 3", "Revision 4", "Revision 5" },
            SelectedIndex = 0,
            MinWidth = 122
        };
        _revisionPicker.SelectionChanged += (_, _) =>
        {
            _revisionLevel = Math.Clamp(_revisionPicker.SelectedIndex, 0, 5);
            UpdateAllRevisionAccents();
        };

        _includedOnly = new CheckBox { Content = "Included only", VerticalAlignment = VerticalAlignment.Center };
        _includedOnly.Click += async (_, _) => await ReloadScriveningsAsync(force: true);

        var goTo = new Button { Content = "Go To…" };
        goTo.Click += async (_, _) => await ShowGoToAsync();
        var bookmark = new Button { Content = "Bookmark" };
        bookmark.Click += async (_, _) => await AddBookmarkAsync();
        var split = new Button { Content = "Split Editor" };
        split.Click += async (_, _) =>
        {
            _splitVisible = !_splitVisible;
            ApplySplitLayout();
            if (_splitVisible) await EnsureSplitDocumentAsync();
        };
        var orientation = new Button { Content = "⇄", MinWidth = 34 };
        ToolTip.SetTip(orientation, "Switch split orientation");
        orientation.Click += (_, _) =>
        {
            _splitVertical = !_splitVertical;
            orientation.Content = _splitVertical ? "⇄" : "⇅";
            ApplySplitLayout();
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto,Auto"),
            Margin = new Thickness(10, 7)
        };
        header.Children.Add(_scopeLabel);
        Grid.SetColumn(_includedOnly, 1); _includedOnly.Margin = new Thickness(8, 0); header.Children.Add(_includedOnly);
        Grid.SetColumn(_revisionPicker, 2); _revisionPicker.Margin = new Thickness(6, 0); header.Children.Add(_revisionPicker);
        Grid.SetColumn(goTo, 3); goTo.Margin = new Thickness(6, 0); header.Children.Add(goTo);
        Grid.SetColumn(bookmark, 4); bookmark.Margin = new Thickness(6, 0); header.Children.Add(bookmark);
        Grid.SetColumn(split, 5); split.Margin = new Thickness(6, 0); header.Children.Add(split);
        Grid.SetColumn(orientation, 6); header.Children.Add(orientation);

        _scriveningsStack = new StackPanel { Spacing = 10, Margin = new Thickness(16, 8, 16, 26) };
        var primaryScroll = new ScrollViewer
        {
            Content = _scriveningsStack,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };

        _splitPicker = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Stretch };
        _splitPicker.SelectionChanged += async (_, _) => await SplitSelectionChangedAsync();
        _splitTitle = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 15, Text = "Split Editor" };
        _splitEditor = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("monospace"),
            FontSize = 15,
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(18)
        };
        _splitEditor.TextChanged += SplitEditorTextChanged;

        var splitContent = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(12) };
        splitContent.Children.Add(_splitTitle);
        Grid.SetRow(_splitPicker, 1); _splitPicker.Margin = new Thickness(0, 8, 0, 0); splitContent.Children.Add(_splitPicker);
        Grid.SetRow(_splitEditor, 2); splitContent.Children.Add(_splitEditor);
        _splitPane = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(6),
            IsVisible = false,
            Child = splitContent
        };

        _scriveningsBody = new Grid { ColumnDefinitions = new ColumnDefinitions("*,0,0"), RowDefinitions = new RowDefinitions("*") };
        _scriveningsBody.Children.Add(primaryScroll);
        var splitter = new GridSplitter
        {
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ShowsPreview = true,
            Width = 5,
            IsVisible = false
        };
        Grid.SetColumn(splitter, 1);
        _scriveningsBody.Children.Add(splitter);
        Grid.SetColumn(_splitPane, 2);
        _scriveningsBody.Children.Add(_splitPane);

        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        content.Children.Add(header);
        Grid.SetRow(_scriveningsBody, 1); content.Children.Add(_scriveningsBody);

        _scriveningsTab = new TabItem { Header = "Scrivenings", Content = content };
        items.Insert(Math.Min(1, items.Count), _scriveningsTab);
        _centerTabs.ItemsSource = items.ToArray();
        _centerTabs.SelectionChanged += CenterTabSelectionChanged;
        _scriveningsInjected = true;
    }

    private async void CenterTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_centerTabs?.SelectedItem == _scriveningsTab)
            await ReloadScriveningsAsync(force: false);
    }

    private async Task ReloadScriveningsAsync(bool force)
    {
        if (_scriveningsStack is null || _repository.CurrentProject is not { } project) return;
        var rows = ScopeRows().ToArray();
        var key = string.Join('|', rows.Select(static row => row.Node.PersistentId)) + $":{_includedOnly?.IsChecked == true}";
        if (!force && string.Equals(key, _loadedScopeKey, StringComparison.Ordinal)) return;

        await FlushAllScriveningSavesAsync();
        _loadedScopeKey = key;
        _scriveningEditors.Clear();
        _scriveningsStack.Children.Clear();
        _activeScrivening = null;

        _scopeLabel!.Text = _viewModel.SelectedIsContainer
            ? $"Scrivenings — {_viewModel.SelectedTitle}"
            : _viewModel.HasDocument
                ? $"Scrivenings — {_viewModel.SelectedTitle}"
                : "Scrivenings — Manuscript";

        if (rows.Length == 0)
        {
            _scriveningsStack.Children.Add(new TextBlock
            {
                Text = "No manuscript documents in this scope.",
                Margin = new Thickness(18),
                Opacity = 0.7
            });
            return;
        }

        foreach (var row in rows)
        {
            var text = ReferenceEquals(row, _viewModel.SelectedRow)
                ? _viewModel.EditorText
                : await _repository.ReadDocumentAsync(project, row.Node);
            var state = BuildScriveningDocument(project, row, text);
            _scriveningEditors[row.Node.PersistentId] = state;
            _scriveningsStack.Children.Add(state.Container);
        }
        RefreshSplitPicker();
    }

    private ScriveningDocumentState BuildScriveningDocument(BookProject project, BinderRowViewModel row, string text)
    {
        var revision = new TextBlock { FontSize = 11, Opacity = 0.72, VerticalAlignment = VerticalAlignment.Center };
        var words = new TextBlock { FontSize = 11, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = row.Node.Label, FontSize = 11, Opacity = 0.72, VerticalAlignment = VerticalAlignment.Center };
        var title = new Button
        {
            Content = row.Node.Title,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        title.Click += async (_, _) => await SelectNodeAsync(row);

        var bookmark = new Button { Content = "⌑", MinWidth = 32, Padding = new Thickness(6, 3) };
        ToolTip.SetTip(bookmark, "Bookmark this writing position");

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"), Margin = new Thickness(8, 6) };
        header.Children.Add(title);
        Grid.SetColumn(label, 1); label.Margin = new Thickness(10, 0); header.Children.Add(label);
        Grid.SetColumn(revision, 2); revision.Margin = new Thickness(8, 0); header.Children.Add(revision);
        Grid.SetColumn(words, 3); words.Margin = new Thickness(8, 0); header.Children.Add(words);
        Grid.SetColumn(bookmark, 4); header.Children.Add(bookmark);

        var editor = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("monospace"),
            FontSize = 15,
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(24, 18),
            MinHeight = 180,
            Height = Math.Clamp(90 + CountLines(text) * 22, 180, 720),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var accent = new Border { Width = 4, CornerRadius = new CornerRadius(3, 0, 0, 3) };
        var editorGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("4,*") };
        editorGrid.Children.Add(accent);
        Grid.SetColumn(editor, 1); editorGrid.Children.Add(editor);

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        body.Children.Add(header);
        Grid.SetRow(editorGrid, 1); body.Children.Add(editorGrid);

        var container = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Margin = new Thickness(0, 0, 0, 4),
            Child = body
        };

        var state = new ScriveningDocumentState(project, row, container, editor, accent, revision, words, text);
        editor.GotFocus += (_, _) => _activeScrivening = state;
        editor.TextChanged += (_, _) => ScriveningTextChanged(state);
        bookmark.Click += async (_, _) =>
        {
            _activeScrivening = state;
            await AddBookmarkAsync(state);
        };
        UpdateScriveningVisuals(state);
        return state;
    }

    private void ScriveningTextChanged(ScriveningDocumentState state)
    {
        if (_syncingEditors) return;
        var text = state.Editor.Text ?? string.Empty;
        state.Dirty = !string.Equals(text, state.LastSavedText, StringComparison.Ordinal);
        state.Words.Text = $"{CountWords(text):N0} words";
        state.Editor.Height = Math.Clamp(90 + CountLines(text) * 22, 180, 720);

        if (state.Editor.IsKeyboardFocusWithin && _revisionLevel > 0)
            RecordRevision(state.Row.Node.PersistentId, LineFromCaret(state.Editor), _revisionLevel);

        SyncDuplicateEditors(state.Row.Node.PersistentId, state.Editor, text);
        UpdateScriveningVisuals(state);
        ScheduleDocumentSave(state);
    }

    private void ScheduleDocumentSave(ScriveningDocumentState state)
    {
        if (_saveTimers.Remove(state.Row.Node.PersistentId, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }
        var cts = new CancellationTokenSource();
        _saveTimers[state.Row.Node.PersistentId] = cts;
        _ = SaveAfterDelayAsync(state, cts);
    }

    private async Task SaveAfterDelayAsync(ScriveningDocumentState state, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(800, cts.Token);
            await SaveScriveningDocumentAsync(state, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch { }
        finally
        {
            if (_saveTimers.TryGetValue(state.Row.Node.PersistentId, out var current) && ReferenceEquals(current, cts))
                _saveTimers.Remove(state.Row.Node.PersistentId);
            cts.Dispose();
        }
    }

    private async Task SaveScriveningDocumentAsync(ScriveningDocumentState state, CancellationToken cancellationToken = default)
    {
        if (!state.Dirty) return;
        var text = state.Editor.Text ?? string.Empty;
        await _repository.SaveDocumentAsync(state.Project, state.Row.Node, text, cancellationToken);
        state.LastSavedText = text;
        state.Dirty = false;

        if (string.Equals(_viewModel.SelectedRow?.Node.PersistentId, state.Row.Node.PersistentId, StringComparison.Ordinal) &&
            ReferenceEquals(state.Project, _repository.CurrentProject))
            _viewModel.UpdateEditorText(text);

        SchedulePreviewRefresh();
    }

    private async Task FlushAllScriveningSavesAsync()
    {
        foreach (var cts in _saveTimers.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _saveTimers.Clear();

        var states = _scriveningEditors.Values.ToArray();
        foreach (var state in states)
        {
            try { await SaveScriveningDocumentAsync(state); }
            catch { }
        }
        await SaveSplitEditorAsync();
    }

    private void SchedulePreviewRefresh()
    {
        _previewRefreshCts?.Cancel();
        _previewRefreshCts?.Dispose();
        _previewRefreshCts = new CancellationTokenSource();
        var token = _previewRefreshCts.Token;
        _ = RefreshPreviewAfterDelayAsync(token);
    }

    private async Task RefreshPreviewAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(900, token);
            if (_viewModel.CanPublishPdf && _repository.CurrentProject is not null)
                await _viewModel.RefreshLivePdfPreviewAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { }
    }

    private IEnumerable<BinderRowViewModel> ScopeRows()
    {
        var all = _viewModel.BinderRows;
        if (all.Count == 0) return [];
        var selected = _viewModel.SelectedRow;
        IEnumerable<BinderRowViewModel> scope;

        if (selected is null)
        {
            scope = all.Where(static row => row.Node.IsDocument);
        }
        else if (selected.Node.IsDocument)
        {
            scope = [selected];
        }
        else
        {
            var index = all.IndexOf(selected);
            if (index < 0)
            {
                scope = all.Where(static row => row.Node.IsDocument);
            }
            else
            {
                var depth = selected.Depth;
                var descendants = new List<BinderRowViewModel>();
                for (var cursor = index + 1; cursor < all.Count; cursor++)
                {
                    var row = all[cursor];
                    if (row.Depth <= depth) break;
                    if (row.Node.IsDocument) descendants.Add(row);
                }
                scope = descendants;
            }
        }

        if (_includedOnly?.IsChecked == true)
            scope = scope.Where(static row => row.Node.IncludeInCompilation);
        return scope;
    }

    private void ApplySplitLayout()
    {
        if (_scriveningsBody is null || _splitPane is null) return;
        _splitPane.IsVisible = _splitVisible;
        var splitter = _scriveningsBody.Children.OfType<GridSplitter>().FirstOrDefault();
        if (splitter is not null) splitter.IsVisible = _splitVisible;
        var primary = _scriveningsBody.Children.OfType<ScrollViewer>().FirstOrDefault();
        if (primary is null) return;

        if (!_splitVisible)
        {
            _scriveningsBody.ColumnDefinitions = new ColumnDefinitions("*,0,0");
            _scriveningsBody.RowDefinitions = new RowDefinitions("*");
            Grid.SetRow(primary, 0); Grid.SetColumn(primary, 0);
            if (splitter is not null) { Grid.SetRow(splitter, 0); Grid.SetColumn(splitter, 1); }
            Grid.SetRow(_splitPane, 0); Grid.SetColumn(_splitPane, 2);
            return;
        }

        if (_splitVertical)
        {
            _scriveningsBody.RowDefinitions = new RowDefinitions("*");
            _scriveningsBody.ColumnDefinitions = new ColumnDefinitions("*,5,*");
            Grid.SetRow(primary, 0); Grid.SetColumn(primary, 0);
            if (splitter is not null)
            {
                splitter.ResizeDirection = GridResizeDirection.Columns;
                splitter.Width = 5;
                splitter.Height = double.NaN;
                Grid.SetRow(splitter, 0); Grid.SetColumn(splitter, 1);
            }
            Grid.SetRow(_splitPane, 0); Grid.SetColumn(_splitPane, 2);
        }
        else
        {
            _scriveningsBody.ColumnDefinitions = new ColumnDefinitions("*");
            _scriveningsBody.RowDefinitions = new RowDefinitions("*,5,*");
            Grid.SetRow(primary, 0); Grid.SetColumn(primary, 0);
            if (splitter is not null)
            {
                splitter.ResizeDirection = GridResizeDirection.Rows;
                splitter.Height = 5;
                splitter.Width = double.NaN;
                Grid.SetRow(splitter, 1); Grid.SetColumn(splitter, 0);
            }
            Grid.SetRow(_splitPane, 2); Grid.SetColumn(_splitPane, 0);
        }
    }

    private void RefreshSplitPicker()
    {
        if (_splitPicker is null) return;
        var rows = _viewModel.BinderRows.Where(static row => row.Node.IsDocument).ToArray();
        var choices = rows.Select(static row => new DocumentChoice(row.Node.PersistentId, row.Node.Title)).ToArray();
        _splitPicker.ItemsSource = choices;
        if (_splitNode is null) return;
        var index = Array.FindIndex(choices, choice => string.Equals(choice.PersistentId, _splitNode.PersistentId, StringComparison.Ordinal));
        if (index >= 0) _splitPicker.SelectedIndex = index;
    }

    private async Task EnsureSplitDocumentAsync()
    {
        if (_splitPicker is null) return;
        RefreshSplitPicker();
        if (_splitPicker.SelectedIndex < 0 && _splitPicker.ItemsSource is ICollection<DocumentChoice> choices && choices.Count > 0)
            _splitPicker.SelectedIndex = 0;
        await SplitSelectionChangedAsync();
    }

    private async Task SplitSelectionChangedAsync()
    {
        if (_syncingEditors || _splitPicker?.SelectedItem is not DocumentChoice choice || _repository.CurrentProject is not { } project) return;
        await SaveSplitEditorAsync();
        var row = _viewModel.BinderRows.FirstOrDefault(row => string.Equals(row.Node.PersistentId, choice.PersistentId, StringComparison.Ordinal));
        if (row is null || _splitEditor is null || _splitTitle is null) return;

        _splitProject = project;
        _splitNode = row.Node;
        var text = _scriveningEditors.TryGetValue(row.Node.PersistentId, out var existing)
            ? existing.Editor.Text ?? string.Empty
            : ReferenceEquals(row, _viewModel.SelectedRow)
                ? _viewModel.EditorText
                : await _repository.ReadDocumentAsync(project, row.Node);
        _syncingEditors = true;
        try { _splitEditor.Text = text; }
        finally { _syncingEditors = false; }
        _splitSavedText = text;
        _splitTitle.Text = row.Node.Title;
        UpdateSplitRevisionAccent();
    }

    private void SplitEditorTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncingEditors || _splitEditor is null || _splitNode is null || _splitProject is null) return;
        var text = _splitEditor.Text ?? string.Empty;
        if (_splitEditor.IsKeyboardFocusWithin && _revisionLevel > 0)
            RecordRevision(_splitNode.PersistentId, LineFromCaret(_splitEditor), _revisionLevel);
        SyncDuplicateEditors(_splitNode.PersistentId, _splitEditor, text);
        UpdateSplitRevisionAccent();
        ScheduleSplitSave(_splitProject, _splitNode, text);
    }

    private void ScheduleSplitSave(BookProject project, ProjectNode node, string text)
    {
        _splitSaveCts?.Cancel();
        _splitSaveCts?.Dispose();
        _splitSaveCts = new CancellationTokenSource();
        var token = _splitSaveCts.Token;
        _ = SaveSplitAfterDelayAsync(project, node, text, token);
    }

    private async Task SaveSplitAfterDelayAsync(BookProject project, ProjectNode node, string text, CancellationToken token)
    {
        try
        {
            await Task.Delay(800, token);
            await _repository.SaveDocumentAsync(project, node, text, token);
            if (ReferenceEquals(project, _splitProject) && ReferenceEquals(node, _splitNode)) _splitSavedText = text;
            SchedulePreviewRefresh();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { }
    }

    private async Task SaveSplitEditorAsync()
    {
        _splitSaveCts?.Cancel();
        _splitSaveCts?.Dispose();
        _splitSaveCts = null;
        var project = _splitProject;
        var node = _splitNode;
        var editor = _splitEditor;
        if (project is null || node is null || editor is null) return;
        var text = editor.Text ?? string.Empty;
        if (string.Equals(text, _splitSavedText, StringComparison.Ordinal)) return;
        await _repository.SaveDocumentAsync(project, node, text);
        if (ReferenceEquals(project, _splitProject) && ReferenceEquals(node, _splitNode)) _splitSavedText = text;
    }

    private void SyncDuplicateEditors(string persistentId, TextBox source, string text)
    {
        if (_syncingEditors) return;
        _syncingEditors = true;
        try
        {
            if (_scriveningEditors.TryGetValue(persistentId, out var state) && !ReferenceEquals(state.Editor, source))
                state.Editor.Text = text;
            if (_splitNode is not null && string.Equals(_splitNode.PersistentId, persistentId, StringComparison.Ordinal) &&
                _splitEditor is not null && !ReferenceEquals(_splitEditor, source))
                _splitEditor.Text = text;
            if (string.Equals(_viewModel.SelectedRow?.Node.PersistentId, persistentId, StringComparison.Ordinal) &&
                _mainEditor is not null && !ReferenceEquals(_mainEditor, source))
                _mainEditor.Text = text;
        }
        finally { _syncingEditors = false; }
    }

    private void InjectBookmarksTab()
    {
        if (_bookmarksInjected || _leftTabs is null) return;
        var items = TabItems(_leftTabs);
        if (items.Any(item => string.Equals(item.Header?.ToString(), "Bookmarks", StringComparison.Ordinal)))
        {
            _bookmarksInjected = true;
            return;
        }

        _bookmarkList = new ListBox();
        _bookmarkList.DoubleTapped += async (_, _) => await NavigateSelectedBookmarkAsync();
        var add = new Button { Content = "Add Bookmark" };
        var remove = new Button { Content = "Remove", Margin = new Thickness(6, 0, 0, 0) };
        add.Click += async (_, _) => await AddBookmarkAsync();
        remove.Click += (_, _) => RemoveSelectedBookmark();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6), Children = { add, remove } };
        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        panel.Children.Add(buttons);
        Grid.SetRow(_bookmarkList, 1); panel.Children.Add(_bookmarkList);
        items.Add(new TabItem { Header = "Bookmarks", Content = panel });
        _leftTabs.ItemsSource = items.ToArray();
        _bookmarksInjected = true;
        RefreshBookmarks();
    }

    private async Task AddBookmarkAsync(ScriveningDocumentState? state = null)
    {
        ProjectNode? node;
        TextBox? editor;
        if (state is not null)
        {
            node = state.Row.Node;
            editor = state.Editor;
        }
        else if (_activeScrivening?.Editor.IsKeyboardFocusWithin == true)
        {
            node = _activeScrivening.Row.Node;
            editor = _activeScrivening.Editor;
        }
        else if (_splitEditor?.IsKeyboardFocusWithin == true && _splitNode is not null)
        {
            node = _splitNode;
            editor = _splitEditor;
        }
        else
        {
            node = _viewModel.SelectedRow?.Node;
            editor = _mainEditor;
        }
        if (node?.IsDocument != true || editor is null) return;

        var caret = Math.Clamp(editor.CaretIndex, 0, (editor.Text ?? string.Empty).Length);
        var (line, column) = LineColumn(editor.Text ?? string.Empty, caret);
        var label = await DesktopDialogService.PromptAsync(_window, "Add Bookmark", "Bookmark name", $"{node.Title} — line {line}");
        if (label is null) return;
        var (anchor, anchorOffset) = AnchorAt(editor.Text ?? string.Empty, caret);
        _bookmarks.Add(new BookmarkEntry(
            Guid.NewGuid().ToString("N"), node.PersistentId, label.Trim(), caret, line, column, anchor, anchorOffset));
        ScheduleStateSave();
        RefreshBookmarks();
    }

    private async Task NavigateSelectedBookmarkAsync()
    {
        if (_bookmarkList?.SelectedItem is not BookmarkEntry bookmark) return;
        var row = _viewModel.BinderRows.FirstOrDefault(row =>
            string.Equals(row.Node.PersistentId, bookmark.PersistentId, StringComparison.Ordinal));
        if (row is null) return;
        await _viewModel.SelectAsync(row);
        if (_centerTabs is not null) _centerTabs.SelectedIndex = 0;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_mainEditor is null) return;
            var text = _mainEditor.Text ?? string.Empty;
            var caret = ResolveBookmarkPosition(bookmark, text);
            _mainEditor.CaretIndex = caret;
            _mainEditor.SelectionStart = caret;
            _mainEditor.SelectionEnd = caret;
            _mainEditor.Focus();
        }, DispatcherPriority.Background);
    }

    private void RemoveSelectedBookmark()
    {
        if (_bookmarkList?.SelectedItem is not BookmarkEntry bookmark) return;
        _bookmarks.RemoveAll(item => string.Equals(item.Id, bookmark.Id, StringComparison.Ordinal));
        ScheduleStateSave();
        RefreshBookmarks();
    }

    private void RefreshBookmarks()
    {
        if (_bookmarkList is null) return;
        var titles = _viewModel.BinderRows.ToDictionary(
            static row => row.Node.PersistentId,
            static row => row.Node.Title,
            StringComparer.Ordinal);
        _bookmarkList.ItemsSource = _bookmarks
            .Where(bookmark => titles.ContainsKey(bookmark.PersistentId))
            .Select(bookmark => bookmark with { DisplayTitle = $"{titles[bookmark.PersistentId]}  •  {bookmark.Label}" })
            .ToArray();
    }

    private async Task ShowGoToAsync()
    {
        var source = _viewModel.BinderRows.Where(static row => row.Node.IsDocument).ToArray();
        if (source.Length == 0) return;
        var search = new TextBox { PlaceholderText = "Type a chapter, scene, note, or path…", Margin = new Thickness(0, 0, 0, 8) };
        var list = new ListBox();
        var open = new Button { Content = "Open", MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { open, cancel } };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(14) };
        grid.Children.Add(search);
        Grid.SetRow(list, 1); grid.Children.Add(list);
        Grid.SetRow(buttons, 2); buttons.Margin = new Thickness(0, 10, 0, 0); grid.Children.Add(buttons);
        var dialog = new Window
        {
            Title = "Go To Document",
            Width = 620,
            Height = 620,
            MinWidth = 440,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = grid
        };

        void Filter()
        {
            var query = search.Text?.Trim() ?? string.Empty;
            list.ItemsSource = source
                .Where(row => query.Length == 0 || row.Node.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                              (row.Node.RelativePath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .Select(static row => new GoToEntry(row.Node.PersistentId, row.Node.Title, row.Node.RelativePath ?? string.Empty))
                .ToArray();
            if (list.ItemCount > 0) list.SelectedIndex = 0;
        }

        search.TextChanged += (_, _) => Filter();
        search.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            dialog.Close(list.SelectedItem as GoToEntry);
        };
        list.DoubleTapped += (_, _) => dialog.Close(list.SelectedItem as GoToEntry);
        open.Click += (_, _) => dialog.Close(list.SelectedItem as GoToEntry);
        cancel.Click += (_, _) => dialog.Close(null);
        Filter();
        search.Focus();
        var selected = await dialog.ShowDialog<GoToEntry?>(_window);
        if (selected is null) return;
        var row = _viewModel.BinderRows.FirstOrDefault(row =>
            string.Equals(row.Node.PersistentId, selected.PersistentId, StringComparison.Ordinal));
        if (row is not null) await SelectNodeAsync(row);
    }

    private async Task SelectNodeAsync(BinderRowViewModel row)
        => await _viewModel.SelectAsync(row);

    private void MainEditorTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncingEditors || _mainEditor?.IsKeyboardFocusWithin != true || _revisionLevel <= 0) return;
        var node = _viewModel.SelectedRow?.Node;
        if (node?.IsDocument != true) return;
        RecordRevision(node.PersistentId, LineFromCaret(_mainEditor), _revisionLevel);
        UpdateMainRevisionAccent();
    }

    private void RecordRevision(string persistentId, int line, int level)
    {
        if (level <= 0) return;
        if (!_revisionLines.TryGetValue(persistentId, out var lines))
        {
            lines = new Dictionary<int, int>();
            _revisionLines[persistentId] = lines;
        }
        lines[Math.Max(1, line)] = Math.Clamp(level, 1, 5);
        ScheduleStateSave();
    }

    private void UpdateScriveningVisuals(ScriveningDocumentState state)
    {
        state.Words.Text = $"{CountWords(state.Editor.Text ?? string.Empty):N0} words";
        var highest = HighestRevision(state.Row.Node.PersistentId);
        state.Revision.Text = RevisionSummary(state.Row.Node.PersistentId);
        state.Accent.Background = highest > 0 ? RevisionBrush(highest) : LabelBrush(state.Row.Node);
        state.Container.BorderBrush = NeutralBorderBrush;
        state.Container.Background = SurfaceTint(state.Row.Node);
    }

    private void UpdateAllRevisionAccents()
    {
        foreach (var state in _scriveningEditors.Values) UpdateScriveningVisuals(state);
        UpdateMainRevisionAccent();
        UpdateSplitRevisionAccent();
    }

    private void UpdateMainRevisionAccent()
    {
        if (_mainEditor is null) return;
        var id = _viewModel.SelectedRow?.Node.PersistentId;
        var highest = id is null ? 0 : HighestRevision(id);
        if (highest > 0)
        {
            _mainEditor.BorderBrush = RevisionBrush(highest);
            _mainEditor.BorderThickness = new Thickness(2, 1, 1, 1);
        }
        else
        {
            _mainEditor.ClearValue(TextBox.BorderBrushProperty);
            _mainEditor.ClearValue(TextBox.BorderThicknessProperty);
        }
    }

    private void UpdateSplitRevisionAccent()
    {
        if (_splitEditor is null || _splitNode is null) return;
        var highest = HighestRevision(_splitNode.PersistentId);
        if (highest > 0)
        {
            _splitEditor.BorderBrush = RevisionBrush(highest);
            _splitEditor.BorderThickness = new Thickness(2, 1, 1, 1);
        }
        else
        {
            _splitEditor.ClearValue(TextBox.BorderBrushProperty);
            _splitEditor.ClearValue(TextBox.BorderThicknessProperty);
        }
    }

    private int HighestRevision(string persistentId)
        => _revisionLines.TryGetValue(persistentId, out var lines) && lines.Count > 0 ? lines.Values.Max() : 0;

    private string RevisionSummary(string persistentId)
    {
        if (!_revisionLines.TryGetValue(persistentId, out var lines) || lines.Count == 0) return string.Empty;
        return $"R{lines.Values.Max()} • {lines.Count} line{(lines.Count == 1 ? string.Empty : "s")}";
    }

    private void InjectMenus()
    {
        if (_menuInjected || _menu?.ItemsSource is not IEnumerable<object> top) return;
        var menus = top.OfType<MenuItem>().ToArray();
        var view = menus.FirstOrDefault(item => HeaderEquals(item, "View"));
        var document = menus.FirstOrDefault(item => HeaderEquals(item, "Document"));
        var project = menus.FirstOrDefault(item => HeaderEquals(item, "Project"));

        if (view is not null)
        {
            var items = MenuItems(view.ItemsSource);
            items.Add(new Separator());
            items.Add(Command("Scrivenings", () => { ShowScrivenings(); return Task.CompletedTask; }, new KeyGesture(Key.D1, PrimaryModifier() | KeyModifiers.Shift)));
            items.Add(Command("Go To Document…", ShowGoToAsync, new KeyGesture(Key.G, PrimaryModifier())));
            items.Add(Command("Toggle Split Editor", async () =>
            {
                ShowScrivenings();
                _splitVisible = !_splitVisible;
                ApplySplitLayout();
                if (_splitVisible) await EnsureSplitDocumentAsync();
            }));
            view.ItemsSource = items.ToArray();
        }

        if (document is not null)
        {
            var items = MenuItems(document.ItemsSource);
            items.Add(new Separator());
            items.Add(Command("Add Bookmark…", () => AddBookmarkAsync(), new KeyGesture(Key.B, PrimaryModifier() | KeyModifiers.Shift)));
            items.Add(LabelColorMenu());
            items.Add(RevisionMenu());
            document.ItemsSource = items.ToArray();
        }

        if (project is not null)
        {
            var items = MenuItems(project.ItemsSource);
            items.Add(new Separator());
            items.Add(Command("Backup Now", () => BackupProjectAsync(force: true)));
            items.Add(Command("Open Backups Folder", () => { OpenBackupsFolder(); return Task.CompletedTask; }));
            project.ItemsSource = items.ToArray();
        }
        _menuInjected = true;
    }

    private MenuItem LabelColorMenu()
    {
        var menu = new MenuItem { Header = "Label Color" };
        var items = new List<object>
        {
            Command("Automatic", () => { SetSelectedLabelColor(null); return Task.CompletedTask; })
        };
        string[] names = ["Slate", "Blue", "Violet", "Rose", "Amber", "Emerald", "Cyan", "Pink"];
        for (var index = 0; index < LabelPalette.Length; index++)
        {
            var color = LabelPalette[index];
            var name = names[index];
            items.Add(Command(name, () => { SetSelectedLabelColor(color); return Task.CompletedTask; }));
        }
        menu.ItemsSource = items.ToArray();
        return menu;
    }

    private MenuItem RevisionMenu()
    {
        var menu = new MenuItem { Header = "Revision Mode" };
        var items = new List<object>
        {
            Command("Off", () => { SetRevisionLevel(0); return Task.CompletedTask; })
        };
        for (var level = 1; level <= 5; level++)
        {
            var captured = level;
            items.Add(Command($"Revision {captured}", () => { SetRevisionLevel(captured); return Task.CompletedTask; }));
        }
        items.Add(new Separator());
        items.Add(Command("Clear Revisions for Current Document", () =>
        {
            var id = _viewModel.SelectedRow?.Node.PersistentId;
            if (id is not null) _revisionLines.Remove(id);
            ScheduleStateSave();
            UpdateAllRevisionAccents();
            return Task.CompletedTask;
        }));
        menu.ItemsSource = items.ToArray();
        return menu;
    }

    private void SetRevisionLevel(int level)
    {
        _revisionLevel = Math.Clamp(level, 0, 5);
        if (_revisionPicker is not null) _revisionPicker.SelectedIndex = _revisionLevel;
        UpdateAllRevisionAccents();
    }

    private void SetSelectedLabelColor(string? color)
    {
        var node = _viewModel.SelectedRow?.Node;
        if (node is null) return;
        if (string.IsNullOrWhiteSpace(color)) _labelColors.Remove(node.PersistentId);
        else _labelColors[node.PersistentId] = color;
        ScheduleStateSave();
        ApplyLabelColorsToCorkboard();
        if (_scriveningEditors.TryGetValue(node.PersistentId, out var state)) UpdateScriveningVisuals(state);
    }

    private void ApplyLabelColorsToCorkboard()
    {
        if (_corkboardPanel is null) return;
        var cards = _viewModel.CorkboardCards.ToArray();
        var borders = _corkboardPanel.Children.OfType<Border>().ToArray();
        var count = Math.Min(cards.Length, borders.Length);
        for (var index = 0; index < count; index++)
        {
            borders[index].BorderBrush = LabelBrush(cards[index].Node);
            borders[index].BorderThickness = new Thickness(2, 1, 1, 1);
        }
    }

    private SolidColorBrush LabelBrush(ProjectNode node)
    {
        if (_labelColors.TryGetValue(node.PersistentId, out var explicitColor))
            return new SolidColorBrush(Color.Parse(explicitColor));
        if (string.IsNullOrWhiteSpace(node.Label))
            return new SolidColorBrush(Color.Parse("#64748B"));
        var index = StablePaletteIndex(node.Label, LabelPalette.Length);
        return new SolidColorBrush(Color.Parse(LabelPalette[index]));
    }

    private SolidColorBrush SurfaceTint(ProjectNode node)
    {
        var color = LabelBrush(node).Color;
        return new SolidColorBrush(Color.FromArgb(18, color.R, color.G, color.B));
    }

    private static SolidColorBrush RevisionBrush(int level)
        => new(Color.Parse(RevisionPalette[Math.Clamp(level, 0, RevisionPalette.Length - 1)]));

    private static int StablePaletteIndex(string value, int count)
    {
        uint hash = 2166136261;
        foreach (var ch in value)
        {
            hash ^= char.ToUpperInvariant(ch);
            hash *= 16777619;
        }
        return (int)(hash % (uint)Math.Max(1, count));
    }

    private void ShowScrivenings()
    {
        if (_centerTabs is null || _scriveningsTab is null) return;
        _centerTabs.SelectedItem = _scriveningsTab;
        _ = ReloadScriveningsAsync(force: false);
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var primary = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!primary) return;
        if (e.Key == Key.G)
        {
            e.Handled = true;
            await ShowGoToAsync();
        }
        else if (e.Key == Key.B && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            await AddBookmarkAsync();
        }
    }

    private async Task BackupProjectAsync(bool force)
    {
        if (_backupRunning || _repository.CurrentProject is not { } project) return;
        _backupRunning = true;
        try
        {
            await FlushAllScriveningSavesAsync();
            try { await _viewModel.SaveNowAsync(); } catch { }

            var newest = NewestProjectWriteUtc(project.RootPath);
            if (!force && newest <= _lastBackupUtc) return;

            var backupDirectory = Path.Combine(project.RootPath, ".typescribe", "backups");
            Directory.CreateDirectory(backupDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var destination = Path.Combine(backupDirectory, $"{SafeFileName(project.Title)}-{stamp}.zip");

            await Task.Run(() => CreateProjectArchive(project.RootPath, destination));
            _lastBackupUtc = DateTime.UtcNow;
            PruneBackups(backupDirectory, 10);
        }
        catch { }
        finally { _backupRunning = false; }
    }

    private static void CreateProjectArchive(string root, string destination)
    {
        using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (ShouldSkipBackupPath(relative)) continue;
            var entry = archive.CreateEntry(relative.Replace('\\', '/'), CompressionLevel.Fastest);
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = entry.Open();
            input.CopyTo(output);
        }
    }

    private static bool ShouldSkipBackupPath(string relative)
    {
        var normalized = relative.Replace('\\', '/');
        return normalized.StartsWith("build/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(".typescribe/backups/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime NewestProjectWriteUtc(string root)
    {
        var newest = DateTime.MinValue;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                if (ShouldSkipBackupPath(relative)) continue;
                var write = File.GetLastWriteTimeUtc(file);
                if (write > newest) newest = write;
            }
        }
        catch { }
        return newest;
    }

    private static DateTime NewestBackupUtc(string root)
    {
        var directory = Path.Combine(root, ".typescribe", "backups");
        if (!Directory.Exists(directory)) return DateTime.MinValue;
        try
        {
            return Directory.EnumerateFiles(directory, "*.zip")
                .Select(File.GetLastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
        }
        catch { return DateTime.MinValue; }
    }

    private static void PruneBackups(string directory, int keep)
    {
        try
        {
            foreach (var file in new DirectoryInfo(directory).GetFiles("*.zip")
                         .OrderByDescending(static file => file.LastWriteTimeUtc)
                         .Skip(Math.Max(1, keep)))
                file.Delete();
        }
        catch { }
    }

    private void OpenBackupsFolder()
    {
        var project = _repository.CurrentProject;
        if (project is null) return;
        var directory = Path.Combine(project.RootPath, ".typescribe", "backups");
        Directory.CreateDirectory(directory);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void LoadWorkspaceState()
    {
        _bookmarks.Clear();
        _revisionLines.Clear();
        _labelColors.Clear();
        if (string.IsNullOrWhiteSpace(_workspaceStatePath) || !File.Exists(_workspaceStatePath)) return;
        try
        {
            foreach (var line in File.ReadLines(_workspaceStatePath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
                var parts = line.Split('\t');
                if (parts.Length == 0) continue;
                switch (parts[0])
                {
                    // Legacy collapsed Binder entries are intentionally ignored. ProjectExplorer
                    // stores expansion state independently in project-explorer.tsv.
                    case "labelcolor" when parts.Length >= 3:
                        _labelColors[parts[1]] = parts[2];
                        break;
                    case "revision" when parts.Length >= 4 && int.TryParse(parts[2], out var lineNumber) && int.TryParse(parts[3], out var level):
                        RecordRevisionLoaded(parts[1], lineNumber, level);
                        break;
                    case "bookmark" when parts.Length >= 9:
                        if (!int.TryParse(parts[3], out var offset)) offset = 0;
                        if (!int.TryParse(parts[4], out var bookmarkLine)) bookmarkLine = 1;
                        if (!int.TryParse(parts[5], out var column)) column = 1;
                        if (!int.TryParse(parts[8], out var anchorOffset)) anchorOffset = 0;
                        _bookmarks.Add(new BookmarkEntry(parts[1], parts[2], Decode(parts[6]), offset, bookmarkLine, column, Decode(parts[7]), anchorOffset));
                        break;
                }
            }
        }
        catch { }
    }

    private void RecordRevisionLoaded(string persistentId, int line, int level)
    {
        if (!_revisionLines.TryGetValue(persistentId, out var lines))
        {
            lines = new Dictionary<int, int>();
            _revisionLines[persistentId] = lines;
        }
        lines[Math.Max(1, line)] = Math.Clamp(level, 1, 5);
    }

    private void ScheduleStateSave()
    {
        _stateSavePending = true;
        _stateSaveTimer.Stop();
        _stateSaveTimer.Start();
    }

    private void SaveWorkspaceState()
    {
        if (string.IsNullOrWhiteSpace(_workspaceStatePath)) return;
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("# Typescribe workspace state v2");
            foreach (var pair in _labelColors.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                builder.Append("labelcolor\t").Append(pair.Key).Append('\t').Append(pair.Value).AppendLine();
            foreach (var document in _revisionLines.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                foreach (var revision in document.Value.OrderBy(static pair => pair.Key))
                    builder.Append("revision\t").Append(document.Key).Append('\t').Append(revision.Key).Append('\t').Append(revision.Value).AppendLine();
            foreach (var bookmark in _bookmarks)
            {
                builder.Append("bookmark\t").Append(bookmark.Id).Append('\t').Append(bookmark.PersistentId).Append('\t')
                    .Append(bookmark.Offset).Append('\t').Append(bookmark.Line).Append('\t').Append(bookmark.Column).Append('\t')
                    .Append(Encode(bookmark.Label)).Append('\t').Append(Encode(bookmark.Anchor)).Append('\t')
                    .Append(bookmark.AnchorOffset).AppendLine();
            }

            var directory = Path.GetDirectoryName(_workspaceStatePath)!;
            Directory.CreateDirectory(directory);
            var temp = _workspaceStatePath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, builder.ToString(), new UTF8Encoding(false));
            File.Move(temp, _workspaceStatePath, overwrite: true);
        }
        catch { }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _backupTimer.Stop();
        _stateSaveTimer.Stop();
        _viewModel.StateChanged -= OnViewModelStateChanged;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.KeyDown -= OnKeyDown;
        _window.Closed -= OnClosed;
        _previewRefreshCts?.Cancel();
        _previewRefreshCts?.Dispose();
        _splitSaveCts?.Cancel();
        _splitSaveCts?.Dispose();
        foreach (var cts in _saveTimers.Values) { cts.Cancel(); cts.Dispose(); }
        _saveTimers.Clear();
        if (_stateSavePending) SaveWorkspaceState();
    }

    private static MenuItem Command(string title, Func<Task> action, KeyGesture? gesture = null)
    {
        var item = new MenuItem { Header = title, InputGesture = gesture };
        item.Click += async (_, _) =>
        {
            try { await action(); }
            catch { }
        };
        return item;
    }

    private static List<object> MenuItems(object? itemsSource)
        => itemsSource is IEnumerable<object> items ? items.ToList() : [];

    private static bool HeaderEquals(MenuItem item, string text)
        => string.Equals((item.Header?.ToString() ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal), text, StringComparison.OrdinalIgnoreCase);

    private static List<TabItem> TabItems(TabControl tabs)
        => tabs.ItemsSource is IEnumerable<object> objects
            ? objects.OfType<TabItem>().ToList()
            : tabs.Items.OfType<TabItem>().ToList();

    private static KeyModifiers PrimaryModifier()
        => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private static int LineFromCaret(TextBox editor)
        => LineColumn(editor.Text ?? string.Empty, Math.Clamp(editor.CaretIndex, 0, (editor.Text ?? string.Empty).Length)).Line;

    private static (int Line, int Column) LineColumn(string text, int offset)
    {
        var line = 1;
        var column = 1;
        for (var index = 0; index < Math.Clamp(offset, 0, text.Length); index++)
        {
            if (text[index] == '\n') { line++; column = 1; }
            else column++;
        }
        return (line, column);
    }

    private static (string Anchor, int Offset) AnchorAt(string text, int caret)
    {
        if (text.Length == 0) return (string.Empty, 0);
        var start = Math.Max(0, caret - 28);
        var end = Math.Min(text.Length, caret + 28);
        return (text[start..end], caret - start);
    }

    private static int ResolveBookmarkPosition(BookmarkEntry bookmark, string text)
    {
        if (!string.IsNullOrEmpty(bookmark.Anchor))
        {
            var index = text.IndexOf(bookmark.Anchor, StringComparison.Ordinal);
            if (index >= 0) return Math.Clamp(index + bookmark.AnchorOffset, 0, text.Length);
        }
        var line = 1;
        var indexAtLine = 0;
        while (line < bookmark.Line && indexAtLine < text.Length)
        {
            var next = text.IndexOf('\n', indexAtLine);
            if (next < 0) break;
            indexAtLine = next + 1;
            line++;
        }
        return Math.Clamp(indexAtLine + Math.Max(0, bookmark.Column - 1), 0, text.Length);
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0) return 1;
        var lines = 1;
        foreach (var ch in text) if (ch == '\n') lines++;
        return lines;
    }

    private static int CountWords(string text)
    {
        var count = 0;
        var inWord = false;
        foreach (var ch in text)
        {
            var word = char.IsLetterOrDigit(ch) || ch is '\'' or '’';
            if (word && !inWord) count++;
            inWord = word;
        }
        return count;
    }

    private static string Encode(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));

    private static string Decode(string value)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        catch { return string.Empty; }
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) ? "Typescribe" : result;
    }

    private sealed class ScriveningDocumentState(
        BookProject project,
        BinderRowViewModel row,
        Border container,
        TextBox editor,
        Border accent,
        TextBlock revision,
        TextBlock words,
        string lastSavedText)
    {
        public BookProject Project { get; } = project;
        public BinderRowViewModel Row { get; } = row;
        public Border Container { get; } = container;
        public TextBox Editor { get; } = editor;
        public Border Accent { get; } = accent;
        public TextBlock Revision { get; } = revision;
        public TextBlock Words { get; } = words;
        public string LastSavedText { get; set; } = lastSavedText;
        public bool Dirty { get; set; }
    }

    private sealed record BookmarkEntry(
        string Id,
        string PersistentId,
        string Label,
        int Offset,
        int Line,
        int Column,
        string Anchor,
        int AnchorOffset)
    {
        public string? DisplayTitle { get; init; }
        public override string ToString() => DisplayTitle ?? Label;
    }

    private sealed record DocumentChoice(string PersistentId, string Title)
    {
        public override string ToString() => Title;
    }

    private sealed record GoToEntry(string PersistentId, string Title, string RelativePath)
    {
        public override string ToString() => string.IsNullOrWhiteSpace(RelativePath) ? Title : $"{Title}   {RelativePath}";
    }
}
