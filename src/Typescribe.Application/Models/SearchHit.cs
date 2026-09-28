namespace Typescribe.Application.Models;

public sealed record SearchHit(string NodeId, string Title, string RelativePath, int Line, string Preview);
