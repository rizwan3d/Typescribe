using System.Globalization;
using System.Text;
using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

public sealed class FileSystemProjectRepository : IProjectRepository
{
    private const string ManifestName = "typescribe.yaml";
    private const string ManuscriptFolder = "manuscript";

    public async Task<BookProject> CreateAsync(string rootPath, string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Directory.CreateDirectory(rootPath);
        Directory.CreateDirectory(Path.Combine(rootPath, ManuscriptFolder));
        Directory.CreateDirectory(Path.Combine(rootPath, "research"));
        Directory.CreateDirectory(Path.Combine(rootPath, "assets"));
        Directory.CreateDirectory(Path.Combine(rootPath, "styles"));
        Directory.CreateDirectory(Path.Combine(rootPath, "build"));

        var manifest = $"project:\n  title: {QuoteYaml(title.Trim())}\n  author: \"\"\n  language: en\ncompiler:\n  engine: typst\noutput:\n  trim: 6x9\n";
        await AtomicFileWriter.WriteTextAsync(Path.Combine(rootPath, ManifestName), manifest, cancellationToken);

        var first = Path.Combine(rootPath, ManuscriptFolder, "chapter-01.md");
        if (!File.Exists(first))
            await AtomicFileWriter.WriteTextAsync(first, $"# {title.Trim()}\n\nStart writing here.\n", cancellationToken);

        return await OpenAsync(rootPath, cancellationToken);
    }

    public Task<BookProject> OpenAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manifestPath = Path.Combine(rootPath, ManifestName);
        if (!File.Exists(manifestPath)) throw new FileNotFoundException($"No {ManifestName} found.", manifestPath);
        var metadata = ParseManifest(File.ReadAllLines(manifestPath));
        var root = new ProjectNode("root", metadata.Title, NodeKind.Book);
        var manuscriptPath = Path.Combine(rootPath, ManuscriptFolder);
        Directory.CreateDirectory(manuscriptPath);
        PopulateTree(root, rootPath, manuscriptPath);
        return Task.FromResult(new BookProject
        {
            RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)),
            Title = metadata.Title,
            Author = metadata.Author,
            Language = metadata.Language,
            Root = root
        });
    }

    public Task<string> ReadDocumentAsync(BookProject project, ProjectNode node, CancellationToken cancellationToken = default)
    {
        var path = ResolveDocumentPath(project, node);
        return File.ReadAllTextAsync(path, cancellationToken);
    }

    public Task SaveDocumentAsync(BookProject project, ProjectNode node, string content, CancellationToken cancellationToken = default)
    {
        var path = ResolveDocumentPath(project, node);
        return AtomicFileWriter.WriteTextAsync(path, content ?? string.Empty, cancellationToken);
    }

    public async Task<ProjectNode> AddChapterAsync(BookProject project, string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var folder = Path.Combine(project.RootPath, ManuscriptFolder);
        var safe = Slugify(title);
        var filename = safe + ".md";
        var candidate = Path.Combine(folder, filename);
        var suffix = 2;
        while (File.Exists(candidate))
        {
            filename = $"{safe}-{suffix.ToString(CultureInfo.InvariantCulture)}.md";
            candidate = Path.Combine(folder, filename);
            suffix++;
        }
        await AtomicFileWriter.WriteTextAsync(candidate, $"# {title.Trim()}\n\n", cancellationToken);
        var relative = Path.GetRelativePath(project.RootPath, candidate).Replace('\\', '/');
        var node = new ProjectNode(relative, title.Trim(), NodeKind.Chapter, relative);
        project.Root.AddChild(node);
        return node;
    }

    public async IAsyncEnumerable<(ProjectNode Node, string Content)> EnumerateDocumentsAsync(BookProject project, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var node in Flatten(project.Root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!node.IsDocument || !node.IncludeInCompilation) continue;
            yield return (node, await ReadDocumentAsync(project, node, cancellationToken));
        }
    }

    private static string ResolveDocumentPath(BookProject project, ProjectNode node)
    {
        if (node.RelativePath is null) throw new InvalidOperationException("The selected node is not a document.");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.RootPath));
        var candidate = Path.GetFullPath(Path.Combine(root, node.RelativePath));
        var relativeToRoot = Path.GetRelativePath(root, candidate);

        if (Path.IsPathRooted(relativeToRoot) || IsParentPath(relativeToRoot))
            throw new InvalidOperationException("Project path traversal was rejected.");

        return candidate;
    }

    private static bool IsParentPath(string relativePath)
    {
        if (relativePath.Equals("..", StringComparison.Ordinal)) return true;
        if (relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) return true;
        return Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar
            && relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static IEnumerable<ProjectNode> Flatten(ProjectNode root)
    {
        foreach (var child in root.Children)
        {
            yield return child;
            foreach (var descendant in Flatten(child)) yield return descendant;
        }
    }

    private static void PopulateTree(ProjectNode parent, string projectRoot, string directory)
    {
        foreach (var subdirectory in Directory.EnumerateDirectories(directory).Order(StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(projectRoot, subdirectory).Replace('\\', '/');
            var folder = new ProjectNode(relative, ToTitle(Path.GetFileName(subdirectory)), NodeKind.Folder);
            parent.AddChild(folder);
            PopulateTree(folder, projectRoot, subdirectory);
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.md").Order(StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
            var title = ReadTitle(file) ?? ToTitle(Path.GetFileNameWithoutExtension(file));
            parent.AddChild(new ProjectNode(relative, title, NodeKind.Chapter, relative));
        }
    }

    private static string? ReadTitle(string file)
    {
        using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        for (var i = 0; i < 10; i++)
        {
            var line = reader.ReadLine();
            if (line is null) break;
            if (line.StartsWith("# ", StringComparison.Ordinal)) return line[2..].Trim();
        }
        return null;
    }

    private static (string Title, string Author, string Language) ParseManifest(IEnumerable<string> lines)
    {
        var title = "Untitled Project";
        var author = string.Empty;
        var language = "en";
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("title:", StringComparison.Ordinal)) title = UnquoteYaml(line[6..].Trim());
            else if (line.StartsWith("author:", StringComparison.Ordinal)) author = UnquoteYaml(line[7..].Trim());
            else if (line.StartsWith("language:", StringComparison.Ordinal)) language = UnquoteYaml(line[9..].Trim());
        }
        return (title, author, language);
    }

    private static string QuoteYaml(string value) => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    private static string UnquoteYaml(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal) : value;
    private static string ToTitle(string value) => string.Join(' ', value.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries).Select(static s => char.ToUpperInvariant(s[0]) + s[1..]));

    private static string Slugify(string value)
    {
        var builder = new StringBuilder();
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(ch);
            else if ((char.IsWhiteSpace(ch) || ch is '-' or '_') && builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        return builder.ToString().Trim('-') is { Length: > 0 } slug ? slug : $"chapter-{DateTime.UtcNow:yyyyMMddHHmmss}";
    }
}
