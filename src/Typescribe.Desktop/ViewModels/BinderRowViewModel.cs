using Typescribe.Domain.Models;

namespace Typescribe.Desktop.ViewModels;

public sealed class BinderRowViewModel(ProjectNode node, int depth)
{
    public ProjectNode Node { get; } = node;
    public int Depth { get; } = depth;
    public bool IsIncluded => Node.IncludeInCompilation;

    public override string ToString()
        => $"{new string(' ', Depth * 3)}{(Node.IncludeInCompilation ? "✓" : "○")} {Icon(Node.Kind)} {Node.Title}";

    private static string Icon(NodeKind kind) => kind switch
    {
        NodeKind.Book => "▣",
        NodeKind.Part => "◆",
        NodeKind.Folder => "▸",
        NodeKind.Chapter => "◫",
        NodeKind.Section => "§",
        NodeKind.Scene => "▪",
        NodeKind.Research => "⌕",
        NodeKind.Note => "✎",
        _ => "•"
    };
}
