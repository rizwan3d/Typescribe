using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Application.Abstractions;
using Typescribe.Application.Models;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

/// <summary>
/// Native hierarchical project explorer for Typescribe.
///
/// This is intentionally event driven: project structure changes update project nodes,
/// semantic outline changes update only one document's heading branch, and selection changes
/// never rebuild the tree. Ordinary typing, autosave, PDF preview and inspector updates do not
/// touch this control unless the actual heading structure changes.
/// </summary>
internal sealed class ProjectExplorerFeature
{
    private static readonly DataFormat<ExplorerNode> ExplorerDragFormat =
        DataFormat.CreateInProcessFormat<ExplorerNode>("typescribe-project-explorer-node");

    private static readonly string[] LabelPalette =
    [
        "#64748B", "#3B82F6", "#8B5CF6", "#F43F5E",
        "#F59E0B", "#10B981", "#06B6D4", "#EC4899"
    ];

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly TrackingProjectRepository _repository;
    private readonly IDocumentParser _parser;
    private readonly ObservableCollection<ExplorerNode> _roots = [];
    private readonly Dictionary<string, ExplorerNode> _projectNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExplorerNode> _headingNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OutlineItemViewModel[]> _headingIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _headingSignatures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedKeys = new(StringComparer.Ordinal);

    private readonly TreeView _tree = new()
    {
        SelectionMode = SelectionMode.Single,
        AutoScrollToSelectedItem = true,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0)
    };

    private readonly TextBlock _caption = new()
    {
        Text = "PROJECT EXPLORER",
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0.72
    };

    // The old ListBox is removed from the visual tree. It remains temporarily referenced only so
    // older authoring modules that captured it before replacement can continue receiving the
    // workspace's selection updates while those modules are migrated away from Binder UI access.
    private ListBox? _legacySelectionBridge;
    private Grid? _host;
    private CancellationTokenSource? _indexCts;
    private ExplorerNode? _projectRoot;
    private ExplorerNode? _dragCandidate;
    private PointerPressedEventArgs? _dragTrigger;
    private Point _dragStart;
    private string? _projectPath;
    private string? _statePath;
    private string? _lastModelSelectionId;
    private bool _attachScheduled;
    private bool _structureSyncScheduled;
    private bool _outlineSyncScheduled;
    private bool _syncingTreeSelection;
    private bool _indexing;
    private bool _hasStoredExpansionState;
    private bool _disposed;

    private ProjectExplorerFeature(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository,
        IDocumentParser parser)
    {
        _window = window;
        _viewModel = viewModel;
        _repository = repository;
        _parser = parser;

        _tree.ItemsSource = _roots;
        _tree.ItemTemplate = new FuncTreeDataTemplate<ExplorerNode>(
            static (node, _) => new ExplorerNodeHeader(node),
            static node => node.Children);
        _tree.SelectionChanged += TreeSelectionChanged;
        _tree.ContainerPrepared += TreeContainerPrepared;
        _tree.PointerPressed += TreePointerPressed;
        _tree.PointerMoved += TreePointerMoved;
        _tree.AddHandler(TreeViewItem.ExpandedEvent, TreeItemExpanded, RoutingStrategies.Bubble);
        _tree.AddHandler(TreeViewItem.CollapsedEvent, TreeItemCollapsed, RoutingStrategies.Bubble);
        DragDrop.SetAllowDrop(_tree, true);
        DragDrop.AddDragOverHandler(_tree, TreeDragOver);
        DragDrop.AddDropHandler(_tree, TreeDrop);
        _tree.ContextMenu = BuildContextMenu();
    }

    public static void Apply(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository,
        IDocumentParser parser)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(parser);

        var feature = new ProjectExplorerFeature(window, viewModel, repository, parser);
        window.Opened += feature.WindowOpened;
        window.Closed += feature.WindowClosed;
        viewModel.BinderRows.CollectionChanged += feature.BinderRowsChanged;
        viewModel.OutlineItems.CollectionChanged += feature.OutlineItemsChanged;
        feature.ScheduleAttach();
    }

    private void WindowOpened(object? sender, EventArgs e)
    {
        ScheduleAttach();
        ScheduleStructureSync();
    }

    private void ScheduleAttach()
    {
        if (_disposed || _attachScheduled || _host is not null) return;
        _attachScheduled = true;

        // Give authoring modules that still discover the old ListBox one initial layout pass.
        Dispatcher.UIThread.Post(() =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _attachScheduled = false;
                if (!_disposed) Attach();
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Background);
    }

    private void Attach()
    {
        if (_disposed || _host is not null || _window.Content is not Control content) return;

        var legacy = content.GetVisualDescendants()
            .OfType<ListBox>()
            .FirstOrDefault(static list => list.ContextMenu is not null);
        if (legacy is null || legacy.GetVisualParent() is not Grid parent) return;

        _legacySelectionBridge = legacy;
        _legacySelectionBridge.SelectionChanged += LegacySelectionChanged;

        var row = Grid.GetRow(legacy);
        var column = Grid.GetColumn(legacy);
        var rowSpan = Grid.GetRowSpan(legacy);
        var columnSpan = Grid.GetColumnSpan(legacy);

        _host = BuildHost();
        Grid.SetRow(_host, row);
        Grid.SetColumn(_host, column);
        Grid.SetRowSpan(_host, rowSpan);
        Grid.SetColumnSpan(_host, columnSpan);

        parent.Children.Remove(legacy);
        parent.Children.Add(_host);

        UpdateProjectIdentity(force: true);
        SynchronizeStructure();
        ScheduleOutlineSync();
    }

    private Grid BuildHost()
    {
        var collapse = new Button
        {
            Content = "⊟",
            MinWidth = 28,
            Height = 24,
            Padding = new Thickness(4, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        ToolTip.SetTip(collapse, "Collapse all");
        collapse.Click += (_, _) => CollapseAll();

        var reveal = new Button
        {
            Content = "◎",
            MinWidth = 28,
            Height = 24,
            Padding = new Thickness(4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(4, 0, 0, 0)
        };
        ToolTip.SetTip(reveal, "Reveal active document");
        reveal.Click += (_, _) => RevealActiveDocument();

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(8, 5, 6, 5)
        };
        header.Children.Add(_caption);
        Grid.SetColumn(collapse, 1);
        header.Children.Add(collapse);
        Grid.SetColumn(reveal, 2);
        header.Children.Add(reveal);

        var separator = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.Parse("#2B2B2B")),
            Opacity = 0.35
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,1,*"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        grid.Children.Add(header);
        Grid.SetRow(separator, 1);
        grid.Children.Add(separator);
        Grid.SetRow(_tree, 2);
        grid.Children.Add(_tree);
        return grid;
    }

    private void BinderRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => ScheduleStructureSync();

    private void OutlineItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
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
            if (_disposed) return;
            SyncSelectionFromModel();
            ApplySelectedOutlineSnapshot();
        }, DispatcherPriority.Background);
    }

    private void SynchronizeStructure()
    {
        if (_disposed || _host is null) return;
        UpdateProjectIdentity(force: false);

        var project = _repository.CurrentProject;
        if (project is null)
        {
            _roots.Clear();
            _projectRoot = null;
            return;
        }

        if (_projectRoot is null)
        {
            _projectRoot = ExplorerNode.CreateProjectRoot(project.Title, project.RootPath);
            _projectRoot.IsExpanded = true;
            _roots.Add(_projectRoot);
        }
        else
        {
            _projectRoot.SetTitle(project.Title);
        }

        var desiredByParent = new Dictionary<string, List<ExplorerNode>>(StringComparer.Ordinal)
        {
            [_projectRoot.Key] = []
        };
        var ancestors = new List<ExplorerNode>();
        var liveIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in _viewModel.BinderRows)
        {
            liveIds.Add(row.Node.PersistentId);
            if (!_projectNodes.TryGetValue(row.Node.PersistentId, out var node))
            {
                node = ExplorerNode.CreateProjectNode(row);
                node.IsExpanded = InitialExpandedState(node);
                _projectNodes[row.Node.PersistentId] = node;
            }
            else
            {
                node.UpdateProjectRow(row);
            }

            while (ancestors.Count > row.Depth) ancestors.RemoveAt(ancestors.Count - 1);
            var parent = row.Depth == 0 || ancestors.Count == 0 ? _projectRoot : ancestors[^1];
            node.Parent = parent;
            if (!desiredByParent.TryGetValue(parent.Key, out var siblings))
                desiredByParent[parent.Key] = siblings = [];
            siblings.Add(node);
            desiredByParent.TryAdd(node.Key, []);

            if (ancestors.Count == row.Depth) ancestors.Add(node);
            else ancestors[row.Depth] = node;
        }

        ReconcileCollection(_projectRoot.Children, desiredByParent[_projectRoot.Key]);
        ReconcileProjectChildrenRecursive(_projectRoot, desiredByParent);

        foreach (var removed in _projectNodes.Keys.Where(id => !liveIds.Contains(id)).ToArray())
        {
            _projectNodes.Remove(removed);
            _headingIndex.Remove(removed);
            _headingSignatures.Remove(removed);
        }

        _caption.Text = string.IsNullOrWhiteSpace(project.Title)
            ? "PROJECT EXPLORER"
            : $"PROJECT EXPLORER  ·  {project.Title}";

        SyncSelectionFromModel();
        _ = IndexDocumentsAsync();
    }

    private void ReconcileProjectChildrenRecursive(
        ExplorerNode parent,
        IReadOnlyDictionary<string, List<ExplorerNode>> desiredByParent)
    {
        foreach (var child in parent.Children.Where(static child => child.Kind == ExplorerNodeKind.ProjectNode).ToArray())
        {
            if (child.Row?.Node.IsContainer == true)
            {
                var desired = desiredByParent.TryGetValue(child.Key, out var list) ? list : [];
                ReconcileCollection(child.Children, desired);
                ReconcileProjectChildrenRecursive(child, desiredByParent);
            }
            else if (child.Row?.Node.IsDocument == true)
            {
                ReconcileHeadingChildren(child, _headingIndex.GetValueOrDefault(child.Row.Node.PersistentId, []));
            }
            else
            {
                child.Children.Clear();
            }
        }
    }

    private void ApplySelectedOutlineSnapshot()
    {
        var row = _viewModel.SelectedRow;
        if (row?.Node.IsDocument != true) return;
        ApplyHeadings(row.Node.PersistentId, _viewModel.OutlineItems.ToArray(), expandSelectedDocument: true);
    }

    private void ApplyHeadings(string documentId, OutlineItemViewModel[] headings, bool expandSelectedDocument)
    {
        _headingIndex[documentId] = headings;
        var signature = HeadingVisualSignature(headings);
        var visualChanged = !_headingSignatures.TryGetValue(documentId, out var old) ||
                            !string.Equals(old, signature, StringComparison.Ordinal);
        _headingSignatures[documentId] = signature;

        if (!_projectNodes.TryGetValue(documentId, out var documentNode)) return;
        if (expandSelectedDocument && headings.Length > 0)
        {
            documentNode.IsExpanded = true;
            _expandedKeys.Add(documentNode.Key);
        }

        if (visualChanged)
            ReconcileHeadingChildren(documentNode, headings);
        else
            RefreshHeadingNavigationData(documentId, headings);

        ApplyExpansionToRealizedItems();
    }

    private void ReconcileHeadingChildren(ExplorerNode documentNode, IReadOnlyList<OutlineItemViewModel> headings)
    {
        var desiredRoots = new List<ExplorerNode>();
        var parents = new List<ExplorerNode>();
        var occurrence = new Dictionary<string, int>(StringComparer.Ordinal);
        var desiredChildren = new Dictionary<string, List<ExplorerNode>>(StringComparer.Ordinal);
        var liveKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var heading in headings)
        {
            var occurrenceBase = $"{heading.Level}\u001f{heading.Title}";
            var ordinal = occurrence.GetValueOrDefault(occurrenceBase);
            occurrence[occurrenceBase] = ordinal + 1;
            var key = HeadingKey(documentNode.PersistentId!, heading.Level, heading.Title, ordinal);
            liveKeys.Add(key);

            if (!_headingNodes.TryGetValue(key, out var node))
            {
                node = ExplorerNode.CreateHeading(key, documentNode.PersistentId!, heading);
                node.IsExpanded = InitialExpandedState(node);
                _headingNodes[key] = node;
            }
            else
            {
                node.UpdateHeading(heading);
            }

            while (parents.Count > 0 && parents[^1].Heading!.Level >= heading.Level)
                parents.RemoveAt(parents.Count - 1);

            var parent = parents.Count == 0 ? documentNode : parents[^1];
            node.Parent = parent;
            if (ReferenceEquals(parent, documentNode))
                desiredRoots.Add(node);
            else
            {
                if (!desiredChildren.TryGetValue(parent.Key, out var children))
                    desiredChildren[parent.Key] = children = [];
                children.Add(node);
            }
            desiredChildren.TryAdd(node.Key, []);
            parents.Add(node);
        }

        ReconcileCollection(documentNode.Children, desiredRoots);
        foreach (var root in desiredRoots) ReconcileHeadingBranch(root, desiredChildren);

        var prefix = $"heading:{documentNode.PersistentId}:";
        foreach (var stale in _headingNodes.Keys
                     .Where(key => key.StartsWith(prefix, StringComparison.Ordinal) && !liveKeys.Contains(key))
                     .ToArray())
            _headingNodes.Remove(stale);
    }

    private static void ReconcileHeadingBranch(
        ExplorerNode node,
        IReadOnlyDictionary<string, List<ExplorerNode>> desiredChildren)
    {
        var desired = desiredChildren.TryGetValue(node.Key, out var list) ? list : [];
        ReconcileCollection(node.Children, desired);
        foreach (var child in desired) ReconcileHeadingBranch(child, desiredChildren);
    }

    private void RefreshHeadingNavigationData(string documentId, IReadOnlyList<OutlineItemViewModel> headings)
    {
        var occurrence = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var heading in headings)
        {
            var occurrenceBase = $"{heading.Level}\u001f{heading.Title}";
            var ordinal = occurrence.GetValueOrDefault(occurrenceBase);
            occurrence[occurrenceBase] = ordinal + 1;
            var key = HeadingKey(documentId, heading.Level, heading.Title, ordinal);
            if (_headingNodes.TryGetValue(key, out var node)) node.UpdateHeading(heading);
        }
    }

    private async Task IndexDocumentsAsync()
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

                string source;
                try
                {
                    source = await _repository.ReadDocumentAsync(project, row.Node, token);
                }
                catch when (!token.IsCancellationRequested)
                {
                    continue;
                }

                var headings = await Task.Run(() => ParseOutline(source), token);
                if (token.IsCancellationRequested) return;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var expand = string.Equals(
                        _viewModel.SelectedRow?.Node.PersistentId,
                        row.Node.PersistentId,
                        StringComparison.Ordinal);
                    ApplyHeadings(row.Node.PersistentId, headings, expand);
                });
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

    private OutlineItemViewModel[] ParseOutline(string source)
    {
        var ast = _parser.Parse(source);
        var headings = ast.Blocks.OfType<HeadingBlock>().ToArray();
        var totalLines = Math.Max(1, CountLines(source));
        var result = new OutlineItemViewModel[headings.Length];

        for (var index = 0; index < headings.Length; index++)
        {
            var heading = headings[index];
            var endLine = totalLines;
            for (var next = index + 1; next < headings.Length; next++)
            {
                if (headings[next].Level > heading.Level) continue;
                endLine = Math.Max(heading.SourceLine, headings[next].SourceLine - 1);
                break;
            }
            result[index] = new OutlineItemViewModel(
                heading.Inlines.ToPlainText(),
                heading.Level,
                heading.SourceLine,
                endLine);
        }
        return result;
    }

    private async void TreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_disposed || _syncingTreeSelection || _tree.SelectedItem is not ExplorerNode node) return;
        if (node.Kind == ExplorerNodeKind.ProjectRoot) return;

        try
        {
            if (node.Kind == ExplorerNodeKind.Heading)
            {
                await NavigateToHeadingAsync(node);
                return;
            }

            var row = node.Row;
            if (row is null) return;
            if (string.Equals(
                    _viewModel.SelectedRow?.Node.PersistentId,
                    row.Node.PersistentId,
                    StringComparison.Ordinal))
                return;
            await _viewModel.SelectAsync(row);
        }
        catch
        {
            // Navigation failure must not break the explorer selection surface.
        }
    }

    private async Task NavigateToHeadingAsync(ExplorerNode node)
    {
        if (node.Heading is null || string.IsNullOrWhiteSpace(node.OwnerDocumentId)) return;
        var row = _viewModel.BinderRows.FirstOrDefault(candidate =>
            string.Equals(candidate.Node.PersistentId, node.OwnerDocumentId, StringComparison.Ordinal));
        if (row is null) return;

        if (!string.Equals(_viewModel.SelectedRow?.Node.PersistentId, row.Node.PersistentId, StringComparison.Ordinal))
            await _viewModel.SelectAsync(row);

        var heading = _viewModel.OutlineItems.FirstOrDefault(candidate =>
            candidate.Level == node.Heading.Level &&
            candidate.SourceLine == node.Heading.SourceLine &&
            string.Equals(candidate.Title, node.Heading.Title, StringComparison.Ordinal))
            ?? _viewModel.OutlineItems.FirstOrDefault(candidate =>
                candidate.Level == node.Heading.Level &&
                string.Equals(candidate.Title, node.Heading.Title, StringComparison.Ordinal));
        if (heading is not null) _viewModel.SelectOutline(heading);
    }

    private void LegacySelectionChanged(object? sender, SelectionChangedEventArgs e)
        => SyncSelectionFromModel();

    private void SyncSelectionFromModel()
    {
        if (_disposed || _tree.SelectedItem is ExplorerNode { Kind: ExplorerNodeKind.Heading } selectedHeading &&
            string.Equals(selectedHeading.OwnerDocumentId, _viewModel.SelectedRow?.Node.PersistentId, StringComparison.Ordinal))
            return;

        var id = _viewModel.SelectedRow?.Node.PersistentId;
        if (string.IsNullOrWhiteSpace(id) || string.Equals(id, _lastModelSelectionId, StringComparison.Ordinal)) return;
        _lastModelSelectionId = id;
        if (_projectNodes.TryGetValue(id, out var node)) SelectAndReveal(node);
    }

    private void SelectAndReveal(ExplorerNode node)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            parent.IsExpanded = true;
            _expandedKeys.Add(parent.Key);
        }
        ApplyExpansionToRealizedItems();

        _syncingTreeSelection = true;
        try { _tree.SelectedItem = node; }
        finally { _syncingTreeSelection = false; }
        // AutoScrollToSelectedItem is enabled; no private TreeView container APIs are required.
    }

    private void RevealActiveDocument()
    {
        var id = _viewModel.SelectedRow?.Node.PersistentId;
        if (id is not null && _projectNodes.TryGetValue(id, out var node)) SelectAndReveal(node);
    }

    private void CollapseAll()
    {
        foreach (var node in _projectNodes.Values) node.IsExpanded = false;
        foreach (var node in _headingNodes.Values) node.IsExpanded = false;
        _expandedKeys.Clear();
        if (_projectRoot is not null)
        {
            _projectRoot.IsExpanded = true;
            _expandedKeys.Add(_projectRoot.Key);
        }
        SaveExpansionState();
        ApplyExpansionToRealizedItems();
    }

    private void TreeContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is not TreeViewItem item || item.DataContext is not ExplorerNode node) return;
        item.IsExpanded = node.IsExpanded;
        item.MinHeight = 24;
    }

    private void TreeItemExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not TreeViewItem { DataContext: ExplorerNode node }) return;
        node.IsExpanded = true;
        _expandedKeys.Add(node.Key);
        SaveExpansionState();
    }

    private void TreeItemCollapsed(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not TreeViewItem { DataContext: ExplorerNode node }) return;
        node.IsExpanded = false;
        _expandedKeys.Remove(node.Key);
        SaveExpansionState();
    }

    private void ApplyExpansionToRealizedItems()
    {
        foreach (var item in _tree.GetVisualDescendants().OfType<TreeViewItem>())
            if (item.DataContext is ExplorerNode node && item.IsExpanded != node.IsExpanded)
                item.IsExpanded = node.IsExpanded;
    }

    private void TreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var container = FindTreeViewItem(e.Source);
        if (container?.DataContext is not ExplorerNode node) return;
        var point = e.GetCurrentPoint(_tree);

        if (point.Properties.IsRightButtonPressed)
        {
            _tree.SelectedItem = node;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed || node.Row is null) return;
        _dragCandidate = node;
        _dragTrigger = e;
        _dragStart = e.GetPosition(_tree);
    }

    private async void TreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragCandidate?.Row is null || _dragTrigger is null) return;
        if (!e.GetCurrentPoint(_tree).Properties.IsLeftButtonPressed)
        {
            ClearDragCandidate();
            return;
        }

        var current = e.GetPosition(_tree);
        var dx = current.X - _dragStart.X;
        var dy = current.Y - _dragStart.Y;
        if ((dx * dx) + (dy * dy) < 64) return;

        var candidate = _dragCandidate;
        var trigger = _dragTrigger;
        ClearDragCandidate();
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ExplorerDragFormat, candidate));
        await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Move);
    }

    private void TreeDragOver(object? sender, DragEventArgs e)
    {
        var source = e.DataTransfer.TryGetValue(ExplorerDragFormat);
        var target = ExplorerNodeFromEventSource(e.Source);
        e.DragEffects = source?.Row is not null && target?.Row is not null &&
                        _viewModel.CanDropBinderItem(source.Row, target.Row)
            ? DragDropEffects.Move
            : DragDropEffects.None;
    }

    private async void TreeDrop(object? sender, DragEventArgs e)
    {
        var source = e.DataTransfer.TryGetValue(ExplorerDragFormat);
        var target = ExplorerNodeFromEventSource(e.Source);
        if (source?.Row is null || target?.Row is null || !_viewModel.CanDropBinderItem(source.Row, target.Row))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var container = FindTreeViewItem(e.Source);
        var y = container is null ? 0 : e.GetPosition(container).Y;
        var height = Math.Max(1, container?.Bounds.Height ?? 1);
        var into = target.Row.Node.IsContainer && y >= height * 0.25 && y <= height * 0.75;
        var after = !into && y > height / 2;

        try
        {
            await _viewModel.MoveBinderItemAsync(source.Row, target.Row, into, after);
            e.DragEffects = DragDropEffects.Move;
        }
        catch
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private static TreeViewItem? FindTreeViewItem(object? source)
    {
        if (source is TreeViewItem item) return item;
        if (source is not Control control) return null;
        return control.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
    }

    private static ExplorerNode? ExplorerNodeFromEventSource(object? source)
        => FindTreeViewItem(source)?.DataContext as ExplorerNode;

    private void ClearDragCandidate()
    {
        _dragCandidate = null;
        _dragTrigger = null;
    }

    private ContextMenu BuildContextMenu()
    {
        return new ContextMenu
        {
            ItemsSource = new object[]
            {
                ContextAction("New Chapter…", () => AddNodeAsync(NodeKind.Chapter, "New Chapter")),
                ContextAction("New Folder…", () => AddNodeAsync(NodeKind.Folder, "New Folder")),
                ContextAction("New Part…", () => AddNodeAsync(NodeKind.Part, "New Part")),
                new Separator(),
                ContextAction("Rename", RenameSelectedAsync),
                ContextAction("Delete", DeleteSelectedAsync),
                new Separator(),
                ContextAction("Move Up", () => _viewModel.MoveSelectedAsync(-1)),
                ContextAction("Move Down", () => _viewModel.MoveSelectedAsync(1)),
                ContextAction("Include / Exclude", () => _viewModel.ToggleSelectedCompilationAsync()),
                new Separator(),
                ContextAction("Collapse All", () => { CollapseAll(); return Task.CompletedTask; }),
                ContextAction("Reveal Active", () => { RevealActiveDocument(); return Task.CompletedTask; })
            }
        };
    }

    private static MenuItem ContextAction(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) =>
        {
            try { await action(); }
            catch { }
        };
        return item;
    }

    private async Task AddNodeAsync(NodeKind kind, string initialTitle)
    {
        var title = await DesktopDialogService.PromptAsync(_window, $"Add {kind}", "Title", initialTitle);
        if (title is not null) await _viewModel.AddNodeAsync(kind, title);
    }

    private async Task RenameSelectedAsync()
    {
        if (!_viewModel.HasSelection) return;
        var title = await DesktopDialogService.PromptAsync(
            _window,
            "Rename Binder Item",
            "Title",
            _viewModel.SelectedTitle);
        if (title is not null) await _viewModel.RenameSelectedAsync(title);
    }

    private async Task DeleteSelectedAsync()
    {
        if (!_viewModel.HasSelection) return;
        var confirmed = await DesktopDialogService.ConfirmAsync(
            _window,
            "Delete Binder Item",
            $"Delete '{_viewModel.SelectedTitle}' and its on-disk content?",
            "Delete");
        if (confirmed) await _viewModel.DeleteSelectedAsync();
    }

    private void UpdateProjectIdentity(bool force)
    {
        var project = _repository.CurrentProject;
        var path = project?.RootPath;
        if (!force && string.Equals(path, _projectPath, StringComparison.Ordinal)) return;

        _projectPath = path;
        _statePath = path is null ? null : Path.Combine(path, ".typescribe", "project-explorer.tsv");
        _roots.Clear();
        _projectRoot = null;
        _projectNodes.Clear();
        _headingNodes.Clear();
        _headingIndex.Clear();
        _headingSignatures.Clear();
        _expandedKeys.Clear();
        _lastModelSelectionId = null;
        CancelIndexing();
        LoadExpansionState();
    }

    private bool InitialExpandedState(ExplorerNode node)
    {
        if (node.Kind == ExplorerNodeKind.ProjectRoot) return true;
        if (_expandedKeys.Contains(node.Key)) return true;
        if (!_hasStoredExpansionState)
        {
            if (node.Kind == ExplorerNodeKind.Heading) return true;
            return node.Kind == ExplorerNodeKind.ProjectNode && node.Row?.Node.IsContainer == true;
        }
        return false;
    }

    private void LoadExpansionState()
    {
        _hasStoredExpansionState = false;
        if (string.IsNullOrWhiteSpace(_statePath) || !File.Exists(_statePath)) return;
        try
        {
            foreach (var line in File.ReadLines(_statePath))
            {
                var parts = line.Split('\t', 2);
                if (parts.Length == 2 && string.Equals(parts[0], "expanded", StringComparison.Ordinal))
                    _expandedKeys.Add(parts[1]);
            }
            _hasStoredExpansionState = true;
        }
        catch
        {
        }
    }

    private void SaveExpansionState()
    {
        if (string.IsNullOrWhiteSpace(_statePath)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var content = string.Join(
                Environment.NewLine,
                _expandedKeys.Order(StringComparer.Ordinal).Select(static key => $"expanded\t{key}"));
            File.WriteAllText(_statePath, content.Length == 0 ? string.Empty : content + Environment.NewLine);
        }
        catch
        {
            // Explorer UI state persistence must never break navigation.
        }
    }

    private void CancelIndexing()
    {
        _indexCts?.Cancel();
        _indexCts?.Dispose();
        _indexCts = new CancellationTokenSource();
        _indexing = false;
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        CancelIndexing();
        _window.Opened -= WindowOpened;
        _window.Closed -= WindowClosed;
        _viewModel.BinderRows.CollectionChanged -= BinderRowsChanged;
        _viewModel.OutlineItems.CollectionChanged -= OutlineItemsChanged;
        if (_legacySelectionBridge is not null)
            _legacySelectionBridge.SelectionChanged -= LegacySelectionChanged;
        _tree.SelectionChanged -= TreeSelectionChanged;
        _tree.ContainerPrepared -= TreeContainerPrepared;
        _tree.PointerPressed -= TreePointerPressed;
        _tree.PointerMoved -= TreePointerMoved;
    }

    private static void ReconcileCollection(
        ObservableCollection<ExplorerNode> target,
        IReadOnlyList<ExplorerNode> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            var item = desired[index];
            if (index < target.Count && ReferenceEquals(target[index], item)) continue;
            var existing = target.IndexOf(item);
            if (existing >= 0) target.Move(existing, index);
            else target.Insert(index, item);
        }
        while (target.Count > desired.Count) target.RemoveAt(target.Count - 1);
    }

    private static string HeadingVisualSignature(IEnumerable<OutlineItemViewModel> headings)
        => string.Join('\u001e', headings.Select(static heading => $"{heading.Level}\u001f{heading.Title}"));

    private static string ProjectKey(string persistentId) => $"node:{persistentId}";

    private static string HeadingKey(string documentId, int level, string title, int ordinal)
        => $"heading:{documentId}:{level}:{ordinal}:{title}";

    private static int CountLines(string text)
    {
        if (text.Length == 0) return 1;
        var count = 1;
        foreach (var ch in text)
            if (ch == '\n') count++;
        return count;
    }

    private static IBrush LabelBrush(ProjectNode node)
    {
        if (string.IsNullOrWhiteSpace(node.Label)) return Brushes.Transparent;
        unchecked
        {
            var hash = 17;
            foreach (var ch in node.Label) hash = (hash * 31) + ch;
            var index = (hash & int.MaxValue) % LabelPalette.Length;
            return new SolidColorBrush(Color.Parse(LabelPalette[index]));
        }
    }

    private static string NodeIcon(NodeKind kind) => kind switch
    {
        NodeKind.Book => "▣",
        NodeKind.Part => "◆",
        NodeKind.Folder => "▰",
        NodeKind.Chapter => "▯",
        NodeKind.Section => "§",
        NodeKind.Scene => "▪",
        NodeKind.Research => "⌕",
        NodeKind.Note => "✎",
        _ => "·"
    };

    private enum ExplorerNodeKind
    {
        ProjectRoot,
        ProjectNode,
        Heading
    }

    private sealed class ExplorerNode : INotifyPropertyChanged
    {
        private string _title;
        private string _icon;
        private string _trailingText;
        private IBrush _accentBrush;
        private OutlineItemViewModel? _heading;

        private ExplorerNode(
            string key,
            ExplorerNodeKind kind,
            string title,
            string icon,
            string trailingText,
            IBrush accentBrush)
        {
            Key = key;
            Kind = kind;
            _title = title;
            _icon = icon;
            _trailingText = trailingText;
            _accentBrush = accentBrush;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Key { get; }
        public ExplorerNodeKind Kind { get; }
        public ObservableCollection<ExplorerNode> Children { get; } = [];
        public ExplorerNode? Parent { get; set; }
        public BinderRowViewModel? Row { get; private set; }
        public string? PersistentId => Row?.Node.PersistentId;
        public string? OwnerDocumentId { get; private set; }
        public OutlineItemViewModel? Heading => _heading;
        public bool IsExpanded { get; set; }
        public string Title => _title;
        public string Icon => _icon;
        public string TrailingText => _trailingText;
        public IBrush AccentBrush => _accentBrush;
        public string ToolTipText => Kind switch
        {
            ExplorerNodeKind.ProjectRoot => _title,
            ExplorerNodeKind.Heading => _heading is null ? _title : $"H{_heading.Level} · line {_heading.SourceLine}",
            _ => Row is null
                ? _title
                : $"{Row.Node.Kind} · {Row.Node.Status} · {(Row.Node.IncludeInCompilation ? "included" : "excluded")}" 
        };

        public static ExplorerNode CreateProjectRoot(string title, string rootPath)
            => new($"project:{rootPath}", ExplorerNodeKind.ProjectRoot, title, "▦", string.Empty, Brushes.Transparent);

        public static ExplorerNode CreateProjectNode(BinderRowViewModel row)
        {
            var node = new ExplorerNode(
                ProjectKey(row.Node.PersistentId),
                ExplorerNodeKind.ProjectNode,
                row.Node.Title,
                NodeIcon(row.Node.Kind),
                row.Node.IsDocument ? (row.Node.IncludeInCompilation ? "●" : "○") : string.Empty,
                LabelBrush(row.Node));
            node.Row = row;
            return node;
        }

        public static ExplorerNode CreateHeading(string key, string documentId, OutlineItemViewModel heading)
        {
            var node = new ExplorerNode(
                key,
                ExplorerNodeKind.Heading,
                heading.Title,
                $"H{heading.Level}",
                string.Empty,
                Brushes.Transparent);
            node.OwnerDocumentId = documentId;
            node._heading = heading;
            return node;
        }

        public void SetTitle(string title) => Set(ref _title, title, nameof(Title));

        public void UpdateProjectRow(BinderRowViewModel row)
        {
            Row = row;
            Set(ref _title, row.Node.Title, nameof(Title));
            Set(ref _icon, NodeIcon(row.Node.Kind), nameof(Icon));
            Set(
                ref _trailingText,
                row.Node.IsDocument ? (row.Node.IncludeInCompilation ? "●" : "○") : string.Empty,
                nameof(TrailingText));
            Set(ref _accentBrush, LabelBrush(row.Node), nameof(AccentBrush));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTipText)));
        }

        public void UpdateHeading(OutlineItemViewModel heading)
        {
            _heading = heading;
            Set(ref _title, heading.Title, nameof(Title));
            Set(ref _icon, $"H{heading.Level}", nameof(Icon));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTipText)));
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Small AOT-safe header view. It observes the strongly typed explorer node directly rather
    /// than using reflection bindings, so Native AOT does not need dynamic binding metadata.
    /// </summary>
    private sealed class ExplorerNodeHeader : Grid
    {
        private readonly ExplorerNode _node;
        private readonly Border _accent;
        private readonly TextBlock _icon;
        private readonly TextBlock _title;
        private readonly TextBlock _trailing;

        public ExplorerNodeHeader(ExplorerNode node)
        {
            _node = node;
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto");
            MinHeight = 23;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Center;

            _accent = new Border
            {
                Width = 3,
                Height = 16,
                CornerRadius = new CornerRadius(1),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            _icon = new TextBlock
            {
                Width = 24,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _title = new TextBlock
            {
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            _trailing = new TextBlock
            {
                FontSize = 10,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 4, 0)
            };

            Children.Add(_accent);
            Grid.SetColumn(_icon, 1);
            Children.Add(_icon);
            Grid.SetColumn(_title, 2);
            Children.Add(_title);
            Grid.SetColumn(_trailing, 3);
            Children.Add(_trailing);

            Refresh();
            _node.PropertyChanged += NodePropertyChanged;
            DetachedFromVisualTree += HeaderDetached;
        }

        private void NodePropertyChanged(object? sender, PropertyChangedEventArgs e)
            => Refresh();

        private void HeaderDetached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            _node.PropertyChanged -= NodePropertyChanged;
            DetachedFromVisualTree -= HeaderDetached;
        }

        private void Refresh()
        {
            _accent.Background = _node.AccentBrush;
            _icon.Text = _node.Icon;
            _icon.FontSize = _node.Kind == ExplorerNodeKind.Heading ? 10 : 13;
            _icon.Opacity = _node.Kind == ExplorerNodeKind.Heading ? 0.7 : 0.86;
            _title.Text = _node.Title;
            _title.FontSize = _node.Kind == ExplorerNodeKind.Heading ? 12 : 13;
            _title.FontWeight = _node.Kind == ExplorerNodeKind.ProjectRoot ? FontWeight.SemiBold : FontWeight.Normal;
            _title.Opacity = _node.Kind == ExplorerNodeKind.Heading ? 0.78 : 1;
            _trailing.Text = _node.TrailingText;
            ToolTip.SetTip(this, _node.ToolTipText);
        }
    }
}
