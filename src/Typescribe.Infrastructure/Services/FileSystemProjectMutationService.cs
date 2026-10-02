using System.Text;
using System.Text.Json;
using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

/// <summary>
/// Filesystem-first project mutation coordinator. Every mutation is prepared in
/// .typescribe/history before touching project content, then committed only after
/// binder metadata is durable. Prepared transactions are rolled back on the next open.
/// </summary>
public sealed class FileSystemProjectMutationService : IProjectMutationService
{
    private const string TypescribeDirectory = ".typescribe";
    private const string HistoryDirectory = "history";
    private const string TrashDirectory = "trash";
    private const string BinderFile = "binder.tsv";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly BinderMetadataStore _binderStore = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ProjectMutationResult> ExecuteAsync(
        BookProject project,
        ProjectMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await AbandonRedoStackAsync(project.RootPath, cancellationToken);
            return request.Kind switch
            {
                ProjectMutationKind.Rename => await RenameAsync(project, request, cancellationToken),
                ProjectMutationKind.Delete => await DeleteAsync(project, request, cancellationToken),
                ProjectMutationKind.Reorder => await ReorderAsync(project, request, cancellationToken),
                ProjectMutationKind.Reparent => await ReparentAsync(project, request, cancellationToken),
                ProjectMutationKind.ConvertNodeType => await ConvertNodeTypeAsync(project, request, cancellationToken),
                _ => throw new InvalidOperationException($"Unsupported mutation kind: {request.Kind}.")
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> UndoAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var logs = await LoadLogsAsync(projectRoot, cancellationToken);
            var log = logs
                .Where(static item => string.Equals(item.State, MutationStates.Completed, StringComparison.Ordinal))
                .OrderByDescending(static item => item.CreatedAt)
                .FirstOrDefault();
            if (log is null) return false;

            await ApplyFilesystemDirectionAsync(projectRoot, log, undo: true, cancellationToken);
            await RestoreBinderSnapshotAsync(projectRoot, log.BinderBefore, cancellationToken);
            await UpdateTrashRestoreStateAsync(projectRoot, log, undo: true, cancellationToken);
            log.State = MutationStates.Undone;
            log.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveLogAsync(projectRoot, log, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RedoAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var logs = await LoadLogsAsync(projectRoot, cancellationToken);
            var log = logs
                .Where(static item => string.Equals(item.State, MutationStates.Undone, StringComparison.Ordinal))
                .OrderBy(static item => item.CreatedAt)
                .FirstOrDefault();
            if (log is null) return false;

            await ApplyFilesystemDirectionAsync(projectRoot, log, undo: false, cancellationToken);
            await RestoreBinderSnapshotAsync(projectRoot, log.BinderAfter, cancellationToken);
            await UpdateTrashRestoreStateAsync(projectRoot, log, undo: false, cancellationToken);
            log.State = MutationStates.Completed;
            log.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveLogAsync(projectRoot, log, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProjectMutationValidation> ValidateAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var messages = new List<string>();
            var recovered = 0;
            foreach (var log in (await LoadLogsAsync(projectRoot, cancellationToken))
                         .Where(static item => string.Equals(item.State, MutationStates.Prepared, StringComparison.Ordinal))
                         .OrderBy(static item => item.CreatedAt))
            {
                try
                {
                    await RecoverPreparedAsync(projectRoot, log, cancellationToken);
                    log.State = MutationStates.RolledBack;
                    log.Error = "Recovered an incomplete transaction during project open.";
                    log.UpdatedAt = DateTimeOffset.UtcNow;
                    await SaveLogAsync(projectRoot, log, cancellationToken);
                    recovered++;
                    messages.Add($"Recovered {log.Kind} transaction {log.Id}.");
                }
                catch (Exception ex)
                {
                    log.Error = $"Recovery failed: {ex.Message}";
                    log.UpdatedAt = DateTimeOffset.UtcNow;
                    await SaveLogAsync(projectRoot, log, cancellationToken);
                    messages.Add($"Could not recover transaction {log.Id}: {ex.Message}");
                }
            }

            var remainingPrepared = (await LoadLogsAsync(projectRoot, cancellationToken))
                .Any(static item => string.Equals(item.State, MutationStates.Prepared, StringComparison.Ordinal));
            return new ProjectMutationValidation(!remainingPrepared, recovered, messages);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ProjectTrashItem>> ListTrashAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var directory = TrashRoot(projectRoot);
        if (!Directory.Exists(directory)) return [];

        var result = new List<ProjectTrashItem>();
        foreach (var metadataPath in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var record = JsonSerializer.Deserialize<TrashRecord>(
                    await File.ReadAllTextAsync(metadataPath, cancellationToken), JsonOptions);
                if (record is null || record.RestoredAt is not null) continue;
                if (!PathExists(Absolute(projectRoot, record.PayloadRelativePath))) continue;
                result.Add(ToTrashItem(record));
            }
            catch
            {
                // One damaged trash record must not hide the remaining recoverable items.
            }
        }

        return result.OrderByDescending(static item => item.DeletedAt).ToArray();
    }

    public async Task<bool> RestoreTrashAsync(
        BookProject project,
        string trashId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(trashId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var record = await LoadTrashRecordAsync(project.RootPath, trashId, cancellationToken);
            if (record is null || record.RestoredAt is not null) return false;

            var payload = Absolute(project.RootPath, record.PayloadRelativePath);
            var destination = Absolute(project.RootPath, record.OriginalRelativePath);
            if (!PathExists(payload)) return false;
            if (PathExists(destination))
                throw new IOException($"Cannot restore '{record.Title}' because {record.OriginalRelativePath} already exists.");

            var parent = string.Equals(record.ParentPersistentId, project.Root.PersistentId, StringComparison.Ordinal)
                ? project.Root
                : FindNode(project.Root, record.ParentPersistentId)
                  ?? throw new InvalidOperationException("The original binder parent no longer exists.");
            if (!parent.IsContainer) throw new InvalidOperationException("The original binder parent is no longer a container.");

            var binderBefore = await ReadBinderSnapshotAsync(project.RootPath, cancellationToken);
            var transaction = NewLog(ProjectMutationKind.Delete, record.PersistentId, binderBefore) with
            {
                Id = Guid.NewGuid().ToString("N"),
                Operation = "Restore",
                SourceRelativePath = record.PayloadRelativePath,
                TargetRelativePath = record.OriginalRelativePath,
                OldParentPersistentId = record.ParentPersistentId,
                NewParentPersistentId = record.ParentPersistentId,
                OldIndex = record.BinderPosition,
                NewIndex = record.BinderPosition,
                TrashId = record.TrashId
            };
            await SaveLogAsync(project.RootPath, transaction, cancellationToken);

            ProjectNode? restoredNode = null;
            try
            {
                MovePath(payload, destination);
                restoredNode = RestoreSnapshot(record.Node);
                parent.InsertChild(Math.Clamp(record.BinderPosition, 0, parent.Children.Count), restoredNode);
                await _binderStore.SaveAsync(project, cancellationToken);

                record.RestoredAt = DateTimeOffset.UtcNow;
                await SaveTrashRecordAsync(project.RootPath, record, cancellationToken);
                transaction.BinderAfter = await ReadBinderSnapshotAsync(project.RootPath, cancellationToken);
                transaction.State = MutationStates.Completed;
                transaction.UpdatedAt = DateTimeOffset.UtcNow;
                await SaveLogAsync(project.RootPath, transaction, cancellationToken);
                return true;
            }
            catch (Exception ex)
            {
                if (restoredNode is not null) parent.RemoveChild(restoredNode);
                if (PathExists(destination) && !PathExists(payload)) MovePath(destination, payload);
                await RestoreBinderSnapshotAsync(project.RootPath, binderBefore, CancellationToken.None);
                transaction.State = MutationStates.RolledBack;
                transaction.Error = ex.Message;
                transaction.UpdatedAt = DateTimeOffset.UtcNow;
                await SaveLogAsync(project.RootPath, transaction, CancellationToken.None);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> PermanentlyDeleteTrashAsync(
        string projectRoot,
        string trashId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(trashId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var record = await LoadTrashRecordAsync(projectRoot, trashId, cancellationToken);
            if (record is null) return false;
            var payloadRoot = Path.Combine(TrashRoot(projectRoot), record.TrashId);
            if (Directory.Exists(payloadRoot)) Directory.Delete(payloadRoot, recursive: true);
            var metadata = TrashMetadataPath(projectRoot, record.TrashId);
            if (File.Exists(metadata)) File.Delete(metadata);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ProjectMutationResult> RenameAsync(
        BookProject project,
        ProjectMutationRequest request,
        CancellationToken cancellationToken)
    {
        var node = RequireNode(project, request.NodePersistentId);
        if (ReferenceEquals(node, project.Root)) throw new InvalidOperationException("The book root cannot be renamed from the binder.");
        var newTitle = request.NewTitle?.Trim();
        if (string.IsNullOrWhiteSpace(newTitle)) throw new ArgumentException("A new title is required.", nameof(request));

        var source = ResolveNodePath(project, node);
        var parentDirectory = Path.GetDirectoryName(source) ?? throw new InvalidOperationException("The binder item has no parent directory.");
        var slug = Slugify(newTitle);
        var extension = node.IsContainer ? null : Path.GetExtension(source);
        var desired = extension is null ? Path.Combine(parentDirectory, slug) : Path.Combine(parentDirectory, slug + extension);
        var target = PathsEqual(source, desired) ? source : CreateUniquePath(parentDirectory, slug, extension);
        var sourceRelative = Normalize(Path.GetRelativePath(project.RootPath, source));
        var targetRelative = Normalize(Path.GetRelativePath(project.RootPath, target));
        if (PathsEqual(source, target) && string.Equals(node.Title, newTitle, StringComparison.Ordinal))
            return ProjectMutationResult.NoChange("The binder item already has that name.");

        var binderBefore = await ReadBinderSnapshotAsync(project.RootPath, cancellationToken);
        var log = NewLog(ProjectMutationKind.Rename, node.PersistentId, binderBefore) with
        {
            SourceRelativePath = sourceRelative,
            TargetRelativePath = targetRelative,
            OldTitle = node.Title,
            NewTitle = newTitle,
            FileBeforeBase64 = node.IsDocument && File.Exists(source)
                ? Convert.ToBase64String(await File.ReadAllBytesAsync(source, cancellationToken))
                : null
        };
        await SaveLogAsync(project.RootPath, log, cancellationToken);

        try
        {
            if (!PathsEqual(source, target)) MovePath(source, target);
            RepathSubtree(node, sourceRelative, targetRelative);
            node.Rename(newTitle);
            if (node.IsDocument) await RewritePrimaryHeadingAsync(target, newTitle, cancellationToken);
            await _binderStore.SaveAsync(project, cancellationToken);

            log.FileAfterBase64 = node.IsDocument && File.Exists(target)
                ? Convert.ToBase64String(await File.ReadAllBytesAsync(target, cancellationToken))
                : null;
            log.BinderAfter = await ReadBinderSnapshotAsync(project.RootPath, cancellationToken);
            log.State = MutationStates.Completed;
            log.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveLogAsync(project.RootPath, log, cancellationToken);
            return new ProjectMutationResult(true, log.Id, $"Renamed to {newTitle}.");
        }
        catch (Exception ex)
        {
            try
            {
                if (!PathsEqual(source, target) && PathExists(target) && !PathExists(source)) MovePath(target, source);
                RepathSubtree(node, targetRelative, sourceRelative);
                node.Rename(log.OldTitle ?? node.Title);
                if (log.FileBeforeBase64 is { Length: > 0 } && File.Exists(source))
                    await File.WriteAllBytesAsync(source, Convert.FromBase64String(log.FileBeforeBase64), CancellationToken.None);
                await RestoreBinderSnapshotAsync(project.RootPath, binderBefore, CancellationToken.None);
            }
            catch { }
            await MarkRolledBackAsync(project.RootPath, log, ex, CancellationToken.None);
            throw;
        }
    }

    private async Task<ProjectMutationResult> DeleteAsync(
        BookProject project,
        ProjectMutationRequest request,
        CancellationToken cancellationToken)
    {
        var node = RequireNode(project, request.NodePersistentId);
        if (ReferenceEquals(node, project.Root)) throw new InvalidOperationException("The book root cannot be deleted.");
        var parent = FindParent(project.Root, node) ?? throw new InvalidOperationException("The binder item is detached from the project tree.");
        var position = parent.IndexOf(node);
        var source = ResolveNodePath(project, node);
        var sourceRelative = Normalize(Path.GetRelativePath(project.RootPath, source));
        var binderBefore = await ReadBinderSnapshotAsync(project.RootPath, cancellationToken);
        var log = NewLog(ProjectMutationKind.Delete, node.PersistentId, binderBefore) with
        {
            SourceRelativePath = sourceRelative,
            OldTitle = node.Title,
            OldParentPersistentId = parent.PersistentId,
            OldIndex = position,
            TrashId = string.Empty
        };
        log.TrashId = log.Id;
        var payloadRelative = Normalize(Path.Combine(TypescribeDirectory, TrashDirectory, log.TrashId, "payload", Path.GetFileName(source)));
        log.TargetRelativePath = payloadRelative;
        await SaveLogAsync(project.RootPath, log, cancellationToken);

        var record = new TrashRecord
        {
            TrashId = log.TrashId,
            PersistentId = node.PersistentId,
            Title = node.Title,
            Kind = node.Kind,
            OriginalRelativePath = sourceRelative,
            PayloadRelativePath = payloadRelative,
            ParentPersistentId = parent.PersistentId,
            BinderPosition = position,
            DeletedAt = DateTimeOffset.UtcNow,
            Node = CaptureSnapshot(node)
        };

        var payload = Absolute(project.RootPath, payloadRelative);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
            MovePath(source, payload);
            parent.RemoveChild(node);
            await _binderStore.SaveAsync(project, cancellationToken);
            await SaveTrashRecordAsync(project.RootPath, record, cancellationToken);

            log.BinderAfter = await ReadBinderSnapshotAsync(project.RootPath, cancellationToken);
            log.State = MutationStates.Completed;
            log.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveLogAsync(project.RootPath, log, cancellationToken);
            return new ProjectMutationResult(true, log.Id, $"Moved {node.Title} to Trash.");
        }
        catch (Exception ex)
        {
            try
            {
                if (!parent.Children.Contains(node)) parent.InsertChild(Math.Clamp(position, 0, parent.Children.Count), node);
                if (PathExists(payload) && !PathExists(source)) MovePath(payload, source);
                await RestoreBinderSnapshotAsync(project.RootPath, binderBefore, CancellationToken.None);
            }
            catch { }
            await MarkRolledBackAsync(project.RootPath, log, ex, CancellationToken.None);
            throw;
        }
    }

    private async Task<ProjectMutationResult> ReorderAsync(
        BookProject project,
        ProjectMutationRequest request,
        CancellationToken cancellationToken)
    {
        var node = RequireNode(project, request.NodePersistentId);
        if (ReferenceEquals(node, project.Root) || request.Offset == 0) return ProjectMutationResult.NoChange("Nothing to reorder.");
        var parent = FindParent(project.Root, node) ?? throw new InvalidOperationException("The binder item is detached from the project tree.");
        var oldIndex = parent.IndexOf(node);
        var newIndex = oldIndex + request.Offset;
        if (oldIndex < 0 || newIndex < 0 || newIndex >= parent.Children.Count)
            return ProjectMutationResult.NoChange("The binder item is already at the edge of its parent.");

        var binderBefore = await ReadBinderSnapshotAsync(project.RootPath, cancellationToken);
        var log = NewLog(ProjectMutationKind.Reorder, node.PersistentId, binderBefore) with
        {
            OldParentPersistentId = parent.PersistentId,
            NewParentPersistentId = parent.PersistentId,
            OldIndex = oldIndex,
            NewIndex = newIndex
        };
        await SaveLogAsync(project.RootPath, log, cancellationToken);

        try
        {
            if (!parent.MoveChild(node, request.Offset)) return ProjectMutationResult.NoChange("Nothing to reorder.");
            await _binderStore.SaveAsync(project, cancellationToken);
            log.BinderAfter = await ReadBinderSnapshotAsync(project.RootPath, cancellationToken);
            log.State = MutationStates.Completed;
            log.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveLogAsync(project.RootPath, log, cancellationToken);
            return new ProjectMutationResult(true, log.Id, request.Offset < 0 ? "Moved binder item up." : "Moved binder item down.");
        }
        catch (Exception ex)
        {
            try
            {
                var current = parent.IndexOf(node);
                if (current >= 0 && current != oldIndex)
                {
                    parent.RemoveChild(node);
                    parent.InsertChild(oldIndex, node);
                }
                await RestoreBinderSnapshotAsync(project.RootPath, binderBefore, CancellationToken.None);
            }
            catch { }
            await MarkRolledBackAsync(project.RootPath, log, ex, CancellationToken.None);
            throw;
        }
    }

    private async Task<ProjectMutationResult> ReparentAsync(
        BookProject project,
        ProjectMutationRequest request,
        CancellationToken cancellationToken)
    {
        var node = RequireNode(project, request.NodePersistentId);
        if (ReferenceEquals(node, project.Root)) return ProjectMutationResult.NoChange("The project root cannot be moved.");
        var newParent = string.Equals(request.NewParentPersistentId, project.Root.PersistentId, StringComparison.Ordinal)
            ? project.Root
            : FindNode(project.Root, request.NewParentPersistentId ?? string.Empty)
              ?? throw new InvalidOperationException("The destination binder parent was not found.");
        if (!newParent.IsContainer) throw new InvalidOperationException("Binder items can only be moved into a book, part, or folder.");
        if (ReferenceEquals(node, newParent) || ContainsNode(node, newParent))
            throw new InvalidOperationException("A binder item cannot be moved inside itself or one of its descendants.");

        var oldParent = FindParent(project.Root, node) ?? throw new InvalidOperationException("The binder item is detached from the project tree.");
        var oldIndex = oldParent.IndexOf(node);
        var targetIndex = Math.Clamp(request.TargetIndex, 0, newParent.Children.Count);
        if (ReferenceEquals(oldParent, newParent))
        {
            if (targetIndex > oldIndex) targetIndex--;
            var delta = targetIndex - oldIndex;
            if (delta == 0) return ProjectMutationResult.NoChange("The binder item is already at that position.");
            return await ReorderAsync(project, request with { Kind = ProjectMutationKind.Reorder, Offset = delta }, cancellationToken);
        }

        var source = ResolveNodePath(project, node);
        var destinationDirectory = ReferenceEquals(newParent, project.Root)
            ? Path.Combine(project.RootPath, "manuscript")
            : ResolveNodePath(project, newParent);
        Directory.CreateDirectory(destinationDirectory);
        var fileName = Path.GetFileName(source);
        var destination = Path.Combine(destinationDirectory, fileName);
        if (PathExists(destination))
        {
            destination = node.IsContainer
                ? CreateUniquePath(destinationDirectory, fileName, null)
                : CreateUniquePath(destinationDirectory, Path.GetFileNameWithoutExtension(fileName), Path.GetExtension(fileName));
        }

        var sourceRelative = Normalize(Path.GetRelativePath(project.RootPath, source));
        var targetRelative = Normalize(Path.GetRelativePath(project.RootPath, destination));
        var binderBefore = await ReadBinderSnapshotAsync(project.RootPath, cancellationToken);
        var log = NewLog(ProjectMutationKind.Reparent, node.PersistentId, binderBefore) with
        {
            SourceRelativePath = sourceRelative,
            TargetRelativePath = targetRelative,
            OldParentPersistentId = oldParent.PersistentId,
            NewParentPersistentId = newParent.PersistentId,
            OldIndex = oldIndex,
            NewIndex = targetIndex
        };
        await SaveLogAsync(project.RootPath, log, cancellationToken);

        try
        {
            MovePath(source, destination);
            oldParent.RemoveChild(node);
            newParent.InsertChild(Math.Clamp(targetIndex, 0, newParent.Children.Count), node);
            RepathSubtree(node, sourceRelative, targetRelative);
            await _binderStore.SaveAsync(project, cancellationToken);

            log.BinderAfter = await ReadBinderSnapshotAsync(project.RootPath, cancellationToken);
            log.State = MutationStates.Completed;
            log.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveLogAsync(project.RootPath, log, cancellationToken);
            return new ProjectMutationResult(true, log.Id, $"Moved {node.Title}.");
        }
        catch (Exception ex)
        {
            try
            {
                newParent.RemoveChild(node);
                if (!oldParent.Children.Contains(node)) oldParent.InsertChild(Math.Clamp(oldIndex, 0, oldParent.Children.Count), node);
                if (PathExists(destination) && !PathExists(source)) MovePath(destination, source);
                RepathSubtree(node, targetRelative, sourceRelative);
                await RestoreBinderSnapshotAsync(project.RootPath, binderBefore, CancellationToken.None);
            }
            catch { }
            await MarkRolledBackAsync(project.RootPath, log, ex, CancellationToken.None);
            throw;
        }
    }

    private async Task<ProjectMutationResult> ConvertNodeTypeAsync(
        BookProject project,
        ProjectMutationRequest request,
        CancellationToken cancellationToken)
    {
        var node = RequireNode(project, request.NodePersistentId);
        var newKind = request.NewNodeKind ?? throw new ArgumentException("A target node type is required.", nameof(request));
        if (ReferenceEquals(node, project.Root) || newKind == NodeKind.Book)
            throw new InvalidOperationException("The project root cannot be converted.");
        var newContainer = newKind is NodeKind.Part or NodeKind.Folder;
        if (node.IsContainer != newContainer)
            throw new InvalidOperationException("Converting between container and document node types would require a content migration and is not allowed.");
        if (node.Kind == newKind) return ProjectMutationResult.NoChange("The binder item already has that type.");

        var binderBefore = await ReadBinderSnapshotAsync(project.RootPath, cancellationToken);
        var oldKind = node.Kind;
        var log = NewLog(ProjectMutationKind.ConvertNodeType, node.PersistentId, binderBefore) with
        {
            OldNodeKind = oldKind,
            NewNodeKind = newKind
        };
        await SaveLogAsync(project.RootPath, log, cancellationToken);
        try
        {
            node.ChangeKind(newKind);
            await _binderStore.SaveAsync(project, cancellationToken);
            log.BinderAfter = await ReadBinderSnapshotAsync(project.RootPath, cancellationToken);
            log.State = MutationStates.Completed;
            log.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveLogAsync(project.RootPath, log, cancellationToken);
            return new ProjectMutationResult(true, log.Id, $"Converted {node.Title} to {newKind}.");
        }
        catch (Exception ex)
        {
            node.ChangeKind(oldKind);
            await RestoreBinderSnapshotAsync(project.RootPath, binderBefore, CancellationToken.None);
            await MarkRolledBackAsync(project.RootPath, log, ex, CancellationToken.None);
            throw;
        }
    }

    private static async Task ApplyFilesystemDirectionAsync(
        string root,
        MutationLog log,
        bool undo,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(log.Operation, "Restore", StringComparison.Ordinal))
        {
            var from = undo ? log.TargetRelativePath : log.SourceRelativePath;
            var to = undo ? log.SourceRelativePath : log.TargetRelativePath;
            MoveIfPresent(root, from, to);
            return;
        }

        switch (log.Kind)
        {
            case ProjectMutationKind.Rename:
            case ProjectMutationKind.Reparent:
                MoveIfPresent(
                    root,
                    undo ? log.TargetRelativePath : log.SourceRelativePath,
                    undo ? log.SourceRelativePath : log.TargetRelativePath);
                if (log.Kind == ProjectMutationKind.Rename)
                {
                    var path = Absolute(root, undo ? log.SourceRelativePath : log.TargetRelativePath);
                    var content = undo ? log.FileBeforeBase64 : log.FileAfterBase64;
                    if (content is { Length: > 0 } && File.Exists(path))
                        await File.WriteAllBytesAsync(path, Convert.FromBase64String(content), cancellationToken);
                }
                break;
            case ProjectMutationKind.Delete:
                MoveIfPresent(
                    root,
                    undo ? log.TargetRelativePath : log.SourceRelativePath,
                    undo ? log.SourceRelativePath : log.TargetRelativePath);
                break;
            case ProjectMutationKind.Reorder:
            case ProjectMutationKind.ConvertNodeType:
                break;
        }
    }

    private static async Task UpdateTrashRestoreStateAsync(
        string root,
        MutationLog log,
        bool undo,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(log.Operation, "Restore", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(log.TrashId)) return;
        var record = await LoadTrashRecordAsync(root, log.TrashId, cancellationToken);
        if (record is null) return;
        record.RestoredAt = undo ? null : DateTimeOffset.UtcNow;
        await SaveTrashRecordAsync(root, record, cancellationToken);
    }

    private static async Task RecoverPreparedAsync(string root, MutationLog log, CancellationToken cancellationToken)
    {
        if (log.Kind is ProjectMutationKind.Rename or ProjectMutationKind.Reparent or ProjectMutationKind.Delete ||
            string.Equals(log.Operation, "Restore", StringComparison.Ordinal))
        {
            var source = Absolute(root, log.SourceRelativePath);
            var target = Absolute(root, log.TargetRelativePath);
            if (!PathExists(source) && PathExists(target)) MovePath(target, source);
        }
        if (log.Kind == ProjectMutationKind.Rename && log.FileBeforeBase64 is { Length: > 0 })
        {
            var path = Absolute(root, log.SourceRelativePath);
            if (File.Exists(path))
                await File.WriteAllBytesAsync(path, Convert.FromBase64String(log.FileBeforeBase64), cancellationToken);
        }
        await RestoreBinderSnapshotAsync(root, log.BinderBefore, cancellationToken);
    }

    private static void MoveIfPresent(string root, string? sourceRelative, string? targetRelative)
    {
        if (string.IsNullOrWhiteSpace(sourceRelative) || string.IsNullOrWhiteSpace(targetRelative)) return;
        var source = Absolute(root, sourceRelative);
        var target = Absolute(root, targetRelative);
        if (!PathExists(source)) return;
        if (PathExists(target)) throw new IOException($"Cannot move '{sourceRelative}' because '{targetRelative}' already exists.");
        MovePath(source, target);
    }

    private static async Task AbandonRedoStackAsync(string projectRoot, CancellationToken cancellationToken)
    {
        foreach (var log in (await LoadLogsAsync(projectRoot, cancellationToken))
                     .Where(static item => string.Equals(item.State, MutationStates.Undone, StringComparison.Ordinal)))
        {
            log.State = MutationStates.Abandoned;
            log.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveLogAsync(projectRoot, log, cancellationToken);
        }
    }

    private static MutationLog NewLog(ProjectMutationKind kind, string persistentId, string binderBefore)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Version = 1,
            Kind = kind,
            Operation = kind.ToString(),
            NodePersistentId = persistentId,
            State = MutationStates.Prepared,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            BinderBefore = binderBefore
        };

    private static async Task MarkRolledBackAsync(
        string projectRoot,
        MutationLog log,
        Exception error,
        CancellationToken cancellationToken)
    {
        log.State = MutationStates.RolledBack;
        log.Error = error.Message;
        log.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveLogAsync(projectRoot, log, cancellationToken);
    }

    private static async Task<string> ReadBinderSnapshotAsync(string root, CancellationToken cancellationToken)
    {
        var path = BinderPath(root);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : string.Empty;
    }

    private static async Task RestoreBinderSnapshotAsync(
        string root,
        string? content,
        CancellationToken cancellationToken)
    {
        var path = BinderPath(root);
        if (string.IsNullOrEmpty(content))
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        await AtomicFileWriter.WriteTextAsync(path, content, cancellationToken);
    }

    private static string BinderPath(string root)
        => Path.Combine(root, TypescribeDirectory, BinderFile);

    private static string HistoryRoot(string root)
        => Path.Combine(root, TypescribeDirectory, HistoryDirectory);

    private static string TrashRoot(string root)
        => Path.Combine(root, TypescribeDirectory, TrashDirectory);

    private static string TrashMetadataPath(string root, string id)
        => Path.Combine(TrashRoot(root), id + ".json");

    private static string LogPath(string root, string id)
        => Path.Combine(HistoryRoot(root), id + ".json");

    private static async Task SaveLogAsync(string root, MutationLog log, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(HistoryRoot(root));
        await AtomicFileWriter.WriteTextAsync(
            LogPath(root, log.Id),
            JsonSerializer.Serialize(log, JsonOptions),
            cancellationToken);
    }

    private static async Task<IReadOnlyList<MutationLog>> LoadLogsAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var directory = HistoryRoot(root);
        if (!Directory.Exists(directory)) return [];
        var result = new List<MutationLog>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var log = JsonSerializer.Deserialize<MutationLog>(
                    await File.ReadAllTextAsync(file, cancellationToken), JsonOptions);
                if (log is not null) result.Add(log);
            }
            catch
            {
                // A damaged historical entry is retained for manual inspection and skipped.
            }
        }
        return result;
    }

    private static async Task SaveTrashRecordAsync(
        string root,
        TrashRecord record,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(TrashRoot(root));
        await AtomicFileWriter.WriteTextAsync(
            TrashMetadataPath(root, record.TrashId),
            JsonSerializer.Serialize(record, JsonOptions),
            cancellationToken);
    }

    private static async Task<TrashRecord?> LoadTrashRecordAsync(
        string root,
        string id,
        CancellationToken cancellationToken)
    {
        var path = TrashMetadataPath(root, id);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<TrashRecord>(
            await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
    }

    private static ProjectTrashItem ToTrashItem(TrashRecord record)
        => new(
            record.TrashId,
            record.PersistentId,
            record.Title,
            record.Kind,
            record.OriginalRelativePath,
            record.ParentPersistentId,
            record.BinderPosition,
            record.DeletedAt,
            $"{record.Node.Status} • {record.Node.Label} • {record.Node.Keywords}".Trim(' ', '•'));

    private static NodeSnapshot CaptureSnapshot(ProjectNode node)
        => new()
        {
            Id = node.Id,
            PersistentId = node.PersistentId,
            Title = node.Title,
            Kind = node.Kind,
            RelativePath = node.RelativePath,
            Included = node.IncludeInCompilation,
            Synopsis = node.Synopsis,
            Notes = node.Notes,
            Status = node.Status,
            Label = node.Label,
            Keywords = node.Keywords,
            TargetWords = node.TargetWords,
            CustomMetadata = node.CustomMetadata.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.OrdinalIgnoreCase),
            Comments = node.Comments.ToList(),
            Children = node.Children.Select(CaptureSnapshot).ToList()
        };

    private static ProjectNode RestoreSnapshot(NodeSnapshot snapshot)
    {
        var node = new ProjectNode(snapshot.Id, snapshot.Title, snapshot.Kind, snapshot.RelativePath, snapshot.PersistentId)
        {
            IncludeInCompilation = snapshot.Included
        };
        node.UpdateMetadata(snapshot.Synopsis, snapshot.Notes, snapshot.Status, snapshot.Label, snapshot.Keywords, snapshot.TargetWords);
        node.ReplaceCustomMetadata(snapshot.CustomMetadata);
        node.ReplaceComments(snapshot.Comments);
        foreach (var child in snapshot.Children) node.AddChild(RestoreSnapshot(child));
        return node;
    }

    private static ProjectNode RequireNode(BookProject project, string persistentId)
        => string.Equals(project.Root.PersistentId, persistentId, StringComparison.Ordinal)
            ? project.Root
            : FindNode(project.Root, persistentId)
              ?? throw new InvalidOperationException("The binder item no longer exists.");

    private static ProjectNode? FindNode(ProjectNode root, string persistentId)
    {
        foreach (var child in root.Children)
        {
            if (string.Equals(child.PersistentId, persistentId, StringComparison.Ordinal)) return child;
            var found = FindNode(child, persistentId);
            if (found is not null) return found;
        }
        return null;
    }

    private static ProjectNode? FindParent(ProjectNode root, ProjectNode target)
    {
        foreach (var child in root.Children)
        {
            if (ReferenceEquals(child, target)) return root;
            var found = FindParent(child, target);
            if (found is not null) return found;
        }
        return null;
    }

    private static bool ContainsNode(ProjectNode root, ProjectNode target)
    {
        foreach (var child in root.Children)
        {
            if (ReferenceEquals(child, target) || ContainsNode(child, target)) return true;
        }
        return false;
    }

    private static string ResolveNodePath(BookProject project, ProjectNode node)
    {
        if (node.RelativePath is null) throw new InvalidOperationException("The selected binder item has no project path.");
        return Absolute(project.RootPath, node.RelativePath);
    }

    private static string Absolute(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));
        var relativeToRoot = Path.GetRelativePath(fullRoot, candidate);
        if (Path.IsPathRooted(relativeToRoot) || relativeToRoot.Equals("..", StringComparison.Ordinal) ||
            relativeToRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativeToRoot.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidOperationException("Project path traversal was rejected.");
        return candidate;
    }

    private static void RepathSubtree(ProjectNode node, string oldPrefix, string newPrefix)
    {
        var current = Normalize(node.RelativePath ?? oldPrefix);
        var suffix = current.Length > oldPrefix.Length ? current[oldPrefix.Length..].TrimStart('/') : string.Empty;
        var next = suffix.Length == 0 ? newPrefix : Normalize(Path.Combine(newPrefix, suffix));
        node.Repath(next, next);
        foreach (var child in node.Children) RepathSubtree(child, oldPrefix, newPrefix);
    }

    private static void MovePath(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else if (File.Exists(source)) File.Move(source, destination);
        else throw new FileNotFoundException("The project item to move no longer exists.", source);
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string CreateUniquePath(string directory, string stem, string? extension)
    {
        var suffix = extension ?? string.Empty;
        var candidate = Path.Combine(directory, stem + suffix);
        if (!PathExists(candidate)) return candidate;
        for (var index = 2; index < 100_000; index++)
        {
            candidate = Path.Combine(directory, $"{stem}-{index}{suffix}");
            if (!PathExists(candidate)) return candidate;
        }
        throw new IOException("Unable to create a unique binder path.");
    }

    private static string Slugify(string value)
    {
        var builder = new StringBuilder();
        var dash = false;
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                dash = false;
            }
            else if (!dash && builder.Length > 0)
            {
                builder.Append('-');
                dash = true;
            }
        }
        var result = builder.ToString().Trim('-');
        return result.Length == 0 ? "untitled" : result;
    }

    private static string Normalize(string value) => value.Replace('\\', '/').TrimStart('/');

    private static async Task RewritePrimaryHeadingAsync(string path, string title, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return;
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        var heading = lines.FindIndex(static line => line.StartsWith("# ", StringComparison.Ordinal));
        if (heading >= 0) lines[heading] = "# " + title;
        else lines.Insert(0, "# " + title);
        await AtomicFileWriter.WriteTextAsync(path, string.Join(newline, lines), cancellationToken);
    }

    private sealed record MutationLog
    {
        public int Version { get; set; }
        public string Id { get; set; } = string.Empty;
        public ProjectMutationKind Kind { get; set; }
        public string Operation { get; set; } = string.Empty;
        public string NodePersistentId { get; set; } = string.Empty;
        public string State { get; set; } = MutationStates.Prepared;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string BinderBefore { get; set; } = string.Empty;
        public string BinderAfter { get; set; } = string.Empty;
        public string? SourceRelativePath { get; set; }
        public string? TargetRelativePath { get; set; }
        public string? OldTitle { get; set; }
        public string? NewTitle { get; set; }
        public string? OldParentPersistentId { get; set; }
        public string? NewParentPersistentId { get; set; }
        public int OldIndex { get; set; } = -1;
        public int NewIndex { get; set; } = -1;
        public NodeKind? OldNodeKind { get; set; }
        public NodeKind? NewNodeKind { get; set; }
        public string? TrashId { get; set; }
        public string? FileBeforeBase64 { get; set; }
        public string? FileAfterBase64 { get; set; }
        public string? Error { get; set; }
    }

    private sealed class TrashRecord
    {
        public string TrashId { get; set; } = string.Empty;
        public string PersistentId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public NodeKind Kind { get; set; }
        public string OriginalRelativePath { get; set; } = string.Empty;
        public string PayloadRelativePath { get; set; } = string.Empty;
        public string ParentPersistentId { get; set; } = string.Empty;
        public int BinderPosition { get; set; }
        public DateTimeOffset DeletedAt { get; set; }
        public DateTimeOffset? RestoredAt { get; set; }
        public NodeSnapshot Node { get; set; } = new();
    }

    private sealed class NodeSnapshot
    {
        public string Id { get; set; } = string.Empty;
        public string PersistentId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public NodeKind Kind { get; set; }
        public string? RelativePath { get; set; }
        public bool Included { get; set; } = true;
        public string Synopsis { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public string Status { get; set; } = "Draft";
        public string Label { get; set; } = string.Empty;
        public string Keywords { get; set; } = string.Empty;
        public int TargetWords { get; set; }
        public Dictionary<string, string> CustomMetadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<DocumentComment> Comments { get; set; } = [];
        public List<NodeSnapshot> Children { get; set; } = [];
    }

    private static class MutationStates
    {
        public const string Prepared = "prepared";
        public const string Completed = "completed";
        public const string Undone = "undone";
        public const string Abandoned = "abandoned";
        public const string RolledBack = "rolled-back";
    }
}
