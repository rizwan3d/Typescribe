using System.Globalization;
using System.Text;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

internal sealed class AuthoringStateStore
{
    private const string MetadataDirectory = ".typescribe";
    private const string FileName = "authoring.tsv";

    public ProjectAuthoringState Load(string projectRoot, ProjectNode root)
    {
        var state = new ProjectAuthoringState();
        var path = GetPath(projectRoot);
        if (!File.Exists(path)) return state;

        var nodes = Flatten(root).ToDictionary(static node => node.PersistentId, StringComparer.Ordinal);
        var fields = new List<CustomMetadataDefinition>();
        var collections = new List<ProjectCollection>();

        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('#')) continue;
            var values = raw.Split('\t');
            if (values.Length == 0) continue;

            switch (values[0])
            {
                case "targets" when values.Length >= 5:
                    state.ProjectTargetWords = ParseNonNegative(values[1]);
                    state.DailyTargetWords = ParseNonNegative(values[2]);
                    if (DateOnly.TryParseExact(values[3], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                        state.DailyDate = date;
                    state.DailyBaselineWords = ParseNonNegative(values[4]);
                    break;

                case "field" when values.Length >= 3:
                    fields.Add(new CustomMetadataDefinition(values[1], Decode(values[2])));
                    break;

                case "value" when values.Length >= 4 && nodes.TryGetValue(values[1], out var node):
                    node.SetCustomMetadata(values[2], Decode(values[3]));
                    break;

                case "comment" when values.Length >= 7 && nodes.TryGetValue(values[1], out var commentNode):
                    if (!int.TryParse(values[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var line)) line = 1;
                    if (!bool.TryParse(values[4], out var resolved)) resolved = false;
                    if (!DateTimeOffset.TryParse(values[5], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created))
                        created = DateTimeOffset.UtcNow;
                    commentNode.AddComment(new DocumentComment(values[2], Math.Max(1, line), Decode(values[6]), resolved, created));
                    break;

                case "search" when values.Length >= 7:
                    collections.Add(new ProjectCollection(
                        values[1],
                        Decode(values[2]),
                        ProjectCollectionKind.Search,
                        Decode(values[3]),
                        ParseBool(values[4]),
                        ParseBool(values[5]),
                        ParseBool(values[6]),
                        []));
                    break;

                case "manual" when values.Length >= 4:
                    collections.Add(new ProjectCollection(
                        values[1],
                        Decode(values[2]),
                        ProjectCollectionKind.Manual,
                        string.Empty,
                        false,
                        false,
                        false,
                        values[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
                    break;
            }
        }

        state.ReplaceCustomFields(fields);
        state.ReplaceCollections(collections);
        return state;
    }

    public Task SaveAsync(BookProject project, CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Typescribe authoring state v1");
        builder.Append("targets\t")
            .Append(project.Authoring.ProjectTargetWords.ToString(CultureInfo.InvariantCulture)).Append('\t')
            .Append(project.Authoring.DailyTargetWords.ToString(CultureInfo.InvariantCulture)).Append('\t')
            .Append(project.Authoring.DailyDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\t')
            .Append(project.Authoring.DailyBaselineWords.ToString(CultureInfo.InvariantCulture)).AppendLine();

        foreach (var field in project.Authoring.CustomFields)
            builder.Append("field\t").Append(field.Key).Append('\t').Append(Encode(field.Name)).AppendLine();

        foreach (var node in Flatten(project.Root))
        {
            foreach (var pair in node.CustomMetadata.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("value\t").Append(node.PersistentId).Append('\t')
                    .Append(pair.Key).Append('\t').Append(Encode(pair.Value)).AppendLine();
            }

            foreach (var comment in node.Comments)
            {
                builder.Append("comment\t").Append(node.PersistentId).Append('\t')
                    .Append(comment.Id).Append('\t')
                    .Append(comment.Line.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(comment.Resolved).Append('\t')
                    .Append(comment.CreatedAt.ToString("O", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(Encode(comment.Text)).AppendLine();
            }
        }

        foreach (var collection in project.Authoring.Collections)
        {
            if (collection.Kind == ProjectCollectionKind.Search)
            {
                builder.Append("search\t").Append(collection.Id).Append('\t')
                    .Append(Encode(collection.Name)).Append('\t')
                    .Append(Encode(collection.Query)).Append('\t')
                    .Append(collection.MatchCase).Append('\t')
                    .Append(collection.UseRegex).Append('\t')
                    .Append(collection.WholeWord).AppendLine();
            }
            else
            {
                builder.Append("manual\t").Append(collection.Id).Append('\t')
                    .Append(Encode(collection.Name)).Append('\t')
                    .Append(string.Join(',', collection.NodePersistentIds)).AppendLine();
            }
        }

        return AtomicFileWriter.WriteTextAsync(GetPath(project.RootPath), builder.ToString(), cancellationToken);
    }

    private static IEnumerable<ProjectNode> Flatten(ProjectNode node)
    {
        foreach (var child in node.Children)
        {
            yield return child;
            foreach (var descendant in Flatten(child)) yield return descendant;
        }
    }

    private static int ParseNonNegative(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? Math.Max(0, parsed) : 0;

    private static bool ParseBool(string value) => bool.TryParse(value, out var parsed) && parsed;

    private static string GetPath(string root)
    {
        var directory = Path.Combine(root, MetadataDirectory);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, FileName);
    }

    private static string Encode(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));

    private static string Decode(string value)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        catch (FormatException) { return string.Empty; }
    }
}
