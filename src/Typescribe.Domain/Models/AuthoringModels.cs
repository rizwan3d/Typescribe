namespace Typescribe.Domain.Models;

public sealed class ProjectAuthoringState
{
    private readonly List<CustomMetadataDefinition> _customFields = [];
    private readonly List<ProjectCollection> _collections = [];

    public int ProjectTargetWords { get; set; }
    public int DailyTargetWords { get; set; }
    public int SessionTargetWords { get; set; }
    public DateOnly DailyDate { get; set; }
    public int DailyBaselineWords { get; set; }
    public IReadOnlyList<CustomMetadataDefinition> CustomFields => _customFields;
    public IReadOnlyList<ProjectCollection> Collections => _collections;

    public void ReplaceCustomFields(IEnumerable<CustomMetadataDefinition> fields)
    {
        _customFields.Clear();
        foreach (var field in fields
                     .Where(static field => !string.IsNullOrWhiteSpace(field.Key) && !string.IsNullOrWhiteSpace(field.Name))
                     .DistinctBy(static field => field.Key, StringComparer.OrdinalIgnoreCase))
        {
            _customFields.Add(field with { Key = NormalizeKey(field.Key), Name = field.Name.Trim() });
        }
    }

    public void UpsertCustomField(CustomMetadataDefinition field)
    {
        ArgumentNullException.ThrowIfNull(field);
        var normalized = field with { Key = NormalizeKey(field.Key), Name = field.Name.Trim() };
        var index = _customFields.FindIndex(candidate => string.Equals(candidate.Key, normalized.Key, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _customFields[index] = normalized;
        else _customFields.Add(normalized);
    }

    public bool RemoveCustomField(string key)
    {
        var index = _customFields.FindIndex(field => string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        _customFields.RemoveAt(index);
        return true;
    }

    public void ReplaceCollections(IEnumerable<ProjectCollection> collections)
    {
        _collections.Clear();
        _collections.AddRange(collections);
    }

    public void UpsertCollection(ProjectCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var index = _collections.FindIndex(candidate => string.Equals(candidate.Id, collection.Id, StringComparison.Ordinal));
        if (index >= 0) _collections[index] = collection;
        else _collections.Add(collection);
    }

    public bool RemoveCollection(string id)
    {
        var index = _collections.FindIndex(collection => string.Equals(collection.Id, id, StringComparison.Ordinal));
        if (index < 0) return false;
        _collections.RemoveAt(index);
        return true;
    }

    public static string NormalizeKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var chars = value.Trim().ToLowerInvariant()
            .Select(static ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var key = new string(chars).Trim('-');
        while (key.Contains("--", StringComparison.Ordinal)) key = key.Replace("--", "-", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(key) ? "field" : key;
    }
}

public sealed record CustomMetadataDefinition(string Key, string Name);

public enum ProjectCollectionKind
{
    Manual,
    Search
}

public sealed record ProjectCollection(
    string Id,
    string Name,
    ProjectCollectionKind Kind,
    string Query,
    bool MatchCase,
    bool UseRegex,
    bool WholeWord,
    IReadOnlyList<string> NodePersistentIds)
{
    public static ProjectCollection SavedSearch(
        string name,
        string query,
        bool matchCase,
        bool useRegex,
        bool wholeWord)
        => new(
            Guid.NewGuid().ToString("N"),
            name.Trim(),
            ProjectCollectionKind.Search,
            query.Trim(),
            matchCase,
            useRegex,
            wholeWord,
            []);

    public static ProjectCollection Manual(string name, IEnumerable<string> nodePersistentIds)
        => new(
            Guid.NewGuid().ToString("N"),
            name.Trim(),
            ProjectCollectionKind.Manual,
            string.Empty,
            false,
            false,
            false,
            nodePersistentIds.Distinct(StringComparer.Ordinal).ToArray());
}

public sealed record DocumentComment(
    string Id,
    int Line,
    string Text,
    bool Resolved,
    DateTimeOffset CreatedAt)
{
    public static DocumentComment Create(int line, string text)
        => new(Guid.NewGuid().ToString("N"), Math.Max(1, line), text.Trim(), false, DateTimeOffset.UtcNow);
}

public sealed record ProjectTemplateInfo(string Id, string Name);
