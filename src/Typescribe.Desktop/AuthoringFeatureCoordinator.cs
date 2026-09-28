using Typescribe.Application.Abstractions;
using Typescribe.Application.Models;
using Typescribe.Application.Services;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;
using Typescribe.Infrastructure.Services;

namespace Typescribe.Desktop;

internal sealed class AuthoringFeatureCoordinator
{
    private readonly IProjectRepository _repository = new FileSystemProjectRepository();
    private readonly IAuthoringProjectService _authoring = new AuthoringProjectService();
    private readonly SnapshotDiffService _diff = new();
    private readonly WordCountService _wordCount = new();
    private readonly Dictionary<string, int> _diskWords = new(StringComparer.Ordinal);
    private BookProject? _project;
    private int _sessionBaseline;

    public bool HasProject => _project is not null;
    public ProjectAuthoringState State => _project?.Authoring ?? new ProjectAuthoringState();
    public IReadOnlyList<CustomMetadataDefinition> CustomFields => State.CustomFields;
    public IReadOnlyList<ProjectCollection> Collections => State.Collections;
    public int ProjectWords { get; private set; }
    public int SessionWords => Math.Max(0, ProjectWords - _sessionBaseline);
    public int DailyWords => Math.Max(0, ProjectWords - State.DailyBaselineWords);
    public double ProjectProgress => Progress(ProjectWords, State.ProjectTargetWords);
    public double DailyProgress => Progress(DailyWords, State.DailyTargetWords);

    public async Task OpenAsync(string projectRoot, IEnumerable<BinderRowViewModel> liveRows, CancellationToken cancellationToken = default)
    {
        _project = await _repository.OpenAsync(projectRoot, cancellationToken);
        await _authoring.LoadAsync(_project, cancellationToken);
        ApplyStateToLiveNodes(liveRows);
        await RefreshWordIndexAsync(cancellationToken);
        _sessionBaseline = ProjectWords;

        var today = DateOnly.FromDateTime(DateTime.Today);
        if (_project.Authoring.DailyDate != today)
        {
            _project.Authoring.DailyDate = today;
            _project.Authoring.DailyBaselineWords = ProjectWords;
            await _authoring.SaveAsync(_project, cancellationToken);
        }
    }

    public async Task ReloadStructureAsync(IEnumerable<BinderRowViewModel> liveRows, CancellationToken cancellationToken = default)
    {
        if (_project is null) return;
        var root = _project.RootPath;
        var sessionBaseline = _sessionBaseline;
        _project = await _repository.OpenAsync(root, cancellationToken);
        await _authoring.LoadAsync(_project, cancellationToken);
        ApplyStateToLiveNodes(liveRows);
        await RefreshWordIndexAsync(cancellationToken);
        _sessionBaseline = Math.Min(sessionBaseline, ProjectWords);
    }

    public void UpdateSelectedWords(string? persistentId, int currentWords)
    {
        if (string.IsNullOrWhiteSpace(persistentId)) return;
        var baseline = _diskWords.GetValueOrDefault(persistentId);
        ProjectWords = Math.Max(0, _diskWords.Values.Sum() - baseline + Math.Max(0, currentWords));
    }

    public async Task SaveTargetsAsync(int projectTarget, int dailyTarget, CancellationToken cancellationToken = default)
    {
        EnsureProject();
        _project!.Authoring.ProjectTargetWords = Math.Max(0, projectTarget);
        _project.Authoring.DailyTargetWords = Math.Max(0, dailyTarget);
        await _authoring.SaveAsync(_project, cancellationToken);
    }

    public async Task AddCustomFieldAsync(string name, CancellationToken cancellationToken = default)
    {
        EnsureProject();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var key = ProjectAuthoringState.NormalizeKey(name);
        _project!.Authoring.UpsertCustomField(new CustomMetadataDefinition(key, name.Trim()));
        await _authoring.SaveAsync(_project, cancellationToken);
    }

    public async Task RemoveCustomFieldAsync(string nameOrKey, IEnumerable<BinderRowViewModel> liveRows, CancellationToken cancellationToken = default)
    {
        EnsureProject();
        var field = _project!.Authoring.CustomFields.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, nameOrKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Name, nameOrKey, StringComparison.OrdinalIgnoreCase));
        if (field is null) return;

        _project.Authoring.RemoveCustomField(field.Key);
        foreach (var node in Flatten(_project.Root)) node.SetCustomMetadata(field.Key, string.Empty);
        foreach (var row in liveRows) row.Node.SetCustomMetadata(field.Key, string.Empty);
        await _authoring.SaveAsync(_project, cancellationToken);
    }

    public async Task SetCustomValueAsync(ProjectNode liveNode, string key, string value, CancellationToken cancellationToken = default)
    {
        EnsureProject();
        liveNode.SetCustomMetadata(key, value);
        var node = FindNode(liveNode.PersistentId);
        node?.SetCustomMetadata(key, value);
        await _authoring.SaveAsync(_project!, cancellationToken);
    }

    public async Task<DocumentComment> AddCommentAsync(ProjectNode liveNode, int line, string text, CancellationToken cancellationToken = default)
    {
        EnsureProject();
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var comment = DocumentComment.Create(line, text);
        liveNode.AddComment(comment);
        FindNode(liveNode.PersistentId)?.AddComment(comment);
        await _authoring.SaveAsync(_project!, cancellationToken);
        return comment;
    }

    public async Task SetCommentResolvedAsync(ProjectNode liveNode, DocumentComment comment, bool resolved, CancellationToken cancellationToken = default)
    {
        EnsureProject();
        liveNode.SetCommentResolved(comment.Id, resolved);
        FindNode(liveNode.PersistentId)?.SetCommentResolved(comment.Id, resolved);
        await _authoring.SaveAsync(_project!, cancellationToken);
    }

    public async Task DeleteCommentAsync(ProjectNode liveNode, DocumentComment comment, CancellationToken cancellationToken = default)
    {
        EnsureProject();
        liveNode.RemoveComment(comment.Id);
        FindNode(liveNode.PersistentId)?.RemoveComment(comment.Id);
        await _authoring.SaveAsync(_project!, cancellationToken);
    }

    public async Task<ProjectCollection> SaveSearchCollectionAsync(
        string name,
        string query,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        EnsureProject();
        var collection = ProjectCollection.SavedSearch(name, query, options.MatchCase, options.UseRegex, options.WholeWord);
        _project!.Authoring.UpsertCollection(collection);
        await _authoring.SaveAsync(_project, cancellationToken);
        return collection;
    }

    public async Task<ProjectCollection> CreateManualCollectionAsync(
        string name,
        IEnumerable<ProjectNode> nodes,
        CancellationToken cancellationToken = default)
    {
        EnsureProject();
        var collection = ProjectCollection.Manual(name, nodes.Select(static node => node.PersistentId));
        _project!.Authoring.UpsertCollection(collection);
        await _authoring.SaveAsync(_project, cancellationToken);
        return collection;
    }

    public async Task AddToManualCollectionAsync(ProjectCollection collection, ProjectNode node, CancellationToken cancellationToken = default)
    {
        EnsureProject();
        if (collection.Kind != ProjectCollectionKind.Manual) return;
        var ids = collection.NodePersistentIds.Append(node.PersistentId).Distinct(StringComparer.Ordinal).ToArray();
        _project!.Authoring.UpsertCollection(collection with { NodePersistentIds = ids });
        await _authoring.SaveAsync(_project, cancellationToken);
    }

    public async Task DeleteCollectionAsync(ProjectCollection collection, CancellationToken cancellationToken = default)
    {
        EnsureProject();
        _project!.Authoring.RemoveCollection(collection.Id);
        await _authoring.SaveAsync(_project, cancellationToken);
    }

    public async Task<IReadOnlyList<DiffLine>> CompareSnapshotAsync(
        ProjectNode liveNode,
        SnapshotInfo snapshot,
        string currentText,
        CancellationToken cancellationToken = default)
    {
        EnsureProject();
        var node = FindNode(liveNode.PersistentId)
            ?? throw new InvalidOperationException("The selected document is not available in the project model.");
        var snapshotText = await _repository.ReadSnapshotAsync(_project!, node, snapshot, cancellationToken);
        return _diff.Compare(snapshotText, currentText);
    }

    public Task<IReadOnlyList<ProjectTemplateInfo>> ListTemplatesAsync(CancellationToken cancellationToken = default)
        => _authoring.ListTemplatesAsync(cancellationToken);

    public async Task SaveAsTemplateAsync(string name, CancellationToken cancellationToken = default)
    {
        EnsureProject();
        await _authoring.SaveAsync(_project!, cancellationToken);
        await _authoring.SaveAsTemplateAsync(_project!, name, cancellationToken);
    }

    public Task MaterializeTemplateAsync(ProjectTemplateInfo template, string destination, string title, CancellationToken cancellationToken = default)
        => _authoring.MaterializeTemplateAsync(template, destination, title, cancellationToken);

    public IReadOnlyList<ProjectNode> ResolveCollection(ProjectCollection collection, IEnumerable<BinderRowViewModel> liveRows)
    {
        if (collection.Kind != ProjectCollectionKind.Manual) return [];
        var wanted = collection.NodePersistentIds.ToHashSet(StringComparer.Ordinal);
        return liveRows.Select(static row => row.Node).Where(node => wanted.Contains(node.PersistentId)).ToArray();
    }

    private async Task RefreshWordIndexAsync(CancellationToken cancellationToken)
    {
        _diskWords.Clear();
        if (_project is null)
        {
            ProjectWords = 0;
            return;
        }

        foreach (var node in Flatten(_project.Root).Where(static node => node.IsDocument))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await _repository.ReadDocumentAsync(_project, node, cancellationToken);
            _diskWords[node.PersistentId] = _wordCount.Count(content);
        }
        ProjectWords = _diskWords.Values.Sum();
    }

    private void ApplyStateToLiveNodes(IEnumerable<BinderRowViewModel> liveRows)
    {
        if (_project is null) return;
        var nodes = Flatten(_project.Root).ToDictionary(static node => node.PersistentId, StringComparer.Ordinal);
        foreach (var row in liveRows)
        {
            if (!nodes.TryGetValue(row.Node.PersistentId, out var source)) continue;
            row.Node.ReplaceCustomMetadata(source.CustomMetadata);
            row.Node.ReplaceComments(source.Comments);
        }
    }

    private ProjectNode? FindNode(string persistentId)
        => _project is null ? null : Flatten(_project.Root).FirstOrDefault(node => string.Equals(node.PersistentId, persistentId, StringComparison.Ordinal));

    private static IEnumerable<ProjectNode> Flatten(ProjectNode root)
    {
        foreach (var child in root.Children)
        {
            yield return child;
            foreach (var descendant in Flatten(child)) yield return descendant;
        }
    }

    private void EnsureProject()
    {
        if (_project is null) throw new InvalidOperationException("Open a project first.");
    }

    private static double Progress(int value, int target)
        => target <= 0 ? 0 : Math.Clamp(value / (double)target, 0, 1);
}
