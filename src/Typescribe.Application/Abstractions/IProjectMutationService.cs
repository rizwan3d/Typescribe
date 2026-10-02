using Typescribe.Domain.Models;

namespace Typescribe.Application.Abstractions;

public enum ProjectMutationKind
{
    Rename,
    Delete,
    Reorder,
    Reparent,
    ConvertNodeType
}

public sealed record ProjectMutationRequest(
    ProjectMutationKind Kind,
    string NodePersistentId,
    string? NewTitle = null,
    int Offset = 0,
    string? NewParentPersistentId = null,
    int TargetIndex = 0,
    NodeKind? NewNodeKind = null)
{
    public static ProjectMutationRequest Rename(ProjectNode node, string newTitle)
        => new(ProjectMutationKind.Rename, node.PersistentId, NewTitle: newTitle);

    public static ProjectMutationRequest Delete(ProjectNode node)
        => new(ProjectMutationKind.Delete, node.PersistentId);

    public static ProjectMutationRequest Reorder(ProjectNode node, int offset)
        => new(ProjectMutationKind.Reorder, node.PersistentId, Offset: offset);

    public static ProjectMutationRequest Reparent(ProjectNode node, ProjectNode newParent, int targetIndex)
        => new(ProjectMutationKind.Reparent, node.PersistentId, NewParentPersistentId: newParent.PersistentId, TargetIndex: targetIndex);

    public static ProjectMutationRequest Convert(ProjectNode node, NodeKind kind)
        => new(ProjectMutationKind.ConvertNodeType, node.PersistentId, NewNodeKind: kind);
}

public sealed record ProjectMutationResult(bool Applied, string? TransactionId, string Message)
{
    public static ProjectMutationResult NoChange(string message) => new(false, null, message);
}

public sealed record ProjectMutationValidation(
    bool IsConsistent,
    int RecoveredTransactions,
    IReadOnlyList<string> Messages);

public sealed record ProjectTrashItem(
    string TrashId,
    string PersistentId,
    string Title,
    NodeKind Kind,
    string OriginalRelativePath,
    string ParentPersistentId,
    int BinderPosition,
    DateTimeOffset DeletedAt,
    string MetadataSummary);

/// <summary>
/// Owns destructive project mutations and their restart-safe transaction history.
/// Implementations must write transactions beneath .typescribe/history and recover
/// incomplete filesystem operations before a project is opened.
/// </summary>
public interface IProjectMutationService
{
    Task<ProjectMutationResult> ExecuteAsync(
        BookProject project,
        ProjectMutationRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> UndoAsync(string projectRoot, CancellationToken cancellationToken = default);
    Task<bool> RedoAsync(string projectRoot, CancellationToken cancellationToken = default);
    Task<ProjectMutationValidation> ValidateAsync(string projectRoot, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectTrashItem>> ListTrashAsync(
        string projectRoot,
        CancellationToken cancellationToken = default);

    Task<bool> RestoreTrashAsync(
        BookProject project,
        string trashId,
        CancellationToken cancellationToken = default);

    Task<bool> PermanentlyDeleteTrashAsync(
        string projectRoot,
        string trashId,
        CancellationToken cancellationToken = default);
}
