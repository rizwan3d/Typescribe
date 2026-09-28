using Typescribe.Domain.Models;

namespace Typescribe.Desktop.ViewModels;

public sealed class BinderRowViewModel(ProjectNode node, int depth)
{
    public ProjectNode Node { get; } = node;
    public int Depth { get; } = depth;
    public override string ToString() => $"{new string(' ', Depth * 3)}{Icon(Node.Kind)} {Node.Title}";

    private static string Icon(NodeKind kind) => kind switch
    {
        NodeKind.Book => "▣",
        NodeKind.Part or NodeKind.Folder => "▸",
        NodeKind.Chapter => "◫",
        NodeKind.Section or NodeKind.Scene => "▪",
        NodeKind.Research => "⌕",
        NodeKind.Note => "✎",
        _ => "•"
    };
}
