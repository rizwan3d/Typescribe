namespace Typescribe.Domain.Models;

public sealed class ProjectNode
{
    private readonly List<ProjectNode> _children = [];

    public ProjectNode(string id, string title, NodeKind kind, string? relativePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Id = id;
        Title = title;
        Kind = kind;
        RelativePath = relativePath;
    }

    public string Id { get; }
    public string Title { get; private set; }
    public NodeKind Kind { get; }
    public string? RelativePath { get; }
    public bool IncludeInCompilation { get; set; } = true;
    public IReadOnlyList<ProjectNode> Children => _children;
    public bool IsDocument => RelativePath is not null;

    public void Rename(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Title = title.Trim();
    }

    public void AddChild(ProjectNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        _children.Add(child);
    }
}
