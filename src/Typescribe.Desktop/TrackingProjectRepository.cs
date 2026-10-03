using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

/// <summary>
/// Repository decorator that keeps the currently opened project available to
/// desktop-only workspace features without leaking UI concerns into the core repository.
/// </summary>
internal sealed class TrackingProjectRepository(IProjectRepository inner) : IProjectRepository
{
    public BookProject? CurrentProject { get; private set; }

    public async Task<BookProject> CreateAsync(string rootPath, string title, CancellationToken cancellationToken = default)
    {
        CurrentProject = await inner.CreateAsync(rootPath, title, cancellationToken);
        return CurrentProject;
    }

    public async Task<BookProject> OpenAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        CurrentProject = await inner.OpenAsync(rootPath, cancellationToken);
        return CurrentProject;
    }

    public Task<string> ReadDocumentAsync(BookProject project, ProjectNode node, CancellationToken cancellationToken = default)
        => inner.ReadDocumentAsync(project, node, cancellationToken);

    public Task SaveDocumentAsync(BookProject project, ProjectNode node, string content, CancellationToken cancellationToken = default)
        => inner.SaveDocumentAsync(project, node, content, cancellationToken);

    public Task SaveDocumentStatisticsAsync(BookProject project, ProjectNode node, int wordCount, CancellationToken cancellationToken = default)
        => inner.SaveDocumentStatisticsAsync(project, node, wordCount, cancellationToken);

    public Task<ProjectNode> AddChapterAsync(BookProject project, string title, CancellationToken cancellationToken = default)
        => inner.AddChapterAsync(project, title, cancellationToken);

    public Task<ProjectNode> AddNodeAsync(BookProject project, ProjectNode? parent, NodeKind kind, string title, CancellationToken cancellationToken = default)
        => inner.AddNodeAsync(project, parent, kind, title, cancellationToken);

    public Task RenameNodeAsync(BookProject project, ProjectNode node, string newTitle, CancellationToken cancellationToken = default)
        => inner.RenameNodeAsync(project, node, newTitle, cancellationToken);

    public Task DeleteNodeAsync(BookProject project, ProjectNode node, CancellationToken cancellationToken = default)
        => inner.DeleteNodeAsync(project, node, cancellationToken);

    public Task<bool> MoveNodeAsync(BookProject project, ProjectNode node, int offset, CancellationToken cancellationToken = default)
        => inner.MoveNodeAsync(project, node, offset, cancellationToken);

    public Task<bool> ReparentNodeAsync(BookProject project, ProjectNode node, ProjectNode? newParent, int targetIndex, CancellationToken cancellationToken = default)
        => inner.ReparentNodeAsync(project, node, newParent, targetIndex, cancellationToken);

    public Task SetCompilationIncludedAsync(BookProject project, ProjectNode node, bool included, CancellationToken cancellationToken = default)
        => inner.SetCompilationIncludedAsync(project, node, included, cancellationToken);

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
        => inner.SaveNodeMetadataAsync(project, node, synopsis, notes, status, label, keywords, targetWords, cancellationToken);

    public Task<SnapshotInfo> CreateSnapshotAsync(
        BookProject project,
        ProjectNode node,
        string content,
        string label,
        CancellationToken cancellationToken = default)
        => inner.CreateSnapshotAsync(project, node, content, label, cancellationToken);

    public Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(
        BookProject project,
        ProjectNode node,
        CancellationToken cancellationToken = default)
        => inner.ListSnapshotsAsync(project, node, cancellationToken);

    public Task<string> ReadSnapshotAsync(
        BookProject project,
        ProjectNode node,
        SnapshotInfo snapshot,
        CancellationToken cancellationToken = default)
        => inner.ReadSnapshotAsync(project, node, snapshot, cancellationToken);

    public Task DeleteSnapshotAsync(
        BookProject project,
        ProjectNode node,
        SnapshotInfo snapshot,
        CancellationToken cancellationToken = default)
        => inner.DeleteSnapshotAsync(project, node, snapshot, cancellationToken);

    public Task SaveStyleAsync(BookProject project, BookStyle style, CancellationToken cancellationToken = default)
        => inner.SaveStyleAsync(project, style, cancellationToken);

    public IAsyncEnumerable<(ProjectNode Node, string Content)> EnumerateDocumentsAsync(
        BookProject project,
        CancellationToken cancellationToken = default)
        => inner.EnumerateDocumentsAsync(project, cancellationToken);
}
