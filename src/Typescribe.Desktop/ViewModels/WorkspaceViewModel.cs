using System.Collections.ObjectModel;
using System.Text;
using Typescribe.Application.Abstractions;
using Typescribe.Application.Models;
using Typescribe.Application.Services;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop.ViewModels;

public sealed class WorkspaceViewModel(
    IProjectRepository repository,
    IDocumentParser parser,
    IDocumentRenderer renderer,
    WordCountService wordCountService,
    ProjectSearchService searchService,
    IDocumentExportService exportService) : IAsyncDisposable
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private CancellationTokenSource? _autosaveCts;
    private CancellationTokenSource? _previewCts;
    private BookProject? _project;
    private ProjectNode? _selectedNode;
    private string _editorText = string.Empty;
    private bool _suppressEditorChanges;
    private bool _isDirty;
    private bool _previewWholeBook;
    private long _editVersion;
    private long _previewBuildVersion;
    private long _editorNavigationVersion;
    private long _corkboardVersion;

    public event EventHandler? StateChanged;
    public ObservableCollection<BinderRowViewModel> BinderRows { get; } = [];
    public ObservableCollection<SearchHit> SearchResults { get; } = [];
    public ObservableCollection<OutlineItemViewModel> OutlineItems { get; } = [];
    public ObservableCollection<CorkboardCardViewModel> CorkboardCards { get; } = [];
    public ObservableCollection<SnapshotInfo> Snapshots { get; } = [];

    public string ProjectTitle => _project?.Title ?? "Typescribe";
    public string EditorText => _editorText;
    public string PreviewText { get; private set; } = "Open or create a Typescribe project to begin.";
    public int WordCount { get; private set; }
    public string Status { get; private set; } = "Ready";
    public BinderRowViewModel? SelectedRow { get; private set; }
    public OutlineItemViewModel? SelectedOutline { get; private set; }
    public bool HasProject => _project is not null;
    public bool HasSelection => _selectedNode is not null;
    public bool HasDocument => _selectedNode?.IsDocument == true;
    public bool SelectedIsContainer => _selectedNode?.IsContainer == true;
    public bool SelectedIncluded => _selectedNode?.IncludeInCompilation ?? false;
    public string SelectedTitle => _selectedNode?.Title ?? string.Empty;
    public string SelectedSynopsis => _selectedNode?.Synopsis ?? string.Empty;
    public string SelectedNotes => _selectedNode?.Notes ?? string.Empty;
    public string SelectedStatus => _selectedNode?.Status ?? "Draft";
    public string SelectedLabel => _selectedNode?.Label ?? string.Empty;
    public string SelectedKeywords => _selectedNode?.Keywords ?? string.Empty;
    public int SelectedTargetWords => _selectedNode?.TargetWords ?? 0;
    public double SelectedTargetProgress => SelectedTargetWords <= 0 ? 0 : Math.Clamp(WordCount / (double)SelectedTargetWords, 0, 1);
    public string SelectedTargetText => SelectedTargetWords <= 0
        ? $"{WordCount:N0} words • no target"
        : $"{WordCount:N0} / {SelectedTargetWords:N0} words ({SelectedTargetProgress:P0})";
    public string CorkboardTitle { get; private set; } = "Corkboard";
    public long CorkboardVersion => _corkboardVersion;
    public BookStyle CurrentStyle => _project?.Style ?? BookStyle.Default;
    public bool CanPublishPdf => exportService.CanPublishPdf;
    public string PublishingEngineName => exportService.PublishingEngineName;
    public string? LivePreviewPdfPath { get; private set; }
    public long LivePreviewVersion { get; private set; }
    public bool IsLivePreviewBuilding { get; private set; }
    public string? LivePreviewError { get; private set; }
    public bool PreviewWholeBook => _previewWholeBook;
    public string PreviewScopeLabel => _previewWholeBook
        ? "Whole Book"
        : SelectedOutline is not null
            ? $"Section: {SelectedOutline.Title}"
            : HasDocument
                ? $"Document: {SelectedTitle}"
                : "Whole Book";
    public int EditorNavigationLine { get; private set; } = 1;
    public long EditorNavigationVersion => _editorNavigationVersion;

    public async Task CreateProjectAsync(string folder, CancellationToken cancellationToken = default)
    {
        await FlushAutosaveAsync();
        ResetSelection();
        var title = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        if (string.IsNullOrWhiteSpace(title)) title = "Untitled Book";
        _project = await repository.CreateAsync(folder, title, cancellationToken);
        await LoadProjectAsync(cancellationToken);
        SetStatus($"Created {title}");
    }

    public async Task OpenProjectAsync(string folder, CancellationToken cancellationToken = default)
    {
        await FlushAutosaveAsync();
        ResetSelection();
        _project = await repository.OpenAsync(folder, cancellationToken);
        await LoadProjectAsync(cancellationToken);
        SetStatus($"Opened {_project.Title}");
    }

    public async Task SelectAsync(BinderRowViewModel? row, CancellationToken cancellationToken = default)
    {
        if (row is null || _project is null) return;
        await FlushAutosaveAsync();

        SelectedRow = row;
        _selectedNode = row.Node;
        SelectedOutline = null;
        _suppressEditorChanges = true;
        try
        {
            if (_selectedNode.IsDocument)
            {
                _editorText = await repository.ReadDocumentAsync(_project, _selectedNode, cancellationToken);
                _isDirty = false;
                _editVersion = 0;
                RecomputeDocumentState();
                RequestEditorNavigation(1);
                await RefreshSnapshotsAsync(cancellationToken);
                SetStatus($"Editing {_selectedNode.Title}");
            }
            else
            {
                _editorText = string.Empty;
                _isDirty = false;
                _editVersion = 0;
                OutlineItems.Clear();
                Snapshots.Clear();
                PreviewText = $"{_selectedNode.Kind}: {_selectedNode.Title}";
                WordCount = 0;
                SetStatus($"Selected {_selectedNode.Title}");
            }

            await RefreshCorkboardAsync(cancellationToken);
        }
        finally
        {
            _suppressEditorChanges = false;
        }

        ScheduleLivePdfPreview();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateEditorText(string text)
    {
        if (_suppressEditorChanges || _selectedNode is null || !_selectedNode.IsDocument || _project is null) return;
        _editorText = text ?? string.Empty;
        _isDirty = true;
        _editVersion++;
        RecomputeDocumentState();
        UpdateActiveCorkboardWordCount();
        ScheduleAutosave();
        ScheduleLivePdfPreview();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task AddChapterAsync(CancellationToken cancellationToken = default)
    {
        var number = BinderRows.Count(static row => row.Node.Kind == NodeKind.Chapter) + 1;
        await AddNodeAsync(NodeKind.Chapter, $"Chapter {number}", cancellationToken);
    }

    public async Task AddNodeAsync(NodeKind kind, string title, CancellationToken cancellationToken = default)
    {
        if (_project is null) return;
        await FlushAutosaveAsync();
        var node = await repository.AddNodeAsync(_project, _selectedNode, kind, title, cancellationToken);
        RefreshBinder();
        var row = BinderRows.First(candidate => ReferenceEquals(candidate.Node, node));
        await SelectAsync(row, cancellationToken);
        ScheduleLivePdfPreview();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RenameSelectedAsync(string newTitle, CancellationToken cancellationToken = default)
    {
        if (_project is null || _selectedNode is null) return;
        await FlushAutosaveAsync();
        var node = _selectedNode;
        await repository.RenameNodeAsync(_project, node, newTitle, cancellationToken);
        RefreshBinder();
        var row = BinderRows.FirstOrDefault(candidate => ReferenceEquals(candidate.Node, node));
        if (row is not null) await SelectAsync(row, cancellationToken);
        ScheduleLivePdfPreview();
        SetStatus($"Renamed to {newTitle.Trim()}");
    }

    public async Task DeleteSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (_project is null || _selectedNode is null) return;
        await FlushAutosaveAsync();
        var title = _selectedNode.Title;
        await repository.DeleteNodeAsync(_project, _selectedNode, cancellationToken);
        ResetSelection();
        RefreshBinder();
        var next = BinderRows.FirstOrDefault(static row => row.Node.IsDocument) ?? BinderRows.FirstOrDefault();
        if (next is not null) await SelectAsync(next, cancellationToken);
        ScheduleLivePdfPreview();
        SetStatus($"Deleted {title}");
    }

    public async Task MoveSelectedAsync(int offset, CancellationToken cancellationToken = default)
    {
        if (_project is null || _selectedNode is null || offset == 0) return;
        await FlushAutosaveAsync();
        var node = _selectedNode;
        if (!await repository.MoveNodeAsync(_project, node, offset, cancellationToken)) return;
        RefreshBinder();
        SelectedRow = BinderRows.FirstOrDefault(candidate => ReferenceEquals(candidate.Node, node));
        await RefreshCorkboardAsync(cancellationToken);
        ScheduleLivePdfPreview();
        SetStatus(offset < 0 ? "Moved binder item up" : "Moved binder item down");
    }

    public bool CanDropBinderItem(BinderRowViewModel? source, BinderRowViewModel? target)
    {
        if (_project is null || source is null || target is null) return false;
        if (ReferenceEquals(source.Node, target.Node)) return false;
        return !ContainsNode(source.Node, target.Node);
    }

    public async Task MoveBinderItemAsync(
        BinderRowViewModel source,
        BinderRowViewModel target,
        bool dropIntoTarget,
        bool insertAfterTarget = false,
        CancellationToken cancellationToken = default)
    {
        if (_project is null || !CanDropBinderItem(source, target)) return;
        await FlushAutosaveAsync();

        var destinationParent = dropIntoTarget && target.Node.IsContainer
            ? target.Node
            : FindParent(_project.Root, target.Node) ?? _project.Root;
        var targetIndex = ReferenceEquals(destinationParent, target.Node)
            ? destinationParent.Children.Count
            : Math.Max(0, destinationParent.IndexOf(target.Node) + (insertAfterTarget ? 1 : 0));

        if (!await repository.ReparentNodeAsync(_project, source.Node, destinationParent, targetIndex, cancellationToken)) return;

        RefreshBinder();
        SelectedRow = BinderRows.FirstOrDefault(candidate => ReferenceEquals(candidate.Node, source.Node));
        _selectedNode = source.Node;
        await RefreshCorkboardAsync(cancellationToken);
        ScheduleLivePdfPreview();
        var placement = dropIntoTarget && target.Node.IsContainer
            ? $"into {target.Node.Title}"
            : insertAfterTarget
                ? $"after {target.Node.Title}"
                : $"before {target.Node.Title}";
        SetStatus($"Moved {source.Node.Title} {placement}");
    }

    public async Task ToggleSelectedCompilationAsync(CancellationToken cancellationToken = default)
    {
        if (_project is null || _selectedNode is null) return;
        var include = !_selectedNode.IncludeInCompilation;
        await repository.SetCompilationIncludedAsync(_project, _selectedNode, include, cancellationToken);
        RefreshBinder();
        SelectedRow = BinderRows.FirstOrDefault(candidate => ReferenceEquals(candidate.Node, _selectedNode));
        await RefreshCorkboardAsync(cancellationToken);
        ScheduleLivePdfPreview();
        SetStatus(include ? "Included in compilation" : "Excluded from compilation");
    }

    public async Task SaveSelectedMetadataAsync(
        string synopsis,
        string notes,
        string status,
        string label,
        string keywords,
        int targetWords,
        CancellationToken cancellationToken = default)
    {
        if (_project is null || _selectedNode is null) return;
        await repository.SaveNodeMetadataAsync(
            _project,
            _selectedNode,
            synopsis,
            notes,
            status,
            label,
            keywords,
            targetWords,
            cancellationToken);
        await RefreshCorkboardAsync(cancellationToken);
        SetStatus("Inspector metadata saved");
    }

    public async Task<SnapshotInfo?> CreateSnapshotAsync(string label, CancellationToken cancellationToken = default)
    {
        if (_project is null || _selectedNode?.IsDocument != true) return null;
        var snapshot = await repository.CreateSnapshotAsync(
            _project,
            _selectedNode,
            _editorText,
            label,
            cancellationToken);
        await RefreshSnapshotsAsync(cancellationToken);
        SetStatus($"Snapshot created {snapshot.CreatedAt.LocalDateTime:g}");
        return snapshot;
    }

    public async Task RestoreSnapshotAsync(SnapshotInfo snapshot, CancellationToken cancellationToken = default)
    {
        if (_project is null || _selectedNode?.IsDocument != true) return;
        ArgumentNullException.ThrowIfNull(snapshot);

        await repository.CreateSnapshotAsync(
            _project,
            _selectedNode,
            _editorText,
            $"Before restore {DateTime.Now:g}",
            cancellationToken);

        _editorText = await repository.ReadSnapshotAsync(_project, _selectedNode, snapshot, cancellationToken);
        _isDirty = true;
        _editVersion++;
        RecomputeDocumentState();
        UpdateActiveCorkboardWordCount();
        ScheduleAutosave();
        ScheduleLivePdfPreview(TimeSpan.Zero);
        await RefreshSnapshotsAsync(cancellationToken);
        RequestEditorNavigation(1);
        SetStatus($"Restored snapshot from {snapshot.CreatedAt.LocalDateTime:g}");
    }

    public async Task DeleteSnapshotAsync(SnapshotInfo snapshot, CancellationToken cancellationToken = default)
    {
        if (_project is null || _selectedNode?.IsDocument != true) return;
        ArgumentNullException.ThrowIfNull(snapshot);
        await repository.DeleteSnapshotAsync(_project, _selectedNode, snapshot, cancellationToken);
        await RefreshSnapshotsAsync(cancellationToken);
        SetStatus("Snapshot deleted");
    }

    public async Task SelectCorkboardCardAsync(CorkboardCardViewModel? card, CancellationToken cancellationToken = default)
    {
        if (card is null) return;
        var row = BinderRows.FirstOrDefault(candidate => ReferenceEquals(candidate.Node, card.Node));
        if (row is not null) await SelectAsync(row, cancellationToken);
    }

    public async Task UpdateStyleAsync(BookStyle style, CancellationToken cancellationToken = default)
    {
        if (_project is null) return;
        await repository.SaveStyleAsync(_project, style.Validate(), cancellationToken);
        RecomputeDocumentState();
        ScheduleLivePdfPreview();
        SetStatus($"Applied style: {style.Name}");
    }

    public void SetPreviewWholeBook(bool enabled)
    {
        if (_previewWholeBook == enabled) return;
        _previewWholeBook = enabled;
        ScheduleLivePdfPreview(TimeSpan.Zero);
        SetStatus(enabled ? "Preview scope: whole book" : $"Preview scope: {PreviewScopeLabel}");
    }

    public void SelectOutline(OutlineItemViewModel? item)
    {
        if (!HasDocument) return;
        SelectedOutline = item;
        if (item is not null)
        {
            RequestEditorNavigation(item.SourceLine);
            SetStatus($"Focused heading: {item.Title}");
        }
        else
        {
            RequestEditorNavigation(1);
            SetStatus($"Previewing {SelectedTitle}");
        }
        ScheduleLivePdfPreview(TimeSpan.Zero);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SearchAsync(
        string query,
        SearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        SearchResults.Clear();
        if (_project is null || string.IsNullOrWhiteSpace(query))
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var results = await searchService.SearchAsync(_project, query.Trim(), options, cancellationToken);
        foreach (var result in results) SearchResults.Add(result);
        SetStatus($"{results.Count} search result(s)");
    }

    public async Task GoToSearchHitAsync(SearchHit? hit, CancellationToken cancellationToken = default)
    {
        if (hit is null) return;
        var row = BinderRows.FirstOrDefault(candidate => candidate.Node.Id == hit.NodeId);
        if (row is null) return;
        await SelectAsync(row, cancellationToken);
        if (hit.Line > 0) RequestEditorNavigation(hit.Line);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task EnsurePdfEngineAsync(CancellationToken cancellationToken = default)
    {
        if (!CanPublishPdf)
        {
            SetStatus("Downloading and verifying portable LuaLaTeX…");
            await exportService.EnsurePdfEngineAsync(cancellationToken);
        }

        SetStatus($"PDF engine ready: {PublishingEngineName}");
    }

    public async Task RefreshLivePdfPreviewAsync(CancellationToken cancellationToken = default)
    {
        EnsureProject();
        if (!CanPublishPdf) await EnsurePdfEngineAsync(cancellationToken);
        await BuildLivePdfPreviewCoreAsync(cancellationToken);
    }

    public async Task ExportPdfAsync(string destination, CancellationToken cancellationToken = default)
    {
        EnsureProject();
        await FlushAutosaveAsync();
        if (!CanPublishPdf) await EnsurePdfEngineAsync(cancellationToken);

        SetStatus("Publishing book PDF with LuaLaTeX…");
        var source = await BuildCompilationSourceAsync(cancellationToken, useEditorBuffer: false);
        await exportService.ExportPdfAsync(source, _project!.Title, _project.Style, destination, cancellationToken);
        SetStatus($"Published {Path.GetFileName(destination)}");
    }

    public async Task ExportLatexAsync(string destination, CancellationToken cancellationToken = default)
    {
        EnsureProject();
        await FlushAutosaveAsync();
        var source = await BuildCompilationSourceAsync(cancellationToken, useEditorBuffer: false);
        await exportService.ExportLatexAsync(source, _project!.Title, _project.Style, destination, cancellationToken);
        SetStatus($"Exported {Path.GetFileName(destination)}");
    }

    public async Task SaveNowAsync(CancellationToken cancellationToken = default)
    {
        _autosaveCts?.Cancel();
        if (_project is null || _selectedNode is null || !_selectedNode.IsDocument) return;
        await SaveCurrentDocumentAsync(cancellationToken);
    }

    private async Task LoadProjectAsync(CancellationToken cancellationToken)
    {
        RefreshBinder();
        var first = BinderRows.FirstOrDefault(static row => row.Node.IsDocument) ?? BinderRows.FirstOrDefault();
        if (first is not null) await SelectAsync(first, cancellationToken);
        ScheduleLivePdfPreview();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshBinder()
    {
        BinderRows.Clear();
        if (_project is null) return;
        foreach (var row in Flatten(_project.Root, 0)) BinderRows.Add(row);
    }

    private static IEnumerable<BinderRowViewModel> Flatten(ProjectNode node, int depth)
    {
        foreach (var child in node.Children)
        {
            var row = new BinderRowViewModel(child, depth);
            yield return row;
            foreach (var descendant in Flatten(child, depth + 1)) yield return descendant;
        }
    }

    private void RecomputeDocumentState()
    {
        if (_selectedNode?.IsDocument != true)
        {
            PreviewText = _selectedNode is null ? "Select a manuscript document to begin." : $"{_selectedNode.Kind}: {_selectedNode.Title}";
            WordCount = 0;
            OutlineItems.Clear();
            SelectedOutline = null;
            return;
        }

        var previousHeading = SelectedOutline;
        var ast = parser.Parse(_editorText);
        PreviewText = renderer.RenderPreview(ast);
        WordCount = wordCountService.Count(_editorText);
        RebuildOutline(ast, previousHeading);
    }

    private void RebuildOutline(DocumentAst ast, OutlineItemViewModel? previousHeading)
    {
        var headings = ast.Blocks.OfType<HeadingBlock>().ToArray();
        var totalLines = Math.Max(1, CountLines(_editorText));
        var items = new List<OutlineItemViewModel>(headings.Length);

        for (var index = 0; index < headings.Length; index++)
        {
            var heading = headings[index];
            var endLine = totalLines;
            for (var next = index + 1; next < headings.Length; next++)
            {
                if (headings[next].Level <= heading.Level)
                {
                    endLine = Math.Max(heading.SourceLine, headings[next].SourceLine - 1);
                    break;
                }
            }
            items.Add(new OutlineItemViewModel(
                heading.Inlines.ToPlainText(),
                heading.Level,
                heading.SourceLine,
                endLine));
        }

        OutlineItems.Clear();
        foreach (var item in items) OutlineItems.Add(item);

        if (previousHeading is null)
        {
            SelectedOutline = null;
            return;
        }

        SelectedOutline = items.FirstOrDefault(item =>
            item.Level == previousHeading.Level &&
            string.Equals(item.Title, previousHeading.Title, StringComparison.Ordinal));
    }

    private async Task RefreshSnapshotsAsync(CancellationToken cancellationToken)
    {
        Snapshots.Clear();
        if (_project is null || _selectedNode?.IsDocument != true) return;
        var snapshots = await repository.ListSnapshotsAsync(_project, _selectedNode, cancellationToken);
        foreach (var snapshot in snapshots) Snapshots.Add(snapshot);
    }

    private async Task RefreshCorkboardAsync(CancellationToken cancellationToken)
    {
        CorkboardCards.Clear();
        if (_project is null)
        {
            CorkboardTitle = "Corkboard";
            _corkboardVersion++;
            return;
        }

        ProjectNode container;
        if (_selectedNode is null)
        {
            container = _project.Root;
        }
        else if (_selectedNode.IsContainer)
        {
            container = _selectedNode;
        }
        else
        {
            container = FindParent(_project.Root, _selectedNode) ?? _project.Root;
        }

        CorkboardTitle = container == _project.Root ? _project.Title : container.Title;
        foreach (var child in container.Children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var words = await CountNodeWordsAsync(child, cancellationToken);
            CorkboardCards.Add(new CorkboardCardViewModel(child, words));
        }
        _corkboardVersion++;
    }

    private async Task<int> CountNodeWordsAsync(ProjectNode node, CancellationToken cancellationToken)
    {
        if (_project is null) return 0;
        if (node.IsDocument)
        {
            var content = ReferenceEquals(node, _selectedNode)
                ? _editorText
                : await repository.ReadDocumentAsync(_project, node, cancellationToken);
            return wordCountService.Count(content);
        }

        var total = 0;
        foreach (var child in node.Children)
            total += await CountNodeWordsAsync(child, cancellationToken);
        return total;
    }

    private void UpdateActiveCorkboardWordCount()
    {
        if (_selectedNode is null) return;
        var card = CorkboardCards.FirstOrDefault(candidate => ReferenceEquals(candidate.Node, _selectedNode));
        if (card is null) return;
        card.CurrentWords = WordCount;
        _corkboardVersion++;
    }

    private void ScheduleAutosave()
    {
        _autosaveCts?.Cancel();
        _autosaveCts?.Dispose();
        _autosaveCts = new CancellationTokenSource();
        var token = _autosaveCts.Token;
        _ = AutosaveAfterDelayAsync(token);
    }

    private async Task AutosaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(850), cancellationToken);
            await SaveCurrentDocumentAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetStatus($"Autosave failed: {ex.Message}");
        }
    }

    private void ScheduleLivePdfPreview(TimeSpan? delay = null)
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;

        if (_project is null || !CanPublishPdf)
        {
            if (_project is not null && !CanPublishPdf)
                LivePreviewError = "Install LuaLaTeX to enable live PDF preview.";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _previewCts = new CancellationTokenSource();
        _ = LivePreviewAfterDelayAsync(delay ?? TimeSpan.FromMilliseconds(550), _previewCts.Token);
    }

    private async Task LivePreviewAfterDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            await BuildLivePdfPreviewCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LivePreviewError = ex.Message;
            IsLivePreviewBuilding = false;
            Status = "Live PDF preview failed";
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task BuildLivePdfPreviewCoreAsync(CancellationToken cancellationToken)
    {
        if (_project is null || !CanPublishPdf) return;

        await _previewGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsLivePreviewBuilding = true;
            LivePreviewError = null;
            Status = $"Updating live PDF preview — {PreviewScopeLabel}…";
            StateChanged?.Invoke(this, EventArgs.Empty);

            var source = await BuildPreviewSourceAsync(cancellationToken);
            var buildDirectory = Path.Combine(_project.RootPath, "build");
            Directory.CreateDirectory(buildDirectory);
            var destination = Path.Combine(buildDirectory, "live-preview.pdf");
            var previewTitle = HasDocument && !_previewWholeBook ? SelectedTitle : _project.Title;
            await exportService.ExportPdfPreviewAsync(source, previewTitle, _project.Style, destination, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            LivePreviewPdfPath = destination;
            LivePreviewVersion = Interlocked.Increment(ref _previewBuildVersion);
            Status = $"Live PDF preview updated — {PreviewScopeLabel}";
        }
        finally
        {
            IsLivePreviewBuilding = false;
            _previewGate.Release();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<string> BuildPreviewSourceAsync(CancellationToken cancellationToken)
    {
        if (_project is null) throw new InvalidOperationException("Open a project first.");
        if (_previewWholeBook || _selectedNode?.IsDocument != true)
            return await BuildCompilationSourceAsync(cancellationToken, useEditorBuffer: true);

        cancellationToken.ThrowIfCancellationRequested();
        if (SelectedOutline is null) return _editorText;
        return SliceLines(_editorText, SelectedOutline.SourceLine, SelectedOutline.EndLine);
    }

    private async Task SaveCurrentDocumentAsync(CancellationToken cancellationToken)
    {
        if (_project is null || _selectedNode is null || !_selectedNode.IsDocument || !_isDirty) return;

        var project = _project;
        var node = _selectedNode;
        var content = _editorText;
        var version = _editVersion;

        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            await repository.SaveDocumentAsync(project, node, content, cancellationToken);
            if (ReferenceEquals(project, _project) && ReferenceEquals(node, _selectedNode) && version == _editVersion)
                _isDirty = false;
            SetStatus($"Saved {DateTime.Now:t}");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task FlushAutosaveAsync()
    {
        _autosaveCts?.Cancel();
        _autosaveCts?.Dispose();
        _autosaveCts = null;
        if (_project is not null && _selectedNode?.IsDocument == true)
            await SaveCurrentDocumentAsync(CancellationToken.None);
    }

    private void ResetSelection()
    {
        _autosaveCts?.Cancel();
        _autosaveCts?.Dispose();
        _autosaveCts = null;
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
        _selectedNode = null;
        SelectedRow = null;
        SelectedOutline = null;
        OutlineItems.Clear();
        Snapshots.Clear();
        CorkboardCards.Clear();
        CorkboardTitle = "Corkboard";
        _corkboardVersion++;
        _editorText = string.Empty;
        _isDirty = false;
        _editVersion = 0;
        PreviewText = "Select a manuscript document to begin.";
        WordCount = 0;
        LivePreviewPdfPath = null;
        LivePreviewVersion = 0;
        LivePreviewError = null;
        IsLivePreviewBuilding = false;
        EditorNavigationLine = 1;
    }

    private async Task<string> BuildCompilationSourceAsync(CancellationToken cancellationToken, bool useEditorBuffer)
    {
        if (_project is null) throw new InvalidOperationException("Open a project first.");
        var builder = new StringBuilder();
        await foreach (var (node, content) in repository.EnumerateDocumentsAsync(_project, cancellationToken))
        {
            var effectiveContent = useEditorBuffer && ReferenceEquals(node, _selectedNode)
                ? _editorText
                : content;
            if (builder.Length > 0) builder.AppendLine().AppendLine();
            builder.Append(effectiveContent.TrimEnd()).AppendLine();
        }
        return builder.ToString();
    }

    private void RequestEditorNavigation(int line)
    {
        EditorNavigationLine = Math.Max(1, line);
        _editorNavigationVersion++;
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0) return 1;
        var count = 1;
        foreach (var ch in text)
            if (ch == '\n') count++;
        return count;
    }

    private static string SliceLines(string text, int startLine, int endLine)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var start = Math.Clamp(startLine - 1, 0, Math.Max(0, lines.Length - 1));
        var endExclusive = Math.Clamp(endLine, start + 1, lines.Length);
        return string.Join(Environment.NewLine, lines[start..endExclusive]);
    }

    private static ProjectNode? FindParent(ProjectNode root, ProjectNode target)
    {
        foreach (var child in root.Children)
        {
            if (ReferenceEquals(child, target)) return root;
            var parent = FindParent(child, target);
            if (parent is not null) return parent;
        }
        return null;
    }

    private static bool ContainsNode(ProjectNode root, ProjectNode target)
    {
        foreach (var child in root.Children)
        {
            if (ReferenceEquals(child, target) || ContainsNode(child, target)) return true;
        }
        return false;
    }

    private void EnsureProject()
    {
        if (_project is null) throw new InvalidOperationException("Open a project first.");
    }

    private void SetStatus(string status)
    {
        Status = status;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        _previewCts?.Cancel();
        await FlushAutosaveAsync();
        _saveGate.Dispose();
        _previewGate.Dispose();
        _autosaveCts?.Dispose();
        _previewCts?.Dispose();
    }
}
