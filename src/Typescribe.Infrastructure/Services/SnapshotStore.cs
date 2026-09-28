using System.Globalization;
using System.Text;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

internal sealed class SnapshotStore
{
    private const string MetadataDirectory = ".typescribe";
    private const string SnapshotsDirectory = "snapshots";

    public async Task<SnapshotInfo> CreateAsync(
        BookProject project,
        ProjectNode node,
        string content,
        string label,
        CancellationToken cancellationToken)
    {
        EnsureDocument(node);
        var directory = GetDocumentDirectory(project, node);
        Directory.CreateDirectory(directory);

        var createdAt = DateTimeOffset.UtcNow;
        var id = $"{createdAt:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}";
        var sourcePath = Path.Combine(directory, id + ".md");
        var metadataPath = Path.Combine(directory, id + ".meta");
        var normalizedLabel = (label ?? string.Empty).Trim();
        var wordCount = CountWords(content ?? string.Empty);

        await AtomicFileWriter.WriteTextAsync(sourcePath, content ?? string.Empty, cancellationToken);
        var metadata = new StringBuilder()
            .Append("created=").AppendLine(createdAt.ToString("O", CultureInfo.InvariantCulture))
            .Append("label=").AppendLine(Escape(normalizedLabel))
            .Append("words=").AppendLine(wordCount.ToString(CultureInfo.InvariantCulture))
            .ToString();
        await AtomicFileWriter.WriteTextAsync(metadataPath, metadata, cancellationToken);

        return new SnapshotInfo(id, createdAt, normalizedLabel, wordCount);
    }

    public Task<IReadOnlyList<SnapshotInfo>> ListAsync(
        BookProject project,
        ProjectNode node,
        CancellationToken cancellationToken)
    {
        EnsureDocument(node);
        cancellationToken.ThrowIfCancellationRequested();
        var directory = GetDocumentDirectory(project, node);
        if (!Directory.Exists(directory))
            return Task.FromResult<IReadOnlyList<SnapshotInfo>>([]);

        var snapshots = new List<SnapshotInfo>();
        foreach (var sourcePath in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(sourcePath);
            var metadataPath = Path.Combine(directory, id + ".meta");
            snapshots.Add(ReadInfo(id, sourcePath, metadataPath));
        }

        snapshots.Sort(static (left, right) => right.CreatedAt.CompareTo(left.CreatedAt));
        return Task.FromResult<IReadOnlyList<SnapshotInfo>>(snapshots);
    }

    public Task<string> ReadAsync(
        BookProject project,
        ProjectNode node,
        SnapshotInfo snapshot,
        CancellationToken cancellationToken)
    {
        EnsureDocument(node);
        ArgumentNullException.ThrowIfNull(snapshot);
        var path = ResolveSnapshotPath(project, node, snapshot.Id, ".md");
        if (!File.Exists(path)) throw new FileNotFoundException("Snapshot content was not found.", path);
        return File.ReadAllTextAsync(path, cancellationToken);
    }

    public Task DeleteAsync(
        BookProject project,
        ProjectNode node,
        SnapshotInfo snapshot,
        CancellationToken cancellationToken)
    {
        EnsureDocument(node);
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var sourcePath = ResolveSnapshotPath(project, node, snapshot.Id, ".md");
        var metadataPath = ResolveSnapshotPath(project, node, snapshot.Id, ".meta");
        if (File.Exists(sourcePath)) File.Delete(sourcePath);
        if (File.Exists(metadataPath)) File.Delete(metadataPath);
        return Task.CompletedTask;
    }

    private static SnapshotInfo ReadInfo(string id, string sourcePath, string metadataPath)
    {
        DateTimeOffset createdAt = File.GetLastWriteTimeUtc(sourcePath);
        var label = string.Empty;
        var words = 0;

        if (File.Exists(metadataPath))
        {
            foreach (var raw in File.ReadLines(metadataPath, Encoding.UTF8))
            {
                var separator = raw.IndexOf('=');
                if (separator <= 0) continue;
                var key = raw[..separator];
                var value = raw[(separator + 1)..];
                switch (key)
                {
                    case "created" when DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed):
                        createdAt = parsed;
                        break;
                    case "label":
                        label = Unescape(value);
                        break;
                    case "words" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedWords):
                        words = Math.Max(0, parsedWords);
                        break;
                }
            }
        }

        if (words == 0)
        {
            try { words = CountWords(File.ReadAllText(sourcePath, Encoding.UTF8)); }
            catch (IOException) { }
        }

        return new SnapshotInfo(id, createdAt, label, words);
    }

    private static string GetDocumentDirectory(BookProject project, ProjectNode node)
    {
        var root = Path.Combine(project.RootPath, MetadataDirectory, SnapshotsDirectory);
        return Path.Combine(root, node.PersistentId);
    }

    private static string ResolveSnapshotPath(BookProject project, ProjectNode node, string id, string extension)
    {
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid snapshot identifier.");
        return Path.Combine(GetDocumentDirectory(project, node), id + extension);
    }

    private static void EnsureDocument(ProjectNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.IsDocument) throw new InvalidOperationException("Snapshots are available only for manuscript documents.");
    }

    private static int CountWords(string value)
    {
        var count = 0;
        var inWord = false;
        foreach (var rune in value.EnumerateRunes())
        {
            var isWord = Rune.IsLetterOrDigit(rune) || rune.Value is '_' or '\'' or 0x2019;
            if (isWord && !inWord) count++;
            inWord = isWord;
        }
        return count;
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string Unescape(string value)
    {
        var output = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length)
            {
                output.Append(value[index]);
                continue;
            }

            index++;
            output.Append(value[index] switch
            {
                'r' => '\r',
                'n' => '\n',
                '\\' => '\\',
                _ => value[index]
            });
        }
        return output.ToString();
    }
}
