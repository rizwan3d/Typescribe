namespace Typescribe.Application.Models;

public sealed record SearchOptions(
    bool MatchCase = false,
    bool UseRegex = false,
    bool WholeWord = false,
    int MaxResults = 500)
{
    public int EffectiveMaxResults => Math.Clamp(MaxResults, 1, 5_000);
}
