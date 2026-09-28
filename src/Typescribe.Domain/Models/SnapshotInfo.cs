namespace Typescribe.Domain.Models;

public sealed record SnapshotInfo(
    string Id,
    DateTimeOffset CreatedAt,
    string Label,
    int WordCount);
