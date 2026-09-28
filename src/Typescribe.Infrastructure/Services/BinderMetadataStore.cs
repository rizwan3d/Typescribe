using System.Text;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

internal sealed class BinderMetadataStore
{
    private const string MetadataDirectory = ".typescribe";
    private const string BinderFile = "binder.tsv";

    public IReadOnlyDictionary<string, BinderMetadata> Load(string projectRoot)
    {
        var path = GetPath(projectRoot);
        if (!File.Exists(path)) return new Dictionary<string, BinderMetadata>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, BinderMetadata>(StringComparer.OrdinalIgnoreCase);
        var order = 0;
        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('#')) continue;
            var fields = raw.Split('\t');
            if (fields.Length < 3) continue;
            if (!Enum.TryParse<NodeKind>(fields[0], ignoreCase: true, out var kind)) continue;
            if (!bool.TryParse(fields[1], out var included)) included = true;
            var relativePath = Unescape(fields[2]);
            if (string.IsNullOrWhiteSpace(relativePath)) continue;
            var title = fields.Length >= 4 ? Unescape(fields[3]) : string.Empty;
            var synopsis = fields.Length >= 5 ? Unescape(fields[4]) : string.Empty;
            var notes = fields.Length >= 6 ? Unescape(fields[5]) : string.Empty;
            var status = fields.Length >= 7 ? Unescape(fields[6]) : "Draft";
            var label = fields.Length >= 8 ? Unescape(fields[7]) : string.Empty;
            var keywords = fields.Length >= 9 ? Unescape(fields[8]) : string.Empty;
            var targetWords = fields.Length >= 10 && int.TryParse(fields[9], out var parsedTarget)
                ? Math.Max(0, parsedTarget)
                : 0;
            var persistentId = fields.Length >= 11 ? Unescape(fields[10]) : string.Empty;
            result[Normalize(relativePath)] = new BinderMetadata(
                kind,
                included,
                order++,
                title,
                synopsis,
                notes,
                status,
                label,
                keywords,
                targetWords,
                persistentId);
        }
        return result;
    }

    public Task SaveAsync(BookProject project, CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# kind\tincluded\trelative-path\ttitle\tsynopsis\tnotes\tstatus\tlabel\tkeywords\ttarget-words\tpersistent-id");
        foreach (var node in Flatten(project.Root))
        {
            if (node.RelativePath is null) continue;
            builder.Append(node.Kind).Append('\t')
                .Append(node.IncludeInCompilation).Append('\t')
                .Append(Escape(Normalize(node.RelativePath))).Append('\t')
                .Append(Escape(node.Title)).Append('\t')
                .Append(Escape(node.Synopsis)).Append('\t')
                .Append(Escape(node.Notes)).Append('\t')
                .Append(Escape(node.Status)).Append('\t')
                .Append(Escape(node.Label)).Append('\t')
                .Append(Escape(node.Keywords)).Append('\t')
                .Append(node.TargetWords).Append('\t')
                .Append(Escape(node.PersistentId)).AppendLine();
        }
        return AtomicFileWriter.WriteTextAsync(GetPath(project.RootPath), builder.ToString(), cancellationToken);
    }

    private static IEnumerable<ProjectNode> Flatten(ProjectNode root)
    {
        foreach (var child in root.Children)
        {
            yield return child;
            foreach (var descendant in Flatten(child)) yield return descendant;
        }
    }

    private static string GetPath(string projectRoot)
    {
        var directory = Path.Combine(projectRoot, MetadataDirectory);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, BinderFile);
    }

    internal static string Normalize(string relativePath) => relativePath.Replace('\\', '/').TrimStart('/');

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string Unescape(string value)
    {
        var output = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                output.Append(value[i]);
                continue;
            }

            i++;
            output.Append(value[i] switch
            {
                't' => '\t',
                'r' => '\r',
                'n' => '\n',
                '\\' => '\\',
                _ => value[i]
            });
        }
        return output.ToString();
    }
}

internal sealed record BinderMetadata(
    NodeKind Kind,
    bool Included,
    int Order,
    string Title,
    string Synopsis,
    string Notes,
    string Status,
    string Label,
    string Keywords,
    int TargetWords,
    string PersistentId);
