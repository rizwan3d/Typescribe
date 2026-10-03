using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

/// <summary>
/// Repository decorator that routes destructive binder operations through the
/// project mutation service while preserving the existing filesystem repository
/// for document I/O, snapshots, styles, and non-destructive creation.
/// </summary>
public sealed class TransactionalProjectRepository(
    IProjectRepository inner,
    IProjectMutationService mutations) : IProjectRepository
{
    public Task<BookProject> CreateAsync(string rootPath, string title, CancellationToken cancellationToken = default)
        => inner.CreateAsync(rootPath, title, cancellationToken);

    public async Task<BookProject> OpenAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var validation = await mutations.ValidateAsync(rootPath, cancellationToken);
        if (!validation.IsConsistent)
            throw new IOException("The project has an incomplete mutation that could not be recovered safely.");
        return await inner.OpenAsync(rootPath, cancellationToken);
    }

    public Task<string> ReadDocumentAsync(BookProject project, ProjectNode node, CancellationToken cancellationToken = default)
        => inner.ReadDocumentAsync(project, node, cancellationToken);

    public Task SaveDocumentAsync(BookProject project, ProjectNode node, string content, CancellationToken cancellationToken = default)
        => inner.SaveDocumentAsync(project, node, content, cancellationToken);

    public Task SaveDocumentStatisticsAsync(BookProject project, ProjectNode node, int wordCount, CancellationToken cancellationToken = default)
        => inner.SaveDocumentStatisticsAsync(project, node, wordCount, cancellationToken);

    public Task<ProjectNode> AddChapterAsync(BookProject project, string title, CancellationToken cancellationToken = default)
        => inner.AddChapterAsync(project, title, cancellationToken);

    public Task<ProjectNode> AddNodeAsync(
        BookProject project,
        ProjectNode? parent,
        NodeKind kind,
        string title,
        CancellationToken cancellationToken = default)
        => inner.AddNodeAsync(project, parent, kind, title, cancellationToken);

    public async Task RenameNodeAsync(
        BookProject project,
        ProjectNode node,
        string newTitle,
        CancellationToken cancellationToken = default)
        => _ = await mutations.ExecuteAsync(project, ProjectMutationRequest.Rename(node, newTitle), cancellationToken);

    public async Task DeleteNodeAsync(
        BookProject project,
        ProjectNode node,
        CancellationToken cancellationToken = default)
        => _ = await mutations.ExecuteAsync(project, ProjectMutationRequest.Delete(node), cancellationToken);

    public async Task<bool> MoveNodeAsync(
        BookProject project,
        ProjectNode node,
        int offset,
        CancellationToken cancellationToken = default)
        => (await mutations.ExecuteAsync(project, ProjectMutationRequest.Reorder(node, offset), cancellationToken)).Applied;

    public async Task<bool> ReparentNodeAsync(
        BookProject project,
        ProjectNode node,
        ProjectNode? newParent,
        int targetIndex,
        CancellationToken cancellationToken = default)
        => (await mutations.ExecuteAsync(
            project,
            ProjectMutationRequest.Reparent(node, newParent ?? project.Root, targetIndex),
            cancellationToken)).Applied;

    public Task SetCompilationIncludedAsync(
        BookProject project,
        ProjectNode node,
        bool included,
        CancellationToken cancellationToken = default)
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
