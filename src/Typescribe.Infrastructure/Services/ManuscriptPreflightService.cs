using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

/// <summary>Deterministic project health checks run before final publishing.</summary>
public sealed class ManuscriptPreflightService(
    IProjectRepository repository,
    IDocumentParser parser,
    BibTeXDatabase bibliography,
    AssetManagerService assets)
{
    public async Task<PreflightReport> RunAsync(BookProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var issues = new List<PreflightIssue>();
        var documents = new List<DocumentState>();
        await LoadDocumentsAsync(project.Root, parentIncluded: true, project, documents, cancellationToken);

        var bibliographyEntries = await bibliography.LoadAsync(project.RootPath, cancellationToken);
        var citationKeys = bibliographyEntries.Select(static entry => entry.CitationKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var identifiers = new Dictionary<string, List<IdentifierLocation>>(StringComparer.OrdinalIgnoreCase);

        foreach (var document in documents)
        {
            foreach (var location in EnumerateIdentifiers(document))
            {
                if (!identifiers.TryGetValue(location.Identifier, out var list)) identifiers[location.Identifier] = list = [];
                list.Add(location);
                if (!IsValidIdentifier(location.Identifier))
                    issues.Add(Issue(PreflightSeverity.Error, "identifier.invalid", $"Invalid identifier: {location.Identifier}", document.Node, location.Line));
            }
        }

        foreach (var pair in identifiers.Where(static pair => pair.Value.Count > 1))
        {
            foreach (var duplicate in pair.Value)
                issues.Add(Issue(PreflightSeverity.Error, "identifier.duplicate", $"Duplicate identifier: {pair.Key}", duplicate.Document.Node, duplicate.Line));
        }

        var figuresResolved = 0;
        var citationsResolved = 0;
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document.Included && string.IsNullOrWhiteSpace(document.Content))
                issues.Add(Issue(PreflightSeverity.Warning, "chapter.empty", "Included chapter/document is empty.", document.Node, 1));

            CheckHeadingHierarchy(document, issues);
            var footnoteDefinitions = document.Ast.Blocks.OfType<FootnoteDefinitionBlock>()
                .Select(static definition => definition.Identifier)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var block in document.Ast.Blocks)
            {
                if (block is FigureBlock figure)
                {
                    var exists = TryResolveProjectPath(project.RootPath, figure.Source, out var figurePath) && File.Exists(figurePath);
                    if (exists) figuresResolved++;
                    else issues.Add(Issue(PreflightSeverity.Error, "image.missing", $"Missing image: {figure.Source}", document.Node, figure.SourceLine));
                }

                foreach (var inline in EnumerateInlines(block))
                {
                    switch (inline)
                    {
                        case CitationInline citation:
                            if (citationKeys.Contains(citation.Key)) citationsResolved++;
                            else issues.Add(Issue(PreflightSeverity.Error, "citation.unknown", $"Missing citation: {citation.Key}", document.Node, block.SourceLine));
                            break;
                        case CrossReferenceInline reference:
                            if (!identifiers.TryGetValue(reference.Identifier, out var targets) || targets.Count == 0)
                            {
                                issues.Add(Issue(PreflightSeverity.Error, "reference.broken", $"Unknown reference: {reference.Identifier}", document.Node, block.SourceLine));
                            }
                            else if (document.Included && targets.All(static target => !target.Document.Included))
                            {
                                issues.Add(Issue(PreflightSeverity.Warning, "reference.excluded", $"Reference points only to excluded content: {reference.Identifier}", document.Node, block.SourceLine));
                            }
                            break;
                        case FootnoteReferenceInline footnote when !footnoteDefinitions.Contains(footnote.Identifier):
                            issues.Add(Issue(PreflightSeverity.Error, "footnote.unresolved", $"Unresolved footnote: {footnote.Identifier}", document.Node, block.SourceLine));
                            break;
                        case LinkInline link when IsBrokenLocalLink(project.RootPath, link.Url):
                            issues.Add(Issue(PreflightSeverity.Error, "link.broken", $"Broken link: {link.Url}", document.Node, block.SourceLine));
                            break;
                    }
                }
            }
        }

        var projectAssets = await assets.ListAsync(project, cancellationToken);
        var unusedAssets = projectAssets.Count(static asset => asset.IsUnused);
        foreach (var asset in projectAssets.Where(static asset => asset.IsUnused))
            issues.Add(new PreflightIssue(PreflightSeverity.Warning, "asset.unused", $"Unused asset: {asset.RelativePath}"));
        foreach (var asset in projectAssets.Where(static asset => asset.Missing))
        {
            if (!issues.Any(issue => issue.Code == "image.missing" && issue.Message.EndsWith(asset.RelativePath, StringComparison.OrdinalIgnoreCase)))
                issues.Add(new PreflightIssue(PreflightSeverity.Error, "image.missing", $"Missing image: {asset.RelativePath}"));
        }

        return new PreflightReport(
            figuresResolved,
            citationsResolved,
            unusedAssets,
            issues
                .OrderByDescending(static issue => issue.Severity)
                .ThenBy(static issue => issue.DocumentTitle, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static issue => issue.Line)
                .ThenBy(static issue => issue.Code, StringComparer.Ordinal)
                .ToArray());
    }

    public static bool IsValidIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || !char.IsLetter(identifier[0])) return false;
        return identifier.All(static character => char.IsLetterOrDigit(character) || character is '_' or '-' or ':' or '.');
    }

    private async Task LoadDocumentsAsync(
        ProjectNode node,
        bool parentIncluded,
        BookProject project,
        List<DocumentState> result,
        CancellationToken cancellationToken)
    {
        var included = parentIncluded && node.IncludeInCompilation;
        if (node.IsDocument)
        {
            var content = await repository.ReadDocumentAsync(project, node, cancellationToken);
            result.Add(new DocumentState(node, content, parser.Parse(content), included));
        }
        foreach (var child in node.Children)
            await LoadDocumentsAsync(child, included, project, result, cancellationToken);
    }

    private static IEnumerable<IdentifierLocation> EnumerateIdentifiers(DocumentState document)
    {
        foreach (var block in document.Ast.Blocks)
        {
            var identifier = block switch
            {
                HeadingBlock heading => heading.Identifier,
                FigureBlock figure => figure.Identifier,
                TableBlock table => table.Identifier,
                DisplayMathBlock equation => equation.Identifier,
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(identifier))
                yield return new IdentifierLocation(identifier!, document, block.SourceLine);
        }
    }

    private static IEnumerable<AstInline> EnumerateInlines(AstBlock block)
    {
        IEnumerable<AstInline> roots = block switch
        {
            HeadingBlock heading => heading.Inlines,
            ParagraphBlock paragraph => paragraph.Inlines,
            QuoteBlock quote => quote.Inlines,
            ListItemBlock item => item.Inlines,
            FootnoteDefinitionBlock footnote => footnote.Inlines,
            TableBlock table => table.Header.SelectMany(static cell => cell.Inlines)
                .Concat(table.Rows.SelectMany(static row => row.SelectMany(static cell => cell.Inlines))),
            _ => []
        };
        foreach (var inline in roots)
        {
            yield return inline;
            foreach (var child in EnumerateChildren(inline)) yield return child;
        }
    }

    private static IEnumerable<AstInline> EnumerateChildren(AstInline inline)
    {
        IEnumerable<AstInline> children = inline switch
        {
            StrongInline strong => strong.Children,
            EmphasisInline emphasis => emphasis.Children,
            LinkInline link => link.Label,
            _ => []
        };
        foreach (var child in children)
        {
            yield return child;
            foreach (var descendant in EnumerateChildren(child)) yield return descendant;
        }
    }

    private static void CheckHeadingHierarchy(DocumentState document, ICollection<PreflightIssue> issues)
    {
        var previous = 0;
        foreach (var heading in document.Ast.Blocks.OfType<HeadingBlock>())
        {
            if (previous > 0 && heading.Level > previous + 1)
                issues.Add(Issue(PreflightSeverity.Warning, "heading.jump", $"Heading level jumps H{previous} → H{heading.Level}", document.Node, heading.SourceLine));
            previous = heading.Level;
        }
    }

    private static bool IsBrokenLocalLink(string root, string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith('#')) return false;
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https" or "mailto") return false;
        var pathPart = url.Split('#', 2)[0];
        if (string.IsNullOrWhiteSpace(pathPart)) return false;
        return !TryResolveProjectPath(root, pathPart, out var path) || !File.Exists(path);
    }

    private static bool TryResolveProjectPath(string root, string relative, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return false;
        try
        {
            var projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var candidate = Path.GetFullPath(Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            var rel = Path.GetRelativePath(projectRoot, candidate);
            if (Path.IsPathRooted(rel) || rel.Equals("..", StringComparison.Ordinal) || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return false;
            path = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static PreflightIssue Issue(PreflightSeverity severity, string code, string message, ProjectNode node, int line)
        => new(severity, code, message, node.PersistentId, node.Title, Math.Max(1, line));

    private sealed record DocumentState(ProjectNode Node, string Content, DocumentAst Ast, bool Included);
    private sealed record IdentifierLocation(string Identifier, DocumentState Document, int Line);
}
