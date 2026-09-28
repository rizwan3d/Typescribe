using Typescribe.Application.Models;

namespace Typescribe.Application.Services;

public sealed class SnapshotDiffService
{
    private const int MaxMatrixLines = 1600;

    public IReadOnlyList<DiffLine> Compare(string oldText, string newText)
    {
        var oldLines = Normalize(oldText);
        var newLines = Normalize(newText);
        if (oldLines.Length > MaxMatrixLines || newLines.Length > MaxMatrixLines)
            return CompareLarge(oldLines, newLines);

        var table = new int[oldLines.Length + 1, newLines.Length + 1];
        for (var oldIndex = oldLines.Length - 1; oldIndex >= 0; oldIndex--)
        {
            for (var newIndex = newLines.Length - 1; newIndex >= 0; newIndex--)
            {
                table[oldIndex, newIndex] = string.Equals(oldLines[oldIndex], newLines[newIndex], StringComparison.Ordinal)
                    ? table[oldIndex + 1, newIndex + 1] + 1
                    : Math.Max(table[oldIndex + 1, newIndex], table[oldIndex, newIndex + 1]);
            }
        }

        var result = new List<DiffLine>();
        var i = 0;
        var j = 0;
        while (i < oldLines.Length && j < newLines.Length)
        {
            if (string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal))
            {
                result.Add(new DiffLine(DiffLineKind.Unchanged, i + 1, j + 1, oldLines[i]));
                i++;
                j++;
            }
            else if (table[i + 1, j] >= table[i, j + 1])
            {
                result.Add(new DiffLine(DiffLineKind.Removed, i + 1, null, oldLines[i++]));
            }
            else
            {
                result.Add(new DiffLine(DiffLineKind.Added, null, j + 1, newLines[j++]));
            }
        }

        while (i < oldLines.Length) result.Add(new DiffLine(DiffLineKind.Removed, i + 1, null, oldLines[i++]));
        while (j < newLines.Length) result.Add(new DiffLine(DiffLineKind.Added, null, j + 1, newLines[j++]));
        return result;
    }

    private static IReadOnlyList<DiffLine> CompareLarge(string[] oldLines, string[] newLines)
    {
        var result = new List<DiffLine>();
        var common = Math.Min(oldLines.Length, newLines.Length);
        for (var index = 0; index < common; index++)
        {
            if (string.Equals(oldLines[index], newLines[index], StringComparison.Ordinal))
            {
                result.Add(new DiffLine(DiffLineKind.Unchanged, index + 1, index + 1, oldLines[index]));
            }
            else
            {
                result.Add(new DiffLine(DiffLineKind.Removed, index + 1, null, oldLines[index]));
                result.Add(new DiffLine(DiffLineKind.Added, null, index + 1, newLines[index]));
            }
        }
        for (var index = common; index < oldLines.Length; index++)
            result.Add(new DiffLine(DiffLineKind.Removed, index + 1, null, oldLines[index]));
        for (var index = common; index < newLines.Length; index++)
            result.Add(new DiffLine(DiffLineKind.Added, null, index + 1, newLines[index]));
        return result;
    }

    private static string[] Normalize(string value)
        => (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
}
