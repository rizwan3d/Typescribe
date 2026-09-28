using Typescribe.Domain.Models;

namespace Typescribe.Desktop.ViewModels;

public sealed class CorkboardCardViewModel(ProjectNode node, int currentWords)
{
    public ProjectNode Node { get; } = node;
    public string Title => Node.Title;
    public string Synopsis => string.IsNullOrWhiteSpace(Node.Synopsis) ? "No synopsis yet." : Node.Synopsis;
    public string Status => Node.Status;
    public string Label => Node.Label;
    public string Kind => Node.Kind.ToString();
    public bool IsIncluded => Node.IncludeInCompilation;
    public int TargetWords => Node.TargetWords;
    public int CurrentWords { get; set; } = Math.Max(0, currentWords);
    public double Progress => TargetWords <= 0 ? 0 : Math.Clamp(CurrentWords / (double)TargetWords, 0, 1);

    public string Footer
    {
        get
        {
            var compile = IsIncluded ? "Compile" : "Excluded";
            var target = TargetWords > 0 ? $" • {CurrentWords:N0}/{TargetWords:N0} words" : $" • {CurrentWords:N0} words";
            var status = string.IsNullOrWhiteSpace(Status) ? string.Empty : $" • {Status}";
            return $"{Kind} • {compile}{status}{target}";
        }
    }
}
