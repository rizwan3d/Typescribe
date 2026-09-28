using System.Text.RegularExpressions;
using Typescribe.Application.Abstractions;
using Typescribe.Application.Models;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

public sealed class ProjectSearchService(IProjectRepository repository)
{
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        BookProject project,
        string query,
        SearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        options ??= new SearchOptions();

        var matcher = CreateMatcher(query, options);
        var results = new List<SearchHit>();
        var maxResults = options.EffectiveMaxResults;

        await foreach (var (node, content) in repository.EnumerateDocumentsAsync(project, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (matcher(node.Title))
            {
                results.Add(new SearchHit(node.Id, node.Title, node.RelativePath ?? string.Empty, 0, $"Title: {node.Title}"));
                if (results.Count >= maxResults) return results;
            }

            var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!matcher(lines[i])) continue;

                results.Add(new SearchHit(
                    node.Id,
                    node.Title,
                    node.RelativePath ?? string.Empty,
                    i + 1,
                    lines[i].Trim()));

                if (results.Count >= maxResults) return results;
            }
        }
        return results;
    }

    private static Func<string, bool> CreateMatcher(string query, SearchOptions options)
    {
        if (!options.UseRegex && !options.WholeWord)
        {
            var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return value => value.Contains(query, comparison);
        }

        var pattern = options.UseRegex ? query : Regex.Escape(query);
        if (options.WholeWord) pattern = $"(?<!\\w)(?:{pattern})(?!\\w)";

        var regexOptions = RegexOptions.CultureInvariant;
        if (!options.MatchCase) regexOptions |= RegexOptions.IgnoreCase;

        Regex regex;
        try
        {
            regex = new Regex(pattern, regexOptions, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Invalid search expression: {ex.Message}", ex);
        }

        return value =>
        {
            try
            {
                return regex.IsMatch(value);
            }
            catch (RegexMatchTimeoutException ex)
            {
                throw new InvalidOperationException("The search expression took too long to evaluate. Try a simpler regular expression.", ex);
            }
        };
    }
}
