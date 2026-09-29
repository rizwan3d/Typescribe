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
/// Event-driven Binder presenter.
///
/// The Binder is rendered once and keeps one permanent ItemsSource and ItemTemplate. It reacts
/// only to BinderRows collection changes, explicit disclosure actions, selection changes, and
/// semantic outline collection changes. Ordinary editor StateChanged notifications, autosave,
/// word count updates and PDF preview updates never enter this component.
/// </summary>
internal sealed class BinderTreeFeature
{
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
    private readonly HashSet<string> _headingDisclosureTouched = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OutlineItemViewModel[]> _headingIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _headingVisualSignatures = new(StringComparer.Ordinal);

    private ListBox? _binder;
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
    private bool _structureSyncScheduled;
    private bool _outlineSyncScheduled;
    private bool _restoringPendingSelection;
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
        viewModel.BinderRows.CollectionChanged += feature.OnBinderRowsChanged;
        viewModel.OutlineItems.CollectionChanged += feature.OnOutlineItemsChanged;

        feature.TryAttach();
        feature.UpdateProjectState();
        feature.SynchronizeStructure();
        feature.ScheduleOutlineSync();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        TryAttach();
        ScheduleStructureSync();
        ScheduleOutlineSync();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (_binder is null || _centerTabs is null || _titleEditor is null)
            TryAttach();
        if (_binder is not null && _centerTabs is not null && _titleEditor is not null)
            _window.LayoutUpdated -= OnLayoutUpdated;
    }

    private void OnBinderRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => ScheduleStructureSync();

    private void OnOutlineItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => ScheduleOutlineSync();

    private void ScheduleStructureSync()
    {
        if (_disposed || _structureSyncScheduled) return;
        _structureSyncScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _structureSyncScheduled = false;
            if (!_disposed) SynchronizeStructure();
        }, DispatcherPriority.Background);
    }

    private void ScheduleOutlineSync()
    {
        if (_disposed || _outlineSyncScheduled) return;
        _outlineSyncScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _outlineSyncScheduled = false;
            if (!_disposed) ApplyCurrentOutlineSnapshot();
        }, DispatcherPriority.Background);
    }

    private void TryAttach()
    {
        if (_disposed || _window.Content is not Control content) return;

        _binder ??= EnumerateControls(content)
            .OfType<ListBox>()
            .FirstOrDefault(static list => list.ContextMenu is not null);

        if (_binder is not null && !_binder.Classes.Contains("binder-event-driven"))
            AttachBinder(_binder);

        if (_centerTabs is null)
        {
            foreach (var tabs in EnumerateControls(content).OfType<TabControl>())
            {
                var items = TabItems(tabs).ToArray();
                var headers = items.Select(static item => item.Header?.ToString() ?? string.Empty).ToArray();
                if (!headers.Contains("Editor", StringComparer.Ordinal) || !headers.Contains("Corkboard", StringComparer.Ordinal))
                    continue;
                _centerTabs = tabs;
                _editorTab = items.FirstOrDefault(static item => string.Equals(item.Header?.ToString(), "Editor", StringComparison.Ordinal));
                break;
            }
        }

        EnsureEditableDocumentTitle();
    }

    private void AttachBinder(ListBox binder)
    {
        binder.Classes.Add("binder-event-driven");
        if (!binder.Classes.Contains("binder-list")) binder.Classes.Add("binder-list");
        binder.SelectionMode = SelectionMode.Single;
        binder.Focusable = true;
        binder.AutoScrollToSelectedItem = false;

        _rowTemplate = new FuncDataTemplate<BinderRowViewModel>(
            (row, _) => new BinderTreeRowControl(this, row),
            supportsRecycling: false);

        _selectedPersistentId = (binder.SelectedItem as BinderRowViewModel)?.Node.PersistentId
                                ?? _viewModel.SelectedRow?.Node.PersistentId;

        // Keep one effective source/template for the entire window lifetime. Older workspace
        // code may still write local values, but those values never become effective and thus
        // cannot recreate ListBox containers. This is an ownership guard, not a refresh loop.
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

        binder.PointerPressed += BinderPointerPressed;
        binder.SelectionChanged += BinderSelectionChanged;
        binder.DoubleTapped += BinderDoubleTapped;
        binder.KeyDown += BinderKeyDown;
    }

    private void BinderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_binder is null || !e.GetCurrentPoint(_binder).Properties.IsLeftButtonPressed) return;
        var row = FindBinderRow(e.Source);
        if (row is null) return;
        _pendingUserSelectionId = row.Node.PersistentId;
        _selectedPersistentId = row.Node.PersistentId;
    }

    private void BinderSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_binder?.SelectedItem is not BinderRowViewModel row || _restoringPendingSelection) return;
        var id = row.Node.PersistentId;

        if (_pendingUserSelectionId is not null &&
            !string.Equals(id, _pendingUserSelectionId, StringComparison.Ordinal))
        {
            var pending = _visibleRows.FirstOrDefault(candidate =>
                string.Equals(candidate.Node.PersistentId, _pendingUserSelectionId, StringComparison.Ordinal));
            if (pending is not null)
            {
                _restoringPendingSelection = true;
                try { _binder.SelectedItem = pending; }
                finally { _restoringPendingSelection = false; }
            }
            return;
        }

        _selectedPersistentId = id;
        SynchronizeTitleEditor(row);
        if (_pendingUserSelectionId is not null)
            Dispatcher.UIThread.Post(ConfirmPendingSelection, DispatcherPriority.Background);
    }

    private void ConfirmPendingSelection()
    {
        if (_pendingUserSelectionId is null) return;
        var modelId = _viewModel.SelectedRow?.Node.PersistentId;
        if (string.Equals(modelId, _pendingUserSelectionId, StringComparison.Ordinal))
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

        if (e.Key == Key.Right && row.Node.IsContainer && IsCollapsed(row))
        {
            _collapsedNodes.Remove(row.Node.PersistentId);
            SaveState();
            ApplyVisibleProjection();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Left) return;
        if (row.Node.IsContainer && !IsCollapsed(row))
        {
            _collapsedNodes.Add(row.Node.PersistentId);
            SaveState();
            ApplyVisibleProjection();
            e.Handled = true;
            return;
        }

        var parent = FindParent(row);
        if (parent is null) return;
        _binder.SelectedItem = parent;
        _binder.ScrollIntoView(parent);
        e.Handled = true;
    }

    private void SynchronizeStructure()
    {
        if (_disposed) return;
        TryAttach();
        UpdateProjectState();
        PruneRemovedHeadingCaches();
        ApplyVisibleProjection();
        SynchronizeSelectionFromModel();
        RefreshRealizedRows();
        _ = IndexMissingDocumentsAsync();
    }

    private void ApplyVisibleProjection()
        => ReconcileVisibleRows(BuildVisibleRows());

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
                _visibleRows[index].RefreshFrom(desired);
                continue;
            }

            var existingIndex = FindVisibleIndex(desired.Node.PersistentId, index + 1);
            if (existingIndex >= 0)
            {
                _visibleRows.Move(existingIndex, index);
                _visibleRows[index].RefreshFrom(desired);
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

    private void SynchronizeSelectionFromModel()
    {
        if (_binder is null || _pendingUserSelectionId is not null) return;
        var targetId = _viewModel.SelectedRow?.Node.PersistentId ?? _selectedPersistentId;
        if (targetId is null) return;
        var target = _visibleRows.FirstOrDefault(row =>
            string.Equals(row.Node.PersistentId, targetId, StringComparison.Ordinal));
        if (target is null) return;
        _selectedPersistentId = targetId;
        if (!ReferenceEquals(_binder.SelectedItem, target)) _binder.SelectedItem = target;
    }

    internal void ToggleDisclosure(BinderRowViewModel row, BinderTreeRowControl visual)
    {
        if (row.Node.IsContainer)
        {
            ToggleContainer(row, selectContainer: false);
            return;
        }

        if (!row.Node.IsDocument || !HasHeadings(row.Node.PersistentId)) return;
        _headingDisclosureTouched.Add(row.Node.PersistentId);
        if (!_expandedHeadingDocuments.Add(row.Node.PersistentId))
            _expandedHeadingDocuments.Remove(row.Node.PersistentId);
        SaveState();
        visual.Refresh();
    }

    private void ToggleContainer(BinderRowViewModel row, bool selectContainer)
    {
        if (!row.Node.IsContainer) return;
        var collapse = !_collapsedNodes.Contains(row.Node.PersistentId);
        if (collapse)
        {
            _collapsedNodes.Add(row.Node.PersistentId);
            if (selectContainer || IsSelectionDescendantOf(row))
            {
                _selectedPersistentId = row.Node.PersistentId;
                _pendingUserSelectionId = null;
                if (_binder is not null) _binder.SelectedItem = row;
            }
        }
        else
        {
            _collapsedNodes.Remove(row.Node.PersistentId);
        }

        SaveState();
        ApplyVisibleProjection();
        RefreshRealizedRows();
    }

    private bool IsSelectionDescendantOf(BinderRowViewModel parent)
    {
        if (_selectedPersistentId is null) return false;
        var rows = _viewModel.BinderRows;
        var parentIndex = rows.IndexOf(parent);
        if (parentIndex < 0) return false;
        for (var i = parentIndex + 1; i < rows.Count && rows[i].Depth > parent.Depth; i++)
            if (string.Equals(rows[i].Node.PersistentId, _selectedPersistentId, StringComparison.Ordinal))
                return true;
        return false;
    }

    private BinderRowViewModel? FindParent(BinderRowViewModel row)
    {
        var rows = _viewModel.BinderRows;
        var index = rows.FirstOrDefaultIndex(candidate =>
            string.Equals(candidate.Node.PersistentId, row.Node.PersistentId, StringComparison.Ordinal));
        if (index < 0 || row.Depth <= 0) return null;
        for (var i = index - 1; i >= 0; i--)
            if (rows[i].Depth < row.Depth)
                return rows[i];
        return null;
    }

    internal bool IsCollapsed(BinderRowViewModel row)
        => row.Node.IsContainer && _collapsedNodes.Contains(row.Node.PersistentId);

    internal bool IsHeadingExpanded(BinderRowViewModel row)
        => row.Node.IsDocument && _expandedHeadingDocuments.Contains(row.Node.PersistentId);

    internal IReadOnlyList<OutlineItemViewModel> HeadingsFor(BinderRowViewModel row)
        => _headingIndex.TryGetValue(row.Node.PersistentId, out var headings) ? headings : [];

    internal string HeadingVisualSignature(BinderRowViewModel row)
        => _headingVisualSignatures.GetValueOrDefault(row.Node.PersistentId, string.Empty);

    internal bool HasHeadings(string persistentId)
        => _headingIndex.TryGetValue(persistentId, out var headings) && headings.Length > 0;

    private void ApplyCurrentOutlineSnapshot()
    {
        var selected = _viewModel.SelectedRow;
        if (selected?.Node.IsDocument != true) return;

        var id = selected.Node.PersistentId;
        var headings = _viewModel.OutlineItems.ToArray();
        var signature = BuildHeadingVisualSignature(headings);
        _headingIndex[id] = headings;

        if (_headingVisualSignatures.TryGetValue(id, out var old) &&
            string.Equals(old, signature, StringComparison.Ordinal))
        {
            // SourceLine/EndLine may have changed after ordinary typing. Keep the fresh semantic
            // objects for navigation, but do not touch the visual tree when titles/levels did not.
            return;
        }

        _headingVisualSignatures[id] = signature;
        var stateChanged = false;
        if (headings.Length == 0)
            stateChanged = _expandedHeadingDocuments.Remove(id);
        else if (!_headingDisclosureTouched.Contains(id))
            stateChanged = _expandedHeadingDocuments.Add(id);

        if (stateChanged) SaveState();
        RefreshRealizedRows(id);
    }

    internal async Task NavigateToHeadingAsync(BinderRowViewModel owner, int headingIndex)
    {
        try
        {
            var canonical = _viewModel.BinderRows.FirstOrDefault(row =>
                string.Equals(row.Node.PersistentId, owner.Node.PersistentId, StringComparison.Ordinal));
            if (canonical is null) return;

            if (!string.Equals(_viewModel.SelectedRow?.Node.PersistentId, canonical.Node.PersistentId, StringComparison.Ordinal))
                await _viewModel.SelectAsync(canonical);

            if (!_headingIndex.TryGetValue(owner.Node.PersistentId, out var headings) ||
                headingIndex < 0 || headingIndex >= headings.Length)
                return;

            var heading = headings[headingIndex];
            var liveHeading = _viewModel.OutlineItems.FirstOrDefault(item =>
                item.Level == heading.Level &&
                string.Equals(item.Title, heading.Title, StringComparison.Ordinal) &&
                item.SourceLine == heading.SourceLine) ?? heading;
            _viewModel.SelectOutline(liveHeading);
            if (_centerTabs is not null && _editorTab is not null)
                _centerTabs.SelectedItem = _editorTab;
        }
        catch
        {
            // Navigation should never make the Binder interaction surface unusable.
        }
    }

    private static string BuildHeadingVisualSignature(IReadOnlyList<OutlineItemViewModel> headings)
    {
        if (headings.Count == 0) return string.Empty;
        var builder = new StringBuilder(headings.Count * 24);
        foreach (var heading in headings)
            builder.Append(heading.Level).Append(':').Append(heading.Title).Append('\u001f');
        return builder.ToString();
    }

    private void PruneRemovedHeadingCaches()
    {
        var ids = _viewModel.BinderRows.Select(static row => row.Node.PersistentId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _headingIndex.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            _headingIndex.Remove(id);
            _headingVisualSignatures.Remove(id);
            _expandedHeadingDocuments.Remove(id);
            _headingDisclosureTouched.Remove(id);
        }
    }

    private async Task IndexMissingDocumentsAsync()
    {
        if (_disposed || _indexing || _repository.CurrentProject is not { } project) return;
        var missing = _viewModel.BinderRows
            .Where(static row => row.Node.IsDocument)
            .Where(row => !_headingIndex.ContainsKey(row.Node.PersistentId))
            .ToArray();
        if (missing.Length == 0) return;

        _indexCts ??= new CancellationTokenSource();
        var token = _indexCts.Token;
        _indexing = true;
        var changed = false;
        try
        {
            foreach (var row in missing)
            {
                token.ThrowIfCancellationRequested();
                string text;
                try { text = await _repository.ReadDocumentAsync(project, row.Node, token); }
                catch when (!token.IsCancellationRequested) { continue; }

                var headings = ParseHeadings(text);
                var id = row.Node.PersistentId;
                _headingIndex[id] = headings;
                _headingVisualSignatures[id] = BuildHeadingVisualSignature(headings);
                changed |= headings.Length > 0;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            _indexing = false;
        }

        if (changed && !_disposed)
            Dispatcher.UIThread.Post(() => RefreshRealizedRows(), DispatcherPriority.Background);
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
                if (!inFence) { inFence = true; fenceCharacter = marker; }
                else if (marker == fenceCharacter) { inFence = false; fenceCharacter = '\0'; }
                continue;
            }
            if (inFence || trimmed.Length < 2 || trimmed[0] != '#') continue;

            var level = 0;
            while (level < trimmed.Length && level < 6 && trimmed[level] == '#') level++;
            if (level == 0 || level >= trimmed.Length || !char.IsWhiteSpace(trimmed[level])) continue;
            var title = trimmed[(level + 1)..].Trim();
            while (title.EndsWith('#')) title = title[..^1].TrimEnd();
            if (title.Length > 0) pending.Add((title, level, index + 1));
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
            _headingDisclosureTouched.Clear();
            _headingIndex.Clear();
            _headingVisualSignatures.Clear();
            _visibleRows.Clear();
            CancelIndexing();
            return;
        }

        if (string.Equals(_projectRoot, project.RootPath, StringComparison.Ordinal)) return;

        _projectRoot = project.RootPath;
        _statePath = Path.Combine(project.RootPath, ".typescribe", "binder-tree.tsv");
        _collapsedNodes.Clear();
        _expandedHeadingDocuments.Clear();
        _headingDisclosureTouched.Clear();
        _headingIndex.Clear();
        _headingVisualSignatures.Clear();
        _visibleRows.Clear();
        LoadState(project.RootPath);
        CancelIndexing();
        _indexCts = new CancellationTokenSource();
    }

    private void LoadState(string projectRoot)
    {
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
                return;
            }
            catch
            {
            }
        }

        var oldWorkspace = Path.Combine(projectRoot, ".typescribe", "workspace.tsv");
        if (File.Exists(oldWorkspace))
        {
            try
            {
                foreach (var line in File.ReadLines(oldWorkspace, Encoding.UTF8))
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
        }
    }

    private void RefreshRealizedRows(string? persistentId = null)
    {
        if (_binder is null) return;
        foreach (var visual in _binder.GetVisualDescendants().OfType<BinderTreeRowControl>().ToArray())
            if (persistentId is null || string.Equals(visual.PersistentId, persistentId, StringComparison.Ordinal))
                visual.Refresh();
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
            SynchronizeTitleEditor(_binder?.SelectedItem as BinderRowViewModel, force: true);
            _window.GetVisualDescendants().OfType<ManuscriptEditor>().FirstOrDefault()?.Focus();
        }
    }

    private async Task CommitTitleAsync()
    {
        if (_titleCommitRunning || _titleEditor is null || !_viewModel.HasSelection) return;
        var requested = (_titleEditor.Text ?? string.Empty).Trim();
        if (requested.Length == 0 || string.Equals(requested, _viewModel.SelectedTitle, StringComparison.Ordinal))
        {
            SynchronizeTitleEditor(_binder?.SelectedItem as BinderRowViewModel, force: true);
            return;
        }

        _titleCommitRunning = true;
        try { await _viewModel.RenameSelectedAsync(requested); }
        finally
        {
            _titleCommitRunning = false;
            SynchronizeTitleEditor(_binder?.SelectedItem as BinderRowViewModel, force: true);
        }
    }

    private void SynchronizeTitleEditor(BinderRowViewModel? row, bool force = false)
    {
        if (_titleEditor is null) return;
        _titleEditor.IsEnabled = row is not null;
        if (!force && _titleEditor.IsKeyboardFocusWithin) return;
        var title = row?.Node.Title ?? string.Empty;
        if (!string.Equals(_titleEditor.Text, title, StringComparison.Ordinal))
            _titleEditor.Text = title;
    }

    private static BinderRowViewModel? FindBinderRow(object? source)
    {
        if (source is ListBoxItem direct)
            return direct.Content as BinderRowViewModel ?? direct.DataContext as BinderRowViewModel;
        if (source is not Visual visual) return null;
        var item = visual.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        return item?.Content as BinderRowViewModel ?? item?.DataContext as BinderRowViewModel;
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
            foreach (var child in panel.Children)
                foreach (var nested in EnumerateControls(child))
                    yield return nested;
        if (root is ContentControl content && content.Content is Control contentChild)
            foreach (var nested in EnumerateControls(contentChild))
                yield return nested;
        if (root is Decorator decorator && decorator.Child is Control decoratedChild)
            foreach (var nested in EnumerateControls(decoratedChild))
                yield return nested;
    }

    private void CancelIndexing()
    {
        _indexCts?.Cancel();
        _indexCts?.Dispose();
        _indexCts = null;
        _indexing = false;
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
        _itemsSourceLease?.Dispose();
        _itemTemplateLease?.Dispose();
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        _viewModel.BinderRows.CollectionChanged -= OnBinderRowsChanged;
        _viewModel.OutlineItems.CollectionChanged -= OnOutlineItemsChanged;
        if (_binder is not null)
        {
            _binder.PointerPressed -= BinderPointerPressed;
            _binder.SelectionChanged -= BinderSelectionChanged;
            _binder.DoubleTapped -= BinderDoubleTapped;
            _binder.KeyDown -= BinderKeyDown;
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
        private string? _headingRenderKey;

        internal BinderTreeRowControl(BinderTreeFeature owner, BinderRowViewModel row)
        {
            _owner = owner;
            _row = row;
            PersistentId = row.Node.PersistentId;
            RowDefinitions = new RowDefinitions("Auto,Auto");
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto");

            _disclosure = new Button
            {
                Width = 20,
                MinWidth = 20,
                MinHeight = 24,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
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
            _title = new TextBlock
            {
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            _compile = new TextBlock
            {
                Width = 18,
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

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
            Grid.SetColumnSpan(_headings, 5);
            Children.Add(_headings);
            Refresh();
        }

        internal string PersistentId { get; }

        internal void Refresh()
        {
            var node = _row.Node;
            Margin = new Thickness(Math.Clamp(_row.Depth, 0, 12) * 13, 0, 0, 0);
            var headings = _owner.HeadingsFor(_row);
            var canExpand = node.IsContainer || (node.IsDocument && headings.Count > 0);
            var expanded = node.IsContainer ? !_owner.IsCollapsed(_row) : node.IsDocument && _owner.IsHeadingExpanded(_row);

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

            var renderKey = $"{expanded}:{_owner.HeadingVisualSignature(_row)}";
            if (string.Equals(_headingRenderKey, renderKey, StringComparison.Ordinal)) return;
            _headingRenderKey = renderKey;
            _headings.Children.Clear();
            _headings.IsVisible = node.IsDocument && expanded && headings.Count > 0;
            if (!_headings.IsVisible) return;
            for (var index = 0; index < headings.Count; index++)
                _headings.Children.Add(BuildHeadingButton(index, headings[index]));
        }

        private Control BuildHeadingButton(int index, OutlineItemViewModel heading)
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
                await _owner.NavigateToHeadingAsync(_row, index);
            };
            ToolTip.SetTip(button, $"Go to {heading.Title}");
            return button;
        }
    }
}

internal static class BinderRowCollectionExtensions
{
    internal static int FirstOrDefaultIndex(this IList<BinderRowViewModel> rows, Func<BinderRowViewModel, bool> predicate)
    {
        for (var index = 0; index < rows.Count; index++)
            if (predicate(rows[index])) return index;
        return -1;
    }
}
