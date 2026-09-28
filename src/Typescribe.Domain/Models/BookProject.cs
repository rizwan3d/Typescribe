namespace Typescribe.Domain.Models;

public sealed class BookProject
{
    public required string RootPath { get; init; }
    public required string Title { get; init; }
    public string Author { get; init; } = string.Empty;
    public string Language { get; init; } = "en";
    public required ProjectNode Root { get; init; }
}
