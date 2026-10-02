using System.Text;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

/// <summary>Project-local BibTeX database used by citation insertion and publishing.</summary>
public sealed class BibTeXDatabase
{
    public const string ReferencesDirectory = "references";
    public const string BibliographyFileName = "bibliography.bib";

    public static string GetPath(string projectRoot)
        => Path.Combine(projectRoot, ReferencesDirectory, BibliographyFileName);

    public async Task<IReadOnlyList<BibliographyEntry>> LoadAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var path = GetPath(projectRoot);
        if (!File.Exists(path)) return [];
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        return Parse(text);
    }

    public async Task SaveAsync(string projectRoot, IEnumerable<BibliographyEntry> entries, CancellationToken cancellationToken = default)
    {
        var path = GetPath(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var normalized = entries
            .Select(static entry => entry.Normalize())
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.CitationKey))
            .GroupBy(static entry => entry.CitationKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.Last())
            .OrderBy(static entry => entry.CitationKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await AtomicFileWriter.WriteTextAsync(path, Serialize(normalized), cancellationToken);
    }

    public async Task UpsertAsync(string projectRoot, BibliographyEntry entry, CancellationToken cancellationToken = default)
    {
        var items = (await LoadAsync(projectRoot, cancellationToken)).ToList();
        var normalized = entry.Normalize();
        var index = items.FindIndex(candidate => string.Equals(candidate.CitationKey, normalized.CitationKey, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) items[index] = normalized;
        else items.Add(normalized);
        await SaveAsync(projectRoot, items, cancellationToken);
    }

    public async Task DeleteAsync(string projectRoot, string citationKey, CancellationToken cancellationToken = default)
    {
        var items = (await LoadAsync(projectRoot, cancellationToken))
            .Where(entry => !string.Equals(entry.CitationKey, citationKey, StringComparison.OrdinalIgnoreCase));
        await SaveAsync(projectRoot, items, cancellationToken);
    }

    public async Task ImportAsync(string projectRoot, string sourcePath, CancellationToken cancellationToken = default)
    {
        var incoming = Parse(await File.ReadAllTextAsync(sourcePath, cancellationToken));
        var existing = (await LoadAsync(projectRoot, cancellationToken)).ToDictionary(static item => item.CitationKey, StringComparer.OrdinalIgnoreCase);
        foreach (var item in incoming) existing[item.CitationKey] = item;
        await SaveAsync(projectRoot, existing.Values, cancellationToken);
    }

    public async Task ExportAsync(string projectRoot, string destination, CancellationToken cancellationToken = default)
    {
        var entries = await LoadAsync(projectRoot, cancellationToken);
        await AtomicFileWriter.WriteTextAsync(destination, Serialize(entries), cancellationToken);
    }

    public static IReadOnlyList<BibliographyEntry> Parse(string text)
    {
        text ??= string.Empty;
        var result = new List<BibliographyEntry>();
        var cursor = 0;
        while (cursor < text.Length)
        {
            var at = text.IndexOf('@', cursor);
            if (at < 0) break;
            var open = text.IndexOf('{', at + 1);
            if (open < 0) break;
            var comma = text.IndexOf(',', open + 1);
            if (comma < 0) break;
            var key = text[(open + 1)..comma].Trim();
            if (key.Length == 0)
            {
                cursor = comma + 1;
                continue;
            }

            var depth = 1;
            var end = comma + 1;
            var quoted = false;
            for (; end < text.Length; end++)
            {
                var ch = text[end];
                if (ch == '"' && (end == 0 || text[end - 1] != '\\')) quoted = !quoted;
                if (quoted) continue;
                if (ch == '{') depth++;
                else if (ch == '}' && --depth == 0) break;
            }
            if (end >= text.Length) break;

            var fields = ParseFields(text[(comma + 1)..end]);
            result.Add(new BibliographyEntry(
                key,
                Get(fields, "author"),
                Get(fields, "title"),
                Get(fields, "year"),
                Get(fields, "publisher"),
                Get(fields, "doi"),
                Get(fields, "url"),
                Get(fields, "journal"),
                Get(fields, "pages")).Normalize());
            cursor = end + 1;
        }
        return result;
    }

    public static string Serialize(IEnumerable<BibliographyEntry> entries)
    {
        var output = new StringBuilder();
        foreach (var entry in entries)
        {
            var item = entry.Normalize();
            if (item.CitationKey.Length == 0) continue;
            output.Append("@article{").Append(SanitizeKey(item.CitationKey)).AppendLine(",");
            AppendField(output, "author", item.Author);
            AppendField(output, "title", item.Title);
            AppendField(output, "year", item.Year);
            AppendField(output, "publisher", item.Publisher);
            AppendField(output, "doi", item.Doi);
            AppendField(output, "url", item.Url);
            AppendField(output, "journal", item.Journal);
            AppendField(output, "pages", item.Pages);
            output.AppendLine("}").AppendLine();
        }
        return output.ToString();
    }

    private static Dictionary<string, string> ParseFields(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cursor = 0;
        while (cursor < body.Length)
        {
            while (cursor < body.Length && (char.IsWhiteSpace(body[cursor]) || body[cursor] == ',')) cursor++;
            var equals = body.IndexOf('=', cursor);
            if (equals < 0) break;
            var name = body[cursor..equals].Trim();
            cursor = equals + 1;
            while (cursor < body.Length && char.IsWhiteSpace(body[cursor])) cursor++;
            if (cursor >= body.Length) break;

            string value;
            if (body[cursor] == '{')
            {
                var start = ++cursor;
                var depth = 1;
                while (cursor < body.Length && depth > 0)
                {
                    if (body[cursor] == '{') depth++;
                    else if (body[cursor] == '}') depth--;
                    cursor++;
                }
                value = body[start..Math.Max(start, cursor - 1)].Trim();
            }
            else if (body[cursor] == '"')
            {
                var start = ++cursor;
                while (cursor < body.Length && !(body[cursor] == '"' && body[cursor - 1] != '\\')) cursor++;
                value = body[start..Math.Min(cursor, body.Length)].Trim();
                if (cursor < body.Length) cursor++;
            }
            else
            {
                var end = body.IndexOf(',', cursor);
                if (end < 0) end = body.Length;
                value = body[cursor..end].Trim();
                cursor = end;
            }
            if (name.Length > 0) result[name] = value;
        }
        return result;
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) ? value : string.Empty;

    private static void AppendField(StringBuilder output, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        output.Append("  ").Append(name).Append(" = {").Append(EscapeValue(value.Trim())).AppendLine("},");
    }

    private static string EscapeValue(string value)
        => value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string SanitizeKey(string key)
        => new(key.Trim().Where(static ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or ':' or '.' or '/').ToArray());
}
