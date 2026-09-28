namespace Typescribe.Desktop.ViewModels;

public sealed record OutlineItemViewModel(
    string Title,
    int Level,
    int SourceLine,
    int EndLine)
{
    public string DisplayText => $"{new string(' ', Math.Max(0, Level - 1) * 2)}{Title}";

    public override string ToString() => DisplayText;
}
