using Typescribe.Domain.Models;

namespace Typescribe.Application.Abstractions;

public interface IAuthoringProjectService
{
    Task LoadAsync(BookProject project, CancellationToken cancellationToken = default);
    Task SaveAsync(BookProject project, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectTemplateInfo>> ListTemplatesAsync(CancellationToken cancellationToken = default);
    Task SaveAsTemplateAsync(BookProject project, string name, CancellationToken cancellationToken = default);
    Task MaterializeTemplateAsync(ProjectTemplateInfo template, string destination, string title, CancellationToken cancellationToken = default);
}
