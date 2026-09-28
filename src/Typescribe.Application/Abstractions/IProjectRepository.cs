using Typescribe.Domain.Models;

namespace Typescribe.Application.Abstractions;

public interface IProjectRepository
{
    Task<BookProject> CreateAsync(string rootPath, string title, CancellationToken cancellationToken = default);
    Task<BookProject> OpenAsync(string rootPath, CancellationToken cancellationToken = default);
    Task<string> ReadDocumentAsync(BookProject project, ProjectNode node, CancellationToken cancellationToken = default);
    Task SaveDocumentAsync(BookProject project, ProjectNode node, string content, CancellationToken cancellationToken = default);
    Task<ProjectNode> AddChapterAsync(BookProject project, string title, CancellationToken cancellationToken = default);
    IAsyncEnumerable<(ProjectNode Node, string Content)> EnumerateDocumentsAsync(BookProject project, CancellationToken cancellationToken = default);
}
