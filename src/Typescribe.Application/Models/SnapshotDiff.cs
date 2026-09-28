namespace Typescribe.Application.Models;

public enum DiffLineKind
{
    Unchanged,
    Added,
    Removed
}

public sealed record DiffLine(DiffLineKind Kind, int? OldLine, int? NewLine, string Text)
{
    public override string ToString()
    {
        var marker = Kind switch
        {
            DiffLineKind.Added => "+",
            DiffLineKind.Removed => "-",
            _ => " "
        };
        return $"{marker} {OldLine?.ToString() ?? "":>4} {NewLine?.ToString() ?? "":>4}  {Text}";
    }
}
