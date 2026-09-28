namespace Typescribe.Domain.Models;

public sealed class ProjectNode
{
    private readonly List<ProjectNode> _children = [];

    public ProjectNode(string id, string title, NodeKind kind, string? relativePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Id = id;
        Title = title.Trim();
        Kind = kind;
        RelativePath = relativePath;
    }

    public string Id { get; private set; }
    public string Title { get; private set; }
    public NodeKind Kind { get; private set; }
    public string? RelativePath { get; private set; }
    public bool IncludeInCompilation { get; set; } = true;
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
