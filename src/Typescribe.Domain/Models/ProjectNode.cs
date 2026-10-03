namespace Typescribe.Domain.Models;

public sealed class ProjectNode
{
    private readonly List<ProjectNode> _children = [];
    private readonly Dictionary<string, string> _customMetadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DocumentComment> _comments = [];

    public ProjectNode(
        string id,
        string title,
        NodeKind kind,
        string? relativePath = null,
        string? persistentId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Id = id;
        PersistentId = string.IsNullOrWhiteSpace(persistentId) ? Guid.NewGuid().ToString("N") : persistentId.Trim();
        Title = title.Trim();
        Kind = kind;
        RelativePath = relativePath;
    }

    public string Id { get; private set; }
    public string PersistentId { get; }
    public string Title { get; private set; }
    public NodeKind Kind { get; private set; }
    public string? RelativePath { get; private set; }
    public bool IncludeInCompilation { get; set; } = true;
    public string Synopsis { get; private set; } = string.Empty;
    public string Notes { get; private set; } = string.Empty;
    public string Status { get; private set; } = "Draft";
    public string Label { get; private set; } = string.Empty;
    public string Keywords { get; private set; } = string.Empty;
    public int TargetWords { get; private set; }
    public int? CachedWordCount { get; private set; }
    public IReadOnlyDictionary<string, string> CustomMetadata => _customMetadata;
    public IReadOnlyList<DocumentComment> Comments => _comments;
    public IReadOnlyList<ProjectNode> Children => _children;
    public bool IsDocument => RelativePath is not null && Kind is not NodeKind.Folder and not NodeKind.Part;
    public bool IsContainer => Kind is NodeKind.Book or NodeKind.Part or NodeKind.Folder;

    public void Rename(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Title = title.Trim();
    }

    public void ChangeKind(NodeKind kind) => Kind = kind;

    public void Repath(string id, string? relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        RelativePath = relativePath;
    }

    public void UpdateMetadata(
        string? synopsis,
        string? notes,
        string? status,
        string? label,
        string? keywords,
        int targetWords)
    {
        Synopsis = (synopsis ?? string.Empty).Trim();
        Notes = (notes ?? string.Empty).Trim();
        Status = string.IsNullOrWhiteSpace(status) ? "Draft" : status.Trim();
        Label = (label ?? string.Empty).Trim();
        Keywords = (keywords ?? string.Empty).Trim();
        TargetWords = Math.Max(0, targetWords);
    }

    public void SetCachedWordCount(int? wordCount)
        => CachedWordCount = wordCount is int value ? Math.Max(0, value) : null;

    public void SetCustomMetadata(string key, string? value)
    {
        var normalized = ProjectAuthoringState.NormalizeKey(key);
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0) _customMetadata.Remove(normalized);
        else _customMetadata[normalized] = trimmed;
    }

    public void ReplaceCustomMetadata(IEnumerable<KeyValuePair<string, string>> values)
    {
        _customMetadata.Clear();
        foreach (var pair in values) SetCustomMetadata(pair.Key, pair.Value);
    }

    public void ReplaceComments(IEnumerable<DocumentComment> comments)
    {
        _comments.Clear();
        _comments.AddRange(comments.OrderBy(static comment => comment.Line).ThenBy(static comment => comment.CreatedAt));
    }

    public void AddComment(DocumentComment comment)
    {
        ArgumentNullException.ThrowIfNull(comment);
        _comments.Add(comment);
        _comments.Sort(static (left, right) =>
        {
            var line = left.Line.CompareTo(right.Line);
            return line != 0 ? line : left.CreatedAt.CompareTo(right.CreatedAt);
        });
    }

    public bool SetCommentResolved(string id, bool resolved)
    {
        var index = _comments.FindIndex(comment => string.Equals(comment.Id, id, StringComparison.Ordinal));
        if (index < 0) return false;
        _comments[index] = _comments[index] with { Resolved = resolved };
        return true;
    }

    public bool RemoveComment(string id)
    {
        var index = _comments.FindIndex(comment => string.Equals(comment.Id, id, StringComparison.Ordinal));
        if (index < 0) return false;
        _comments.RemoveAt(index);
        return true;
    }

    public void AddChild(ProjectNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        _children.Add(child);
    }

    public void InsertChild(int index, ProjectNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        _children.Insert(Math.Clamp(index, 0, _children.Count), child);
    }

    public bool RemoveChild(ProjectNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        return _children.Remove(child);
    }

    public int IndexOf(ProjectNode child) => _children.IndexOf(child);

    public bool MoveChild(ProjectNode child, int offset)
    {
        var index = _children.IndexOf(child);
        if (index < 0) return false;
        var target = index + offset;
        if (target < 0 || target >= _children.Count) return false;
        _children.RemoveAt(index);
        _children.Insert(target, child);
        return true;
    }
}
