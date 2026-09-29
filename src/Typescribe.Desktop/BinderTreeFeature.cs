using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.Editing;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

/// <summary>
/// Single visual owner for the Binder.
///
/// Older workspace features still assign Binder ItemsSource/ItemTemplate as part of their
/// state refreshes. This owner pins the real tree at Animation priority, which is intentionally
/// higher than those LocalValue assignments, so they never become effective values and cannot
/// tear down ListBox containers between frames. Selection remains a normal DirectProperty; a
/// short-lived pending-user-selection id prevents an asynchronous state refresh from reverting
/// a click before WorkspaceViewModel.SelectAsync has finished.
/// </summary>
internal sealed class BinderTreeFeature
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000d / 60d);
    private static readonly string[] LabelPalette =
    [
        "#64748B", "#3B82F6", "#8B5CF6", "#F43F5E",
        "#F59E0B", "#10B981", "#06B6D4", "#EC4899"
    ];

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly TrackingProjectRepository _repository;
    private readonly ObservableCollection<BinderRowViewModel> _visibleRows = [];
    private readonly HashSet<string> _collapsedNodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedHeadingDocuments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OutlineItemViewModel[]> _headingIndex = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _frameTimer;

    private ListBox? _binder;
    private TabControl? _leftTabs;
    private TabControl? _centerTabs;
    private TabItem? _editorTab;
    private FuncDataTemplate<BinderRowViewModel>? _rowTemplate;
    private TextBox? _titleEditor;
    private CancellationTokenSource? _indexCts;
    private IDisposable? _itemsSourceLease;
    private IDisposable? _itemTemplateLease;
    private string? _projectRoot;
    private string? _statePath;
    private string? _selectedPersistentId;
    private string? _pendingUserSelectionId;
    private int _bookmarkSelectedIndex = -1;
    private ListBox? _bookmarkList;
    private bool _frameScheduled;
    private bool _titleCommitRunning;
    private bool _indexing;
    private bool _disposed;

    private BinderTreeFeature(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository)
    {
        _window = window;
        _viewModel = viewModel;
        _repository = repository;
        _frameTimer = new DispatcherTimer { Interval = FrameInterval };
        _frameTimer.Tick += OnFrameTick;
    }

    public static void Apply(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(repository);

        var feature = new BinderTreeFeature(window, viewModel, repository);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        viewModel.StateChanged += feature.OnStateChanged;
        viewModel.BinderRows.CollectionChanged += feature.OnCanonicalRowsChanged;

        feature.TryAttach();
        feature.UpdateProjectState();
        feature.ScheduleFrame();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        TryAttach();
        UpdateProjectState();
        ScheduleFrame();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (_binder is null || _centerTabs is null || _leftTabs is null || _bookmarkList is null)
            TryAttach();
    }

    private void OnStateChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            TryAttach();
            UpdateProjectState();
            RefreshSelectedHeadingCache();

            var viewModelId = _viewModel.SelectedRow?.Node.PersistentId;
            if (_pendingUserSelectionId is not null)
            {
                if (string.Equals(viewModelId, _pendingUserSelectionId, StringComparison.Ordinal))
                {
                    _selectedPersistentId = _pendingUserSelectionId;
                    _pendingUserSelectionId = null;
                }
                // Otherwise the click is still in flight. Do not let an unrelated state update
                // replace the user's visible selection with the previous ViewModel selection.
            }
            else
            {
                _selectedPersistentId = viewModelId;
            }

            EnsureSelectedAncestorsExpanded();
            SynchronizeTitleEditor();
            ScheduleFrame();
            _ = IndexMissingDocumentsAsync();
        }, DispatcherPriority.Background);

    private void OnCanonicalRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed) return;
        if (_selectedPersistentId is not null && !_viewModel.BinderRows.Any(row =>
                string.Equals(row.Node.PersistentId, _selectedPersistentId, StringComparison.Ordinal)))
        {
            _selectedPersistentId = _viewModel.SelectedRow?.Node.PersistentId;
            _pendingUserSelectionId = null;
        }
        ScheduleFrame();
        _ = IndexMissingDocumentsAsync();
    }

    private void TryAttach()
    {
        if (_disposed) return;
        if (_window.Content is not Control content) return;

        _binder ??= EnumerateControls(content)
            .OfType<ListBox>()
            .FirstOrDefault(static list => list.ContextMenu is not null);

        if (_binder is not null && !_binder.Classes.Contains("binder-tree-v3"))
            AttachBinder(_binder);

        if (_leftTabs is null || _centerTabs is null)
        {
            foreach (var tabs in EnumerateControls(content).OfType<TabControl>())
            {
                var items = TabItems(tabs).ToArray();
                var headers = items.Select(static item => item.Header?.ToString() ?? string.Empty).ToArray();
                if (_leftTabs is null && headers.Contains("Binder", StringComparer.Ordinal))
                    _leftTabs = tabs;
                if (_centerTabs is null && headers.Contains("Editor", StringComparer.Ordinal) && headers.Contains("Corkboard", StringComparer.Ordinal))
                {
                    _centerTabs = tabs;
                    _editorTab = items.FirstOrDefault(static item => string.Equals(item.Header?.ToString(), "Editor", StringComparison.Ordinal));
                }
            }
        }

        EnsureEditableDocumentTitle();
        AttachBookmarkSelectionGuard();

        if (_binder is not null && _centerTabs is not null && _leftTabs is not null && _bookmarkList is not null)
            _window.LayoutUpdated -= OnLayoutUpdated;
    }

    private void AttachBinder(ListBox binder)
    {
        if (!binder.Classes.Contains("binder-tree-v3")) binder.Classes.Add("binder-tree-v3");
        if (!binder.Classes.Contains("binder-list")) binder.Classes.Add("binder-list");
        binder.SelectionMode = SelectionMode.Single;
        binder.Focusable = true;
        binder.AutoScrollToSelectedItem = false;

        _rowTemplate = new FuncDataTemplate<BinderRowViewModel>(
            (row, _) => new BinderTreeRowControl(this, row),
            supportsRecycling: false);

        _selectedPersistentId = (binder.SelectedItem as BinderRowViewModel)?.Node.PersistentId
                                ?? _viewModel.SelectedRow?.Node.PersistentId;
        ReconcileVisibleRows(BuildVisibleRows());

        // ItemsSource and ItemTemplate are StyledProperties. Animation is Avalonia's highest
        // property priority, so legacy LocalValue writes remain stored but never become visible.
        _itemsSourceLease?.Dispose();
        _itemTemplateLease?.Dispose();
        _itemsSourceLease = binder.SetValue(
            ItemsControl.ItemsSourceProperty,
            (IEnumerable)_visibleRows,
            BindingPriority.Animation);
        _itemTemplateLease = binder.SetValue(
            ItemsControl.ItemTemplateProperty,
            (IDataTemplate)_rowTemplate,
            BindingPriority.Animation);

        binder.AddHandler(InputElement.PointerPressedEvent, BinderPointerPressedTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        binder.SelectionChanged += BinderSelectionChanged;
        binder.DoubleTapped += BinderDoubleTapped;
        binder.KeyDown += BinderKeyDown;
        SynchronizeSelection();
    }

    private void BinderPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (_disposed || _binder is null) return;
        var point = e.GetCurrentPoint(_binder);
        if (!point.Properties.IsLeftButtonPressed) return;

        if (IsBinderActionButton(e.Source)) return;
        var row = FindRowFromSource(e.Source);
        if (row is null) return;

        _pendingUserSelectionId = row.Node.PersistentId;
        _selectedPersistentId = row.Node.PersistentId;
    }

    private static bool IsBinderActionButton(object? source)
    {
        if (source is not Control control) return false;
        if (control is Button self &&
            (self.Classes.Contains("binder-disclosure") || self.Classes.Contains("binder-heading")))
            return true;

        return control.GetVisualAncestors().OfType<Button>().Any(button =>
            button.Classes.Contains("binder-disclosure") || button.Classes.Contains("binder-heading"));
    }

    private static BinderRowViewModel? FindRowFromSource(object? source)
    {
        if (source is not Control control) return null;
        if (control is ListBoxItem self && self.Content is BinderRowViewModel direct) return direct;
        return control.GetVisualAncestors()
            .OfType<ListBoxItem>()
            .Select(static item => item.Content)
            .OfType<BinderRowViewModel>()
            .FirstOrDefault();
    }

    private void BinderSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_binder?.SelectedItem is not BinderRowViewModel row) return;
        var selectedId = row.Node.PersistentId;
        var viewModelId = _viewModel.SelectedRow?.Node.PersistentId;

        if (_pendingUserSelectionId is not null &&
            !string.Equals(selectedId, _pendingUserSelectionId, StringComparison.Ordinal) &&
            !string.Equals(viewModelId, _pendingUserSelectionId, StringComparison.Ordinal))
        {
            // StudioWorkspaceWindow may re-assert the previous ViewModel selection while its
            // asynchronous selection handler is still flushing metadata. Keep the click pending
            // and restore it on the next frame instead of accepting that transient reversion.
            ScheduleFrame();
            return;
        }

        _selectedPersistentId = selectedId;
        if (string.Equals(selectedId, _pendingUserSelectionId, StringComparison.Ordinal) &&
            string.Equals(viewModelId, selectedId, StringComparison.Ordinal))
            _pendingUserSelectionId = null;
    }

    private void BinderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_binder?.SelectedItem is not BinderRowViewModel row || !row.Node.IsContainer) return;
        ToggleContainer(row, selectContainer: true);
        e.Handled = true;
    }

    private void BinderKeyDown(object? sender, KeyEventArgs e)
    {
        if (_binder?.SelectedItem is not BinderRowViewModel row) return;

        if (e.Key == Key.Right && row.Node.IsContainer && _collapsedNodes.Contains(row.Node.PersistentId))
        {
            _collapsedNodes.Remove(row.Node.PersistentId);
            SaveState();
            ScheduleFrame();
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            if (row.Node.IsContainer && !_collapsedNodes.Contains(row.Node.PersistentId))
            {
                _collapsedNodes.Add(row.Node.PersistentId);
                SaveState();
                ScheduleFrame();
                e.Handled = true;
            }
            else
            {
                var parent = FindParent(row);
                if (parent is not null)
                {
                    _pendingUserSelectionId = parent.Node.PersistentId;
                    _selectedPersistentId = parent.Node.PersistentId;
                    _binder.SelectedItem = parent;
                    _binder.ScrollIntoView(parent);
                    e.Handled = true;
                }
            }
        }
    }

    private BinderRowViewModel? FindParent(BinderRowViewModel row)
    {
        var canonical = _viewModel.BinderRows;
        var index = canonical.IndexOf(row);
        if (index < 0 || row.Depth <= 0) return null;
        for (var i = index - 1; i >= 0; i--)
            if (canonical[i].Depth < row.Depth)
                return canonical[i];
        return null;
    }

    internal void ToggleDisclosure(BinderRowViewModel row, BinderTreeRowControl visual)
    {
        if (row.Node.IsContainer)
        {
            ToggleContainer(row, selectContainer: false);
            visual.Refresh();
            return;
        }

        if (!row.Node.IsDocument || !HasHeadings(row.Node.PersistentId)) return;
        if (!_expandedHeadingDocuments.Add(row.Node.PersistentId))
            _expandedHeadingDocuments.Remove(row.Node.PersistentId);
        SaveState();
        visual.Refresh();
    }

    private void ToggleContainer(BinderRowViewModel row, bool selectContainer)
    {
        if (!row.Node.IsContainer) return;
        var willCollapse = !_collapsedNodes.Contains(row.Node.PersistentId);
        if (willCollapse)
        {
            _collapsedNodes.Add(row.Node.PersistentId);
            if (selectContainer || IsSelectionDescendantOf(row))
            {
                _pendingUserSelectionId = row.Node.PersistentId;
                _selectedPersistentId = row.Node.PersistentId;
                if (_binder is not null) _binder.SelectedItem = row;
            }
        }
        else
        {
            _collapsedNodes.Remove(row.Node.PersistentId);
        }

        SaveState();
        ScheduleFrame();
    }

    private bool IsSelectionDescendantOf(BinderRowViewModel parent)
    {
        var selectedId = _pendingUserSelectionId ?? _selectedPersistentId;
        if (selectedId is null) return false;
        var rows = _viewModel.BinderRows;
        var parentIndex = rows.IndexOf(parent);
        if (parentIndex < 0) return false;
        for (var i = parentIndex + 1; i < rows.Count && rows[i].Depth > parent.Depth; i++)
            if (string.Equals(rows[i].Node.PersistentId, selectedId, StringComparison.Ordinal))
                return true;
        return false;
    }

    internal bool IsCollapsed(BinderRowViewModel row)
        => row.Node.IsContainer && _collapsedNodes.Contains(row.Node.PersistentId);

    internal bool IsHeadingExpanded(BinderRowViewModel row)
        => row.Node.IsDocument && _expandedHeadingDocuments.Contains(row.Node.PersistentId);

    internal IReadOnlyList<OutlineItemViewModel> HeadingsFor(BinderRowViewModel row)
        => _headingIndex.TryGetValue(row.Node.PersistentId, out var headings) ? headings : [];

    internal bool HasHeadings(string persistentId)
        => _headingIndex.TryGetValue(persistentId, out var headings) && headings.Length > 0;

    internal async Task NavigateToHeadingAsync(BinderRowViewModel owner, OutlineItemViewModel heading)
    {
        try
        {
            var canonical = _viewModel.BinderRows.FirstOrDefault(row =>
                string.Equals(row.Node.PersistentId, owner.Node.PersistentId, StringComparison.Ordinal));
            if (canonical is null) return;

            _pendingUserSelectionId = canonical.Node.PersistentId;
            _selectedPersistentId = canonical.Node.PersistentId;
            if (!ReferenceEquals(_viewModel.SelectedRow, canonical))
                await _viewModel.SelectAsync(canonical);

            _pendingUserSelectionId = null;
            var liveHeading = _viewModel.OutlineItems.FirstOrDefault(item =>
                item.SourceLine == heading.SourceLine &&
                string.Equals(item.Title, heading.Title, StringComparison.Ordinal)) ?? heading;
            _viewModel.SelectOutline(liveHeading);
            if (_centerTabs is not null && _editorTab is not null)
                _centerTabs.SelectedItem = _editorTab;
        }
        catch
        {
            // Heading navigation should never destabilize the Binder.
        }
    }

    private void ScheduleFrame()
    {
        if (_disposed || _frameScheduled) return;
        _frameScheduled = true;
        _frameTimer.Start();
    }

    private void OnFrameTick(object? sender, EventArgs e)
    {
        _frameTimer.Stop();
        _frameScheduled = false;
        if (_disposed) return;

        EnsureSelectedAncestorsExpanded();
        ReconcileVisibleRows(BuildVisibleRows());
        SynchronizeSelection();
    }

    private BinderRowViewModel[] BuildVisibleRows()
    {
        var result = new List<BinderRowViewModel>(_viewModel.BinderRows.Count);
        int? collapsedDepth = null;

        foreach (var row in _viewModel.BinderRows)
        {
            if (collapsedDepth is { } depth)
            {
                if (row.Depth > depth) continue;
                collapsedDepth = null;
            }

            result.Add(row);
            if (row.Node.IsContainer && _collapsedNodes.Contains(row.Node.PersistentId))
                collapsedDepth = row.Depth;
        }

        return result.ToArray();
    }

    private void ReconcileVisibleRows(IReadOnlyList<BinderRowViewModel> target)
    {
        for (var index = 0; index < target.Count; index++)
        {
            var desired = target[index];
            if (index < _visibleRows.Count && SameNode(_visibleRows[index], desired))
            {
                if (!ReferenceEquals(_visibleRows[index], desired))
                    _visibleRows[index] = desired;
                continue;
            }

            var existingIndex = FindVisibleIndex(desired.Node.PersistentId, index + 1);
            if (existingIndex >= 0)
            {
                _visibleRows.Move(existingIndex, index);
                if (!ReferenceEquals(_visibleRows[index], desired))
                    _visibleRows[index] = desired;
            }
            else
            {
                _visibleRows.Insert(index, desired);
            }
        }

        while (_visibleRows.Count > target.Count)
            _visibleRows.RemoveAt(_visibleRows.Count - 1);
    }

    private int FindVisibleIndex(string persistentId, int start)
    {
        for (var index = Math.Max(0, start); index < _visibleRows.Count; index++)
            if (string.Equals(_visibleRows[index].Node.PersistentId, persistentId, StringComparison.Ordinal))
                return index;
        return -1;
    }

    private static bool SameNode(BinderRowViewModel left, BinderRowViewModel right)
        => string.Equals(left.Node.PersistentId, right.Node.PersistentId, StringComparison.Ordinal);

    private void SynchronizeSelection()
    {
        if (_binder is null) return;
        var targetId = _pendingUserSelectionId ?? _selectedPersistentId ?? _viewModel.SelectedRow?.Node.PersistentId;
        if (targetId is null) return;

        var target = _visibleRows.FirstOrDefault(row =>
            string.Equals(row.Node.PersistentId, targetId, StringComparison.Ordinal));
        if (target is null) return;

        _selectedPersistentId = targetId;
        if (!ReferenceEquals(_binder.SelectedItem, target))
            _binder.SelectedItem = target;
    }

    private void EnsureSelectedAncestorsExpanded()
    {
        var targetId = _pendingUserSelectionId ?? _selectedPersistentId ?? _viewModel.SelectedRow?.Node.PersistentId;
        if (targetId is null) return;
        var rows = _viewModel.BinderRows;
        var selected = rows.FirstOrDefault(row => string.Equals(row.Node.PersistentId, targetId, StringComparison.Ordinal));
        if (selected is null || selected.Depth <= 0) return;

        var index = rows.IndexOf(selected);
        if (index < 0) return;
        var depth = selected.Depth;
        var changed = false;
        for (var i = index - 1; i >= 0 && depth > 0; i--)
        {
            var candidate = rows[i];
            if (candidate.Depth >= depth) continue;
            depth = candidate.Depth;
            if (candidate.Node.IsContainer)
                changed |= _collapsedNodes.Remove(candidate.Node.PersistentId);
        }
        if (changed) SaveState();
    }

    private void RefreshRealizedRows(string? persistentId = null)
    {
        if (_binder is null) return;
        foreach (var visual in _binder.GetVisualDescendants().OfType<BinderTreeRowControl>().ToArray())
        {
            if (persistentId is null || string.Equals(visual.PersistentId, persistentId, StringComparison.Ordinal))
                visual.Refresh();
        }
    }

    private void UpdateProjectState()
    {
        var project = _repository.CurrentProject;
        if (project is null)
        {
            if (_projectRoot is null) return;
            _projectRoot = null;
            _statePath = null;
            _collapsedNodes.Clear();
            _expandedHeadingDocuments.Clear();
            _headingIndex.Clear();
            _pendingUserSelectionId = null;
            CancelIndexing();
            return;
        }

        if (string.Equals(_projectRoot, project.RootPath, StringComparison.Ordinal)) return;

        _projectRoot = project.RootPath;
        _statePath = Path.Combine(project.RootPath, ".typescribe", "binder-tree.tsv");
        _collapsedNodes.Clear();
        _expandedHeadingDocuments.Clear();
        _headingIndex.Clear();
        _pendingUserSelectionId = null;
        LoadState(project.RootPath);
        CancelIndexing();
        _indexCts = new CancellationTokenSource();
        _ = IndexMissingDocumentsAsync();
    }

    private void LoadState(string projectRoot)
    {
        var loaded = false;
        if (_statePath is not null && File.Exists(_statePath))
        {
            try
            {
                foreach (var line in File.ReadLines(_statePath, Encoding.UTF8))
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 2) continue;
                    if (string.Equals(parts[0], "collapsed", StringComparison.Ordinal))
                        _collapsedNodes.Add(parts[1]);
                    else if (string.Equals(parts[0], "headings-open", StringComparison.Ordinal))
                        _expandedHeadingDocuments.Add(parts[1]);
                }
                loaded = true;
            }
            catch
            {
            }
        }

        if (loaded) return;

        var workspace = Path.Combine(projectRoot, ".typescribe", "workspace.tsv");
        if (File.Exists(workspace))
        {
            try
            {
                foreach (var line in File.ReadLines(workspace, Encoding.UTF8))
                {
                    var parts = line.Split('\t');
                    if (parts.Length >= 2 && string.Equals(parts[0], "collapsed", StringComparison.Ordinal))
                        _collapsedNodes.Add(parts[1]);
                }
            }
            catch
            {
            }
        }

        var oldBinderState = Path.Combine(projectRoot, ".typescribe", "binder-ui.tsv");
        if (File.Exists(oldBinderState))
        {
            try
            {
                foreach (var line in File.ReadLines(oldBinderState, Encoding.UTF8))
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

        SaveState();
    }

    private void SaveState()
    {
        if (string.IsNullOrWhiteSpace(_statePath)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var builder = new StringBuilder();
            foreach (var id in _collapsedNodes.Order(StringComparer.Ordinal))
                builder.Append("collapsed\t").Append(id).AppendLine();
            foreach (var id in _expandedHeadingDocuments.Order(StringComparer.Ordinal))
                builder.Append("headings-open\t").Append(id).AppendLine();
            File.WriteAllText(_statePath, builder.ToString(), Encoding.UTF8);
        }
        catch
        {
            // UI state persistence must never make the Binder unusable.
        }
    }

    private async Task IndexMissingDocumentsAsync()
    {
        if (_disposed || _indexing || _repository.CurrentProject is not { } project) return;
        _indexCts ??= new CancellationTokenSource();
        var token = _indexCts.Token;
        _indexing = true;
        try
        {
            foreach (var row in _viewModel.BinderRows.Where(static row => row.Node.IsDocument).ToArray())
            {
                token.ThrowIfCancellationRequested();
                if (_headingIndex.ContainsKey(row.Node.PersistentId)) continue;
                string text;
                try
                {
                    text = await _repository.ReadDocumentAsync(project, row.Node, token);
                }
                catch when (!token.IsCancellationRequested)
                {
                    _headingIndex[row.Node.PersistentId] = [];
                    continue;
                }

                var headings = ParseHeadings(text);
                _headingIndex[row.Node.PersistentId] = headings;
                if (headings.Length > 0 && string.Equals(_viewModel.SelectedRow?.Node.PersistentId, row.Node.PersistentId, StringComparison.Ordinal))
                    _expandedHeadingDocuments.Add(row.Node.PersistentId);
                await Dispatcher.UIThread.InvokeAsync(() => RefreshRealizedRows(row.Node.PersistentId));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            _indexing = false;
        }
    }

    private void RefreshSelectedHeadingCache()
    {
        var selected = _viewModel.SelectedRow;
        if (selected?.Node.IsDocument != true) return;

        var headings = _viewModel.OutlineItems.ToArray();
        var id = selected.Node.PersistentId;
        var changed = !_headingIndex.TryGetValue(id, out var old) || !HeadingSetsEqual(old, headings);
        _headingIndex[id] = headings;
        if (headings.Length > 0 && changed)
            _expandedHeadingDocuments.Add(id);
        if (changed)
        {
            SaveState();
            RefreshRealizedRows(id);
        }
    }

    private static bool HeadingSetsEqual(OutlineItemViewModel[] left, OutlineItemViewModel[] right)
    {
        if (left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i].Level != right[i].Level || left[i].SourceLine != right[i].SourceLine ||
                !string.Equals(left[i].Title, right[i].Title, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static OutlineItemViewModel[] ParseHeadings(string text)
    {
        var source = (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = source.Split('\n');
        var pending = new List<(string Title, int Level, int SourceLine)>();
        var inFence = false;
        char fenceCharacter = '\0';

        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                var marker = trimmed[0];
                if (!inFence)
                {
                    inFence = true;
                    fenceCharacter = marker;
                }
                else if (marker == fenceCharacter)
                {
                    inFence = false;
                    fenceCharacter = '\0';
                }
                continue;
            }
            if (inFence || trimmed.Length < 2 || trimmed[0] != '#') continue;

            var level = 0;
            while (level < trimmed.Length && level < 6 && trimmed[level] == '#') level++;
            if (level == 0 || level >= trimmed.Length || !char.IsWhiteSpace(trimmed[level])) continue;

            var title = trimmed[(level + 1)..].Trim();
            while (title.EndsWith('#')) title = title[..^1].TrimEnd();
            if (title.Length == 0) continue;
            pending.Add((title, level, index + 1));
        }

        var result = new OutlineItemViewModel[pending.Count];
        for (var i = 0; i < pending.Count; i++)
        {
            var current = pending[i];
            var endLine = lines.Length;
            for (var j = i + 1; j < pending.Count; j++)
            {
                if (pending[j].Level > current.Level) continue;
                endLine = Math.Max(current.SourceLine, pending[j].SourceLine - 1);
                break;
            }
            result[i] = new OutlineItemViewModel(current.Title, current.Level, current.SourceLine, endLine);
        }
        return result;
    }

    private void CancelIndexing()
    {
        _indexCts?.Cancel();
        _indexCts?.Dispose();
        _indexCts = null;
        _indexing = false;
    }

    private void EnsureEditableDocumentTitle()
    {
        if (_titleEditor is not null || _editorTab?.Content is not Grid editorPanel) return;
        var header = editorPanel.Children.OfType<Grid>().FirstOrDefault(static grid => Grid.GetRow(grid) == 0);
        if (header is null) return;
        var original = header.Children.OfType<TextBlock>()
            .FirstOrDefault(block => Grid.GetColumn(block) == 0 && block.FontSize >= 17);
        if (original is null) return;

        var title = new TextBox
        {
            Text = _viewModel.HasSelection ? _viewModel.SelectedTitle : string.Empty,
            PlaceholderText = "Document title",
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
        header.Children.RemoveAt(index);
        header.Children.Insert(index, title);
        Grid.SetRow(title, Grid.GetRow(original));
        Grid.SetColumn(title, Grid.GetColumn(original));
        Grid.SetRowSpan(title, Grid.GetRowSpan(original));
        Grid.SetColumnSpan(title, Grid.GetColumnSpan(original));
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
        if (requested.Length == 0 || string.Equals(requested, _viewModel.SelectedTitle, StringComparison.Ordinal))
        {
            SynchronizeTitleEditor(force: true);
            return;
        }

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

    private void AttachBookmarkSelectionGuard()
    {
        if (_leftTabs is null || _bookmarkList is not null) return;
        var bookmarks = TabItems(_leftTabs).FirstOrDefault(static item =>
            string.Equals(item.Header?.ToString(), "Bookmarks", StringComparison.Ordinal));
        if (bookmarks?.Content is not Control content) return;
        _bookmarkList = EnumerateControls(content).OfType<ListBox>().FirstOrDefault();
        if (_bookmarkList is null) return;
        _bookmarkSelectedIndex = _bookmarkList.SelectedIndex;
        _bookmarkList.SelectionChanged += BookmarkSelectionChanged;
        _bookmarkList.PropertyChanged += BookmarkPropertyChanged;
    }

    private void BookmarkSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_bookmarkList?.SelectedIndex >= 0)
            _bookmarkSelectedIndex = _bookmarkList.SelectedIndex;
    }

    private void BookmarkPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_bookmarkList is null || e.Property != ItemsControl.ItemsSourceProperty || _bookmarkSelectedIndex < 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _bookmarkList is null || _bookmarkList.ItemCount <= 0 || _bookmarkList.SelectedIndex >= 0) return;
            _bookmarkList.SelectedIndex = Math.Clamp(_bookmarkSelectedIndex, 0, _bookmarkList.ItemCount - 1);
        }, DispatcherPriority.Background);
    }

    private static IEnumerable<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is IEnumerable source) return source.Cast<object?>().OfType<TabItem>();
        return tabs.Items.OfType<TabItem>();
    }

    private static IEnumerable<Control> EnumerateControls(Control root)
    {
        yield return root;
        if (root is Panel panel)
        {
            foreach (var child in panel.Children)
                foreach (var nested in EnumerateControls(child))
                    yield return nested;
        }
        if (root is ContentControl content && content.Content is Control contentChild)
        {
            foreach (var nested in EnumerateControls(contentChild))
                yield return nested;
        }
        if (root is Decorator decorator && decorator.Child is Control decoratedChild)
        {
            foreach (var nested in EnumerateControls(decoratedChild))
                yield return nested;
        }
    }

    internal static IBrush LabelBrush(ProjectNode node)
    {
        if (string.IsNullOrWhiteSpace(node.Label)) return Brushes.Transparent;
        uint hash = 2166136261;
        foreach (var ch in node.Label)
        {
            hash ^= char.ToUpperInvariant(ch);
            hash *= 16777619;
        }
        return new SolidColorBrush(Color.Parse(LabelPalette[hash % (uint)LabelPalette.Length]));
    }

    internal static string Icon(NodeKind kind) => kind switch
    {
        NodeKind.Book => "▣",
        NodeKind.Part => "◆",
        NodeKind.Folder => "▸",
        NodeKind.Chapter => "▤",
        NodeKind.Section => "§",
        NodeKind.Scene => "▪",
        NodeKind.Research => "⌕",
        NodeKind.Note => "✎",
        _ => "•"
    };

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        SaveState();
        CancelIndexing();
        _frameTimer.Stop();
        _frameTimer.Tick -= OnFrameTick;
        _itemsSourceLease?.Dispose();
        _itemTemplateLease?.Dispose();
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        _viewModel.StateChanged -= OnStateChanged;
        _viewModel.BinderRows.CollectionChanged -= OnCanonicalRowsChanged;

        if (_binder is not null)
        {
            _binder.SelectionChanged -= BinderSelectionChanged;
            _binder.DoubleTapped -= BinderDoubleTapped;
            _binder.KeyDown -= BinderKeyDown;
        }
        if (_bookmarkList is not null)
        {
            _bookmarkList.SelectionChanged -= BookmarkSelectionChanged;
            _bookmarkList.PropertyChanged -= BookmarkPropertyChanged;
        }
    }

    internal sealed class BinderTreeRowControl : Grid
    {
        private readonly BinderTreeFeature _owner;
        private readonly BinderRowViewModel _row;
        private readonly Button _disclosure;
        private readonly Border _labelSwatch;
        private readonly TextBlock _icon;
        private readonly TextBlock _title;
        private readonly TextBlock _compile;
        private readonly StackPanel _headings;
        private string _headingSignature = string.Empty;
        private bool _lastExpanded;

        internal BinderTreeRowControl(BinderTreeFeature owner, BinderRowViewModel row)
        {
            _owner = owner;
            _row = row;
            PersistentId = row.Node.PersistentId;
            RowDefinitions = new RowDefinitions("Auto,Auto");
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto");
            Margin = new Thickness(Math.Clamp(row.Depth, 0, 12) * 13, 0, 0, 0);

            _disclosure = new Button
            {
                Width = 20,
                MinWidth = 20,
                MinHeight = 24,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Focusable = false
            };
            _disclosure.Classes.Add("binder-disclosure");
            _disclosure.PointerPressed += (_, e) => e.Handled = true;
            _disclosure.Click += (_, e) =>
            {
                e.Handled = true;
                _owner.ToggleDisclosure(_row, this);
            };

            _labelSwatch = new Border
            {
                Width = 3,
                Height = 20,
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 5, 0)
            };

            _icon = new TextBlock
            {
                Width = 20,
                FontSize = 12,
                Opacity = 0.72,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _icon.Classes.Add("binder-icon");

            _title = new TextBlock
            {
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            _title.Classes.Add("binder-title");

            _compile = new TextBlock
            {
                Width = 18,
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _compile.Classes.Add("binder-compile-state");

            Children.Add(_disclosure);
            Grid.SetColumn(_labelSwatch, 1); Children.Add(_labelSwatch);
            Grid.SetColumn(_icon, 2); Children.Add(_icon);
            Grid.SetColumn(_title, 3); Children.Add(_title);
            Grid.SetColumn(_compile, 4); Children.Add(_compile);

            _headings = new StackPanel
            {
                Spacing = 1,
                Margin = new Thickness(40, 1, 2, 4),
                IsVisible = false
            };
            _headings.Classes.Add("binder-heading-group");
            Grid.SetRow(_headings, 1);
            Grid.SetColumn(_headings, 0);
            Grid.SetColumnSpan(_headings, 5);
            Children.Add(_headings);

            Refresh();
        }

        internal string PersistentId { get; }

        internal void Refresh()
        {
            var node = _row.Node;
            var headings = _owner.HeadingsFor(_row);
            var canExpand = node.IsContainer || (node.IsDocument && headings.Count > 0);
            var expanded = node.IsContainer
                ? !_owner.IsCollapsed(_row)
                : node.IsDocument && _owner.IsHeadingExpanded(_row);

            _disclosure.IsVisible = canExpand;
            _disclosure.IsEnabled = canExpand;
            _disclosure.Content = canExpand ? (expanded ? "▾" : "▸") : string.Empty;
            _labelSwatch.Background = LabelBrush(node);
            _labelSwatch.Opacity = string.IsNullOrWhiteSpace(node.Label) ? 0 : 1;
            _icon.Text = Icon(node.Kind);
            _title.Text = node.Title;
            _title.FontWeight = node.IsContainer ? FontWeight.SemiBold : FontWeight.Normal;
            _compile.Text = node.IncludeInCompilation ? "●" : "○";
            _compile.Opacity = node.IncludeInCompilation ? 0.78 : 0.3;

            var signature = string.Join('\u001f', headings.Select(static heading => $"{heading.Level}:{heading.SourceLine}:{heading.Title}"));
            if (expanded == _lastExpanded && string.Equals(signature, _headingSignature, StringComparison.Ordinal))
            {
                _headings.IsVisible = node.IsDocument && expanded && headings.Count > 0;
                return;
            }

            _lastExpanded = expanded;
            _headingSignature = signature;
            _headings.Children.Clear();
            _headings.IsVisible = node.IsDocument && expanded && headings.Count > 0;
            if (!_headings.IsVisible) return;

            foreach (var heading in headings)
                _headings.Children.Add(BuildHeadingButton(heading));
        }

        private Control BuildHeadingButton(OutlineItemViewModel heading)
        {
            var level = Math.Clamp(heading.Level, 1, 6);
            var badge = new TextBlock
            {
                Text = $"H{level}",
                Width = 25,
                FontSize = 9.5,
                Opacity = 0.5,
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
            var content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Margin = new Thickness((level - 1) * 11, 0, 0, 0)
            };
            content.Children.Add(badge);
            Grid.SetColumn(title, 1); content.Children.Add(title);

            var button = new Button
            {
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(4, 3),
                MinHeight = 26,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Focusable = false
            };
            button.Classes.Add("binder-heading");
            button.PointerPressed += (_, e) => e.Handled = true;
            button.Click += async (_, e) =>
            {
                e.Handled = true;
                await _owner.NavigateToHeadingAsync(_row, heading);
            };
            ToolTip.SetTip(button, $"Go to {heading.Title} — line {heading.SourceLine}");
            return button;
        }
    }
}
