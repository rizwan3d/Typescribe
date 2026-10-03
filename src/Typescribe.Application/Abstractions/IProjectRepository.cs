using Typescribe.Domain.Models;

namespace Typescribe.Application.Abstractions;

public interface IProjectRepository
{
    Task<BookProject> CreateAsync(string rootPath, string title, CancellationToken cancellationToken = default);
    Task<BookProject> OpenAsync(string rootPath, CancellationToken cancellationToken = default);
    Task<string> ReadDocumentAsync(BookProject project, ProjectNode node, CancellationToken cancellationToken = default);
    Task SaveDocumentAsync(BookProject project, ProjectNode node, string content, CancellationToken cancellationToken = default);

    async Task SaveDocumentStatisticsAsync(
        BookProject project,
        ProjectNode node,
        int wordCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(node);
        node.SetCachedWordCount(wordCount);
        await SaveNodeMetadataAsync(
            project,
            node,
            node.Synopsis,
            node.Notes,
            node.Status,
            node.Label,
            node.Keywords,
            node.TargetWords,
            cancellationToken);
    }

    Task<ProjectNode> AddChapterAsync(BookProject project, string title, CancellationToken cancellationToken = default);
    Task<ProjectNode> AddNodeAsync(BookProject project, ProjectNode? parent, NodeKind kind, string title, CancellationToken cancellationToken = default);
    Task RenameNodeAsync(BookProject project, ProjectNode node, string newTitle, CancellationToken cancellationToken = default);
    Task DeleteNodeAsync(BookProject project, ProjectNode node, CancellationToken cancellationToken = default);
    Task<bool> MoveNodeAsync(BookProject project, ProjectNode node, int offset, CancellationToken cancellationToken = default);
    Task<bool> ReparentNodeAsync(BookProject project, ProjectNode node, ProjectNode? newParent, int targetIndex, CancellationToken cancellationToken = default);
    Task SetCompilationIncludedAsync(BookProject project, ProjectNode node, bool included, CancellationToken cancellationToken = default);
    Task SaveNodeMetadataAsync(
        BookProject project,
        ProjectNode node,
        string synopsis,
        string notes,
        string status,
        string label,
        string keywords,
        int targetWords,
        CancellationToken cancellationToken = default);
    Task<SnapshotInfo> CreateSnapshotAsync(
        BookProject project,
        ProjectNode node,
        string content,
        string label,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(
        BookProject project,
        ProjectNode node,
        CancellationToken cancellationToken = default);
    Task<string> ReadSnapshotAsync(
        BookProject project,
        ProjectNode node,
        SnapshotInfo snapshot,
        CancellationToken cancellationToken = default);
    Task DeleteSnapshotAsync(
        BookProject project,
        ProjectNode node,
        SnapshotInfo snapshot,
        CancellationToken cancellationToken = default);
    Task SaveStyleAsync(BookProject project, BookStyle style, CancellationToken cancellationToken = default);
    IAsyncEnumerable<(ProjectNode Node, string Content)> EnumerateDocumentsAsync(BookProject project, CancellationToken cancellationToken = default);
}
