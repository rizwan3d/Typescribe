namespace Typescribe.Domain.Models;

/// <summary>
/// Describes a merged rectangular table region using zero-based logical grid coordinates.
/// The top-left cell is the semantic anchor; covered cells remain in the grid as span continuations.
/// </summary>
public sealed record TableMergeSpan(int Row, int Column, int RowSpan, int ColumnSpan)
{
    public bool IsMerged => RowSpan > 1 || ColumnSpan > 1;
}
