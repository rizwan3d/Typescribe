using System.Globalization;
using System.Text;
using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

public sealed class FileSystemProjectRepository : IProjectRepository
{
    private const string ManifestName = "typescribe.yaml";
    private const string ManuscriptFolder = "manuscript";

    private readonly BinderMetadataStore _binderStore = new();
    private readonly BookStyleStore _styleStore = new();

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

        var manifest = $"project:\n  title: {QuoteYaml(title.Trim())}\n  author: \"\"\n  language: en\ncompiler:\n  engine: lualatex\noutput:\n  trim: 6x9\n";
        await AtomicFileWriter.WriteTextAsync(Path.Combine(rootPath, ManifestName), manifest, cancellationToken);
        await _styleStore.SaveAsync(rootPath, BookStyle.Default, cancellationToken);

        var first = Path.Combine(rootPath, ManuscriptFolder, "chapter-01.md");
        if (!File.Exists(first))
            await AtomicFileWriter.WriteTextAsync(first, $"# {title.Trim()}\n\nStart writing here.\n", cancellationToken);

        var project = await OpenAsync(rootPath, cancellationToken);
        await _binderStore.SaveAsync(project, cancellationToken);
        return project;
    }

    public Task<BookProject> OpenAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var manifestPath = Path.Combine(fullRoot, ManifestName);
        if (!File.Exists(manifestPath)) throw new FileNotFoundException($"No {ManifestName} found.", manifestPath);

        var metadata = ParseManifest(File.ReadAllLines(manifestPath));
        var binderMetadata = _binderStore.Load(fullRoot);
        var root = new ProjectNode("root", metadata.Title, NodeKind.Book);
        var manuscriptPath = Path.Combine(fullRoot, ManuscriptFolder);
        Directory.CreateDirectory(manuscriptPath);
        PopulateTree(root, fullRoot, manuscriptPath, binderMetadata);

        return Task.FromResult(new BookProject
        {
            RootPath = fullRoot,
            Title = metadata.Title,
            Author = metadata.Author,
            Language = metadata.Language,
            Style = _styleStore.Load(fullRoot),
            Root = root
        });
    }

    public Task<string> ReadDocumentAsync(BookProject project, ProjectNode node, CancellationToken cancellationToken = default)
    {
        if (!node.IsDocument) throw new InvalidOperationException("The selected binder item is not an editable document.");
        var path = ResolveNodePath(project, node);
        return File.ReadAllTextAsync(path, cancellationToken);
    }

    public Task SaveDocumentAsync(BookProject project, ProjectNode node, string content, CancellationToken cancellationToken = default)
    {
        if (!node.IsDocument) throw new InvalidOperationException("The selected binder item is not an editable document.");
        var path = ResolveNodePath(project, node);
        return AtomicFileWriter.WriteTextAsync(path, content ?? string.Empty, cancellationToken);
    }

    public Task<ProjectNode> AddChapterAsync(BookProject project, string title, CancellationToken cancellationToken = default)
        => AddNodeAsync(project, project.Root, NodeKind.Chapter, title, cancellationToken);

    public async Task<ProjectNode> AddNodeAsync(
        BookProject project,
        ProjectNode? parent,
        NodeKind kind,
        string title,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        cancellationToken.ThrowIfCancellationRequested();

        parent ??= project.Root;
        if (!parent.IsContainer)
            parent = FindParent(project.Root, parent) ?? project.Root;
        if (!parent.IsContainer) throw new InvalidOperationException("New binder items can only be added inside a book, part, or folder.");

        var containerPath = parent == project.Root
            ? Path.Combine(project.RootPath, ManuscriptFolder)
            : ResolveNodePath(project, parent);

        var safe = Slugify(title);
        var isContainer = kind is NodeKind.Part or NodeKind.Folder;
        if (kind == NodeKind.Book) throw new InvalidOperationException("A book cannot be nested inside another book.");

        var candidate = isContainer
            ? CreateUniquePath(containerPath, safe, extension: null)
            : CreateUniquePath(containerPath, safe, ".md");

        if (isContainer)
        {
            Directory.CreateDirectory(candidate);
        }
        else
        {
            await AtomicFileWriter.WriteTextAsync(candidate, $"# {title.Trim()}\n\n", cancellationToken);
        }

        var relative = BinderMetadataStore.Normalize(Path.GetRelativePath(project.RootPath, candidate));
        var node = new ProjectNode(relative, title.Trim(), kind, relative);
        parent.AddChild(node);
        await _binderStore.SaveAsync(project, cancellationToken);
        return node;
    }

    public async Task RenameNodeAsync(
        BookProject project,
        ProjectNode node,
        string newTitle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(newTitle);
        if (ReferenceEquals(node, project.Root)) throw new InvalidOperationException("The book root cannot be renamed from the binder.");

        var source = ResolveNodePath(project, node);
        var parentDirectory = Path.GetDirectoryName(source) ?? throw new InvalidOperationException("The binder item has no parent directory.");
        var slug = Slugify(newTitle);
        var extension = node.IsContainer ? null : Path.GetExtension(source);
        var desired = extension is null ? Path.Combine(parentDirectory, slug) : Path.Combine(parentDirectory, slug + extension);
        var target = PathsEqual(source, desired) ? source : CreateUniquePath(parentDirectory, slug, extension);

        var oldRelative = node.RelativePath ?? throw new InvalidOperationException("The binder item has no project path.");
        if (!PathsEqual(source, target))
        {
            if (node.IsContainer) Directory.Move(source, target);
            else File.Move(source, target);
        }

        var newRelative = BinderMetadataStore.Normalize(Path.GetRelativePath(project.RootPath, target));
        RepathSubtree(node, BinderMetadataStore.Normalize(oldRelative), newRelative);
        node.Rename(newTitle);

        if (node.IsDocument)
            await RewritePrimaryHeadingAsync(target, newTitle.Trim(), cancellationToken);

        await _binderStore.SaveAsync(project, cancellationToken);
    }

    public async Task DeleteNodeAsync(BookProject project, ProjectNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(node);
        if (ReferenceEquals(node, project.Root)) throw new InvalidOperationException("The book root cannot be deleted.");

        var parent = FindParent(project.Root, node) ?? throw new InvalidOperationException("The binder item is detached from the project tree.");
        var path = ResolveNodePath(project, node);
        cancellationToken.ThrowIfCancellationRequested();

        if (node.IsContainer)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }

        parent.RemoveChild(node);
        await _binderStore.SaveAsync(project, cancellationToken);
    }

    public async Task<bool> MoveNodeAsync(
        BookProject project,
        ProjectNode node,
        int offset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(node);
        if (offset == 0 || ReferenceEquals(node, project.Root)) return false;
        var parent = FindParent(project.Root, node);
        if (parent is null || !parent.MoveChild(node, offset)) return false;
        await _binderStore.SaveAsync(project, cancellationToken);
        return true;
    }

    public async Task SetCompilationIncludedAsync(
        BookProject project,
        ProjectNode node,
        bool included,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(node);
        node.IncludeInCompilation = included;
        await _binderStore.SaveAsync(project, cancellationToken);
    }

    public async Task SaveStyleAsync(BookProject project, BookStyle style, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(style);
        style.Validate();
        await _styleStore.SaveAsync(project.RootPath, style, cancellationToken);
        project.Style = style;
    }

    public async IAsyncEnumerable<(ProjectNode Node, string Content)> EnumerateDocumentsAsync(
        BookProject project,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stack = new Stack<(ProjectNode Node, bool ParentIncluded)>();
        for (var index = project.Root.Children.Count - 1; index >= 0; index--)
            stack.Push((project.Root.Children[index], true));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (node, parentIncluded) = stack.Pop();
            var included = parentIncluded && node.IncludeInCompilation;

            if (node.IsDocument && included)
                yield return (node, await ReadDocumentAsync(project, node, cancellationToken));

            for (var index = node.Children.Count - 1; index >= 0; index--)
                stack.Push((node.Children[index], included));
        }
    }

    private static string ResolveNodePath(BookProject project, ProjectNode node)
    {
        if (node.RelativePath is null) throw new InvalidOperationException("The selected binder item has no project path.");

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

    private static ProjectNode? FindParent(ProjectNode root, ProjectNode target)
    {
        foreach (var child in root.Children)
        {
            if (ReferenceEquals(child, target)) return root;
            var parent = FindParent(child, target);
            if (parent is not null) return parent;
        }
        return null;
    }

    private static void PopulateTree(
        ProjectNode parent,
        string projectRoot,
        string directory,
        IReadOnlyDictionary<string, BinderMetadata> metadata)
    {
        var pending = new List<(ProjectNode Node, string? Directory, int Order)>();

        foreach (var subdirectory in Directory.EnumerateDirectories(directory))
        {
            var relative = BinderMetadataStore.Normalize(Path.GetRelativePath(projectRoot, subdirectory));
            metadata.TryGetValue(relative, out var entry);
            var kind = entry?.Kind is NodeKind.Part or NodeKind.Folder ? entry.Kind : NodeKind.Folder;
            var title = !string.IsNullOrWhiteSpace(entry?.Title) ? entry.Title : ToTitle(Path.GetFileName(subdirectory));
            var folder = new ProjectNode(relative, title, kind, relative)
            {
                IncludeInCompilation = entry?.Included ?? true
            };
            pending.Add((folder, subdirectory, entry?.Order ?? int.MaxValue));
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.md"))
        {
            var relative = BinderMetadataStore.Normalize(Path.GetRelativePath(projectRoot, file));
            metadata.TryGetValue(relative, out var entry);
            var kind = entry?.Kind is NodeKind.Chapter or NodeKind.Section or NodeKind.Scene or NodeKind.Note or NodeKind.Research
                ? entry.Kind
                : NodeKind.Chapter;
            var title = !string.IsNullOrWhiteSpace(entry?.Title)
                ? entry.Title
                : ReadTitle(file) ?? ToTitle(Path.GetFileNameWithoutExtension(file));
            var node = new ProjectNode(relative, title, kind, relative)
            {
                IncludeInCompilation = entry?.Included ?? true
            };
            pending.Add((node, null, entry?.Order ?? int.MaxValue));
        }

        foreach (var item in pending
                     .OrderBy(static item => item.Order)
                     .ThenBy(static item => item.Node.Title, StringComparer.OrdinalIgnoreCase))
        {
            parent.AddChild(item.Node);
            if (item.Directory is not null)
                PopulateTree(item.Node, projectRoot, item.Directory, metadata);
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

    private static async Task RewritePrimaryHeadingAsync(string file, string title, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(file, cancellationToken);
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        var headingIndex = lines.FindIndex(static line => line.StartsWith("# ", StringComparison.Ordinal));
        if (headingIndex >= 0) lines[headingIndex] = $"# {title}";
        else
        {
            lines.Insert(0, string.Empty);
            lines.Insert(0, $"# {title}");
        }
        await AtomicFileWriter.WriteTextAsync(file, string.Join(Environment.NewLine, lines), cancellationToken);
    }

    private static void RepathSubtree(ProjectNode node, string oldPrefix, string newPrefix)
    {
        if (node.RelativePath is { } relative)
        {
            var normalized = BinderMetadataStore.Normalize(relative);
            string replacement;
            if (PathEquals(normalized, oldPrefix))
            {
                replacement = newPrefix;
            }
            else if (StartsWithPath(normalized, oldPrefix))
            {
                replacement = newPrefix + normalized[oldPrefix.Length..];
            }
            else
            {
                replacement = normalized;
            }
            node.Repath(replacement, replacement);
        }

        foreach (var child in node.Children) RepathSubtree(child, oldPrefix, newPrefix);
    }

    private static string CreateUniquePath(string directory, string baseName, string? extension)
    {
        var suffix = 1;
        while (true)
        {
            var name = suffix == 1 ? baseName : $"{baseName}-{suffix.ToString(CultureInfo.InvariantCulture)}";
            if (!string.IsNullOrEmpty(extension)) name += extension;
            var candidate = Path.Combine(directory, name);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            suffix++;
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), PathComparison());

    private static bool PathEquals(string left, string right)
        => string.Equals(left, right, PathComparison());

    private static bool StartsWithPath(string path, string prefix)
    {
        if (!path.StartsWith(prefix, PathComparison())) return false;
        if (path.Length == prefix.Length) return true;
        return path[prefix.Length] == '/';
    }

    private static StringComparison PathComparison()
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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
        return builder.ToString().Trim('-') is { Length: > 0 } slug ? slug : $"item-{DateTime.UtcNow:yyyyMMddHHmmss}";
    }
}
