using Typescribe.Domain.Models;

namespace Typescribe.Application.Abstractions;

public interface IProjectRepository
{
    Task<BookProject> CreateAsync(string rootPath, string title, CancellationToken cancellationToken = default);
    Task<BookProject> OpenAsync(string rootPath, CancellationToken cancellationToken = default);
    Task<string> ReadDocumentAsync(BookProject project, ProjectNode node, CancellationToken cancellationToken = default);
    Task SaveDocumentAsync(BookProject project, ProjectNode node, string content, CancellationToken cancellationToken = default);
    Task<ProjectNode> AddChapterAsync(BookProject project, string title, CancellationToken cancellationToken = default);
    Task<ProjectNode> AddNodeAsync(BookProject project, ProjectNode? parent, NodeKind kind, string title, CancellationToken cancellationToken = default);
    Task RenameNodeAsync(BookProject project, ProjectNode node, string newTitle, CancellationToken cancellationToken = default);
    Task DeleteNodeAsync(BookProject project, ProjectNode node, CancellationToken cancellationToken = default);
    Task<bool> MoveNodeAsync(BookProject project, ProjectNode node, int offset, CancellationToken cancellationToken = default);
    Task<bool> ReparentNodeAsync(BookProject project, ProjectNode node, ProjectNode? newParent, int targetIndex, CancellationToken cancellationToken = default);
    Task SetCompilationIncludedAsync(BookProject project, ProjectNode node, bool included, CancellationToken cancellationToken = default);
    Task SaveStyleAsync(BookProject project, BookStyle style, CancellationToken cancellationToken = default);
    IAsyncEnumerable<(ProjectNode Node, string Content)> EnumerateDocumentsAsync(BookProject project, CancellationToken cancellationToken = default);
}
