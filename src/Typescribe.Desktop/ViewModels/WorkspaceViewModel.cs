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
    private long _editVersion;
    private long _previewBuildVersion;

    public event EventHandler? StateChanged;
    public ObservableCollection<BinderRowViewModel> BinderRows { get; } = [];
    public ObservableCollection<SearchHit> SearchResults { get; } = [];

    public string ProjectTitle => _project?.Title ?? "Typescribe";
    public string EditorText => _editorText;
    public string PreviewText { get; private set; } = "Open or create a Typescribe project to begin.";
    public int WordCount { get; private set; }
    public string Status { get; private set; } = "Ready";
    public BinderRowViewModel? SelectedRow { get; private set; }
    public bool HasProject => _project is not null;
    public bool HasSelection => _selectedNode is not null;
    public bool HasDocument => _selectedNode?.IsDocument == true;
    public bool SelectedIsContainer => _selectedNode?.IsContainer == true;
    public bool SelectedIncluded => _selectedNode?.IncludeInCompilation ?? false;
    public string SelectedTitle => _selectedNode?.Title ?? string.Empty;
    public BookStyle CurrentStyle => _project?.Style ?? BookStyle.Default;
    public bool CanPublishPdf => exportService.CanPublishPdf;
    public string PublishingEngineName => exportService.PublishingEngineName;
    public string? LivePreviewPdfPath { get; private set; }
    public long LivePreviewVersion { get; private set; }
    public bool IsLivePreviewBuilding { get; private set; }
    public string? LivePreviewError { get; private set; }

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
        _suppressEditorChanges = true;
        try
        {
            if (_selectedNode.IsDocument)
            {
                _editorText = await repository.ReadDocumentAsync(_project, _selectedNode, cancellationToken);
                _isDirty = false;
                _editVersion = 0;
                RecomputeDocumentState();
                SetStatus($"Editing {_selectedNode.Title}");
            }
            else
            {
                _editorText = string.Empty;
                _isDirty = false;
                _editVersion = 0;
                PreviewText = $"{_selectedNode.Kind}: {_selectedNode.Title}";
                WordCount = 0;
                SetStatus($"Selected {_selectedNode.Title}");
            }
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
        ScheduleLivePdfPreview();
        SetStatus(offset < 0 ? "Moved binder item up" : "Moved binder item down");
    }

    public async Task ToggleSelectedCompilationAsync(CancellationToken cancellationToken = default)
    {
        if (_project is null || _selectedNode is null) return;
        var include = !_selectedNode.IncludeInCompilation;
        await repository.SetCompilationIncludedAsync(_project, _selectedNode, include, cancellationToken);
        RefreshBinder();
        SelectedRow = BinderRows.FirstOrDefault(candidate => ReferenceEquals(candidate.Node, _selectedNode));
        ScheduleLivePdfPreview();
        SetStatus(include ? "Included in compilation" : "Excluded from compilation");
    }

    public async Task UpdateStyleAsync(BookStyle style, CancellationToken cancellationToken = default)
    {
        if (_project is null) return;
        await repository.SaveStyleAsync(_project, style.Validate(), cancellationToken);
        RecomputeDocumentState();
        ScheduleLivePdfPreview();
        SetStatus($"Applied style: {style.Name}");
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
        if (row is not null) await SelectAsync(row, cancellationToken);
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
            return;
        }

        var ast = parser.Parse(_editorText);
        PreviewText = renderer.RenderPreview(ast);
        WordCount = wordCountService.Count(_editorText);
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
            Status = "Updating live PDF preview…";
            StateChanged?.Invoke(this, EventArgs.Empty);

            var source = await BuildCompilationSourceAsync(cancellationToken, useEditorBuffer: true);
            var buildDirectory = Path.Combine(_project.RootPath, "build");
            Directory.CreateDirectory(buildDirectory);
            var destination = Path.Combine(buildDirectory, "live-preview.pdf");
            await exportService.ExportPdfPreviewAsync(source, _project.Title, _project.Style, destination, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            LivePreviewPdfPath = destination;
            LivePreviewVersion = Interlocked.Increment(ref _previewBuildVersion);
            Status = "Live PDF preview updated";
        }
        finally
        {
            IsLivePreviewBuilding = false;
            _previewGate.Release();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
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
        _editorText = string.Empty;
        _isDirty = false;
        _editVersion = 0;
        PreviewText = "Select a manuscript document to begin.";
        WordCount = 0;
        LivePreviewPdfPath = null;
        LivePreviewVersion = 0;
        LivePreviewError = null;
        IsLivePreviewBuilding = false;
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
