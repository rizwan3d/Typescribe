using System.Text;
using System.Collections.ObjectModel;
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
    private CancellationTokenSource? _autosaveCts;
    private BookProject? _project;
    private ProjectNode? _selectedNode;
    private string _editorText = string.Empty;
    private bool _suppressEditorChanges;
    private bool _isDirty;
    private long _editVersion;

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
    public bool HasDocument => _selectedNode?.IsDocument == true;
    public bool CanPublishPdf => exportService.CanPublishPdf;
    public string PublishingEngineName => exportService.PublishingEngineName;

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
        if (row is null || !row.Node.IsDocument || _project is null) return;
        await FlushAutosaveAsync();
        SelectedRow = row;
        _selectedNode = row.Node;
        _suppressEditorChanges = true;
        try
        {
            _editorText = await repository.ReadDocumentAsync(_project, _selectedNode, cancellationToken);
            _isDirty = false;
            _editVersion = 0;
            RecomputeDocumentState();
        }
        finally
        {
            _suppressEditorChanges = false;
        }
        SetStatus($"Editing {_selectedNode.Title}");
    }

    public void UpdateEditorText(string text)
    {
        if (_suppressEditorChanges || _selectedNode is null || _project is null) return;
        _editorText = text ?? string.Empty;
        _isDirty = true;
        _editVersion++;
        RecomputeDocumentState();
        ScheduleAutosave();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task AddChapterAsync(CancellationToken cancellationToken = default)
    {
        if (_project is null) return;
        await FlushAutosaveAsync();
        var number = BinderRows.Count(static row => row.Node.Kind == NodeKind.Chapter) + 1;
        var node = await repository.AddChapterAsync(_project, $"Chapter {number}", cancellationToken);
        RefreshBinder();
        var row = BinderRows.First(candidate => ReferenceEquals(candidate.Node, node));
        await SelectAsync(row, cancellationToken);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        SearchResults.Clear();
        if (_project is null || string.IsNullOrWhiteSpace(query))
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        var results = await searchService.SearchAsync(_project, query.Trim(), cancellationToken);
        foreach (var result in results) SearchResults.Add(result);
        SetStatus($"{results.Count} search result(s)");
    }

    public async Task GoToSearchHitAsync(SearchHit? hit, CancellationToken cancellationToken = default)
    {
        if (hit is null) return;
        var row = BinderRows.FirstOrDefault(candidate => candidate.Node.Id == hit.NodeId);
        if (row is not null) await SelectAsync(row, cancellationToken);
    }

    public async Task ExportPdfAsync(string destination, CancellationToken cancellationToken = default)
    {
        EnsureProject();
        await FlushAutosaveAsync();
        SetStatus("Publishing book PDF…");
        var source = await BuildCompilationSourceAsync(cancellationToken);
        await exportService.ExportPdfAsync(source, _project!.Title, destination, cancellationToken);
        SetStatus($"Published {Path.GetFileName(destination)}");
    }

    public async Task ExportTypstAsync(string destination, CancellationToken cancellationToken = default)
    {
        EnsureProject();
        await FlushAutosaveAsync();
        var source = await BuildCompilationSourceAsync(cancellationToken);
        await exportService.ExportTypstAsync(source, _project!.Title, destination, cancellationToken);
        SetStatus($"Exported {Path.GetFileName(destination)}");
    }

    public async Task ExportLatexAsync(string destination, CancellationToken cancellationToken = default)
    {
        EnsureProject();
        await FlushAutosaveAsync();
        var source = await BuildCompilationSourceAsync(cancellationToken);
        await exportService.ExportLatexAsync(source, _project!.Title, destination, cancellationToken);
        SetStatus($"Exported {Path.GetFileName(destination)}");
    }

    public async Task SaveNowAsync(CancellationToken cancellationToken = default)
    {
        _autosaveCts?.Cancel();
        if (_project is null || _selectedNode is null) return;
        await SaveCurrentDocumentAsync(cancellationToken);
    }

    private async Task LoadProjectAsync(CancellationToken cancellationToken)
    {
        RefreshBinder();
        var first = BinderRows.FirstOrDefault(static row => row.Node.IsDocument);
        if (first is not null) await SelectAsync(first, cancellationToken);
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

    private async Task SaveCurrentDocumentAsync(CancellationToken cancellationToken)
    {
        if (_project is null || _selectedNode is null || !_isDirty) return;

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
        if (_project is not null && _selectedNode is not null)
            await SaveCurrentDocumentAsync(CancellationToken.None);
    }

    private void ResetSelection()
    {
        _autosaveCts?.Cancel();
        _autosaveCts?.Dispose();
        _autosaveCts = null;
        _selectedNode = null;
        SelectedRow = null;
        _editorText = string.Empty;
        _isDirty = false;
        _editVersion = 0;
        PreviewText = "Select a manuscript document to begin.";
        WordCount = 0;
    }

    private async Task<string> BuildCompilationSourceAsync(CancellationToken cancellationToken)
    {
        if (_project is null) throw new InvalidOperationException("Open a project first.");
        var builder = new StringBuilder();
        await foreach (var (_, content) in repository.EnumerateDocumentsAsync(_project, cancellationToken))
        {
            if (builder.Length > 0) builder.AppendLine().AppendLine();
            builder.Append(content.TrimEnd()).AppendLine();
        }
        return builder.ToString();
    }

    private void EnsureProject()
    {
        if (_project is null) throw new InvalidOperationException("Open a project first.");
    }

    private void EnsureDocument()
    {
        if (_project is null || _selectedNode is null) throw new InvalidOperationException("Open a manuscript document first.");
    }

    private void SetStatus(string status)
    {
        Status = status;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAutosaveAsync();
        _saveGate.Dispose();
        _autosaveCts?.Dispose();
    }
}
