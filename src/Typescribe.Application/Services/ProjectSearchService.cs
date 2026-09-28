using Typescribe.Application.Abstractions;
using Typescribe.Application.Models;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

public sealed class ProjectSearchService(IProjectRepository repository)
{
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(BookProject project, string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var results = new List<SearchHit>();
        await foreach (var (node, content) in repository.EnumerateDocumentsAsync(project, cancellationToken))
        {
            var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                results.Add(new SearchHit(node.Id, node.Title, node.RelativePath ?? string.Empty, i + 1, lines[i].Trim()));
                if (results.Count >= 200) return results;
            }
        }
        return results;
    }
}
