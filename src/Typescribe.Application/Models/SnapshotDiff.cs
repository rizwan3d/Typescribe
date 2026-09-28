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
        var oldLine = OldLine?.ToString() ?? string.Empty;
        var newLine = NewLine?.ToString() ?? string.Empty;
        return $"{marker} {oldLine,4} {newLine,4}  {Text}";
    }
}
