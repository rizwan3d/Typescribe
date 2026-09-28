using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

public sealed class AuthoringProjectService : IAuthoringProjectService
{
    private readonly AuthoringStateStore _stateStore = new();
    private readonly ProjectTemplateStore _templateStore = new();

    public Task LoadAsync(BookProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        cancellationToken.ThrowIfCancellationRequested();
        project.Authoring = _stateStore.Load(project.RootPath, project.Root);
        return Task.CompletedTask;
    }

    public Task SaveAsync(BookProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        return _stateStore.SaveAsync(project, cancellationToken);
    }

    public Task<IReadOnlyList<ProjectTemplateInfo>> ListTemplatesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_templateStore.List());
    }

    public Task SaveAsTemplateAsync(BookProject project, string name, CancellationToken cancellationToken = default)
        => _templateStore.SaveAsync(project, name, cancellationToken);

    public Task MaterializeTemplateAsync(ProjectTemplateInfo template, string destination, string title, CancellationToken cancellationToken = default)
        => _templateStore.MaterializeAsync(template, destination, title, cancellationToken);
}
