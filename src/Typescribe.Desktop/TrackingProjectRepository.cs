using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

/// <summary>
/// Repository decorator that keeps the currently opened project available to
/// desktop-only workspace features without leaking UI concerns into the core repository.
/// </summary>
internal sealed class TrackingProjectRepository : IProjectRepository
{
    private readonly IProjectRepository _inner;

    public TrackingProjectRepository(IProjectRepository inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ActiveInstance = this;
    }

    public static TrackingProjectRepository? ActiveInstance { get; private set; }
    public BookProject? CurrentProject { get; private set; }

    public async Task<BookProject> CreateAsync(string rootPath, string title, CancellationToken cancellationToken = default)
    {
        CurrentProject = await _inner.CreateAsync(rootPath, title, cancellationToken);
        return CurrentProject;
    }

    public async Task<BookProject> OpenAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        CurrentProject = await _inner.OpenAsync(rootPath, cancellationToken);
        return CurrentProject;
    }

    public Task<string> ReadDocumentAsync(BookProject project, ProjectNode node, CancellationToken cancellationToken = default)
        => _inner.ReadDocumentAsync(project, node, cancellationToken);

    public Task SaveDocumentAsync(BookProject project, ProjectNode node, string content, CancellationToken cancellationToken = default)
        => _inner.SaveDocumentAsync(project, node, content, cancellationToken);

    public Task SaveDocumentStatisticsAsync(BookProject project, ProjectNode node, int wordCount, CancellationToken cancellationToken = default)
        => _inner.SaveDocumentStatisticsAsync(project, node, wordCount, cancellationToken);

    public Task<ProjectNode> AddChapterAsync(BookProject project, string title, CancellationToken cancellationToken = default)
        => _inner.AddChapterAsync(project, title, cancellationToken);

    public Task<ProjectNode> AddNodeAsync(BookProject project, ProjectNode? parent, NodeKind kind, string title, CancellationToken cancellationToken = default)
        => _inner.AddNodeAsync(project, parent, kind, title, cancellationToken);

    public Task RenameNodeAsync(BookProject project, ProjectNode node, string newTitle, CancellationToken cancellationToken = default)
        => _inner.RenameNodeAsync(project, node, newTitle, cancellationToken);

    public Task DeleteNodeAsync(BookProject project, ProjectNode node, CancellationToken cancellationToken = default)
        => _inner.DeleteNodeAsync(project, node, cancellationToken);

    public Task<bool> MoveNodeAsync(BookProject project, ProjectNode node, int offset, CancellationToken cancellationToken = default)
        => _inner.MoveNodeAsync(project, node, offset, cancellationToken);

    public Task<bool> ReparentNodeAsync(BookProject project, ProjectNode node, ProjectNode? newParent, int targetIndex, CancellationToken cancellationToken = default)
        => _inner.ReparentNodeAsync(project, node, newParent, targetIndex, cancellationToken);

    public Task SetCompilationIncludedAsync(BookProject project, ProjectNode node, bool included, CancellationToken cancellationToken = default)
        => _inner.SetCompilationIncludedAsync(project, node, included, cancellationToken);

    public Task SaveNodeMetadataAsync(
        BookProject project,
        ProjectNode node,
        string synopsis,
        string notes,
        string status,
        string label,
        string keywords,
        int targetWords,
        CancellationToken cancellationToken = default)
        => _inner.SaveNodeMetadataAsync(project, node, synopsis, notes, status, label, keywords, targetWords, cancellationToken);

    public Task<SnapshotInfo> CreateSnapshotAsync(
        BookProject project,
        ProjectNode node,
        string content,
        string label,
        CancellationToken cancellationToken = default)
        => _inner.CreateSnapshotAsync(project, node, content, label, cancellationToken);

    public Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(
        BookProject project,
        ProjectNode node,
        CancellationToken cancellationToken = default)
        => _inner.ListSnapshotsAsync(project, node, cancellationToken);

    public Task<string> ReadSnapshotAsync(
        BookProject project,
        ProjectNode node,
        SnapshotInfo snapshot,
        CancellationToken cancellationToken = default)
        => _inner.ReadSnapshotAsync(project, node, snapshot, cancellationToken);

    public Task DeleteSnapshotAsync(
        BookProject project,
        ProjectNode node,
        SnapshotInfo snapshot,
        CancellationToken cancellationToken = default)
        => _inner.DeleteSnapshotAsync(project, node, snapshot, cancellationToken);

    public Task SaveStyleAsync(BookProject project, BookStyle style, CancellationToken cancellationToken = default)
        => _inner.SaveStyleAsync(project, style, cancellationToken);

    public IAsyncEnumerable<(ProjectNode Node, string Content)> EnumerateDocumentsAsync(
        BookProject project,
        CancellationToken cancellationToken = default)
        => _inner.EnumerateDocumentsAsync(project, cancellationToken);
}
