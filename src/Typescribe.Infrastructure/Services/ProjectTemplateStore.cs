using System.Text;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

internal sealed class ProjectTemplateStore
{
    private const string TemplateInfoFile = "template.info";

    public IReadOnlyList<ProjectTemplateInfo> List()
    {
        var root = GetTemplatesRoot();
        if (!Directory.Exists(root)) return [];
        var result = new List<ProjectTemplateInfo>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var id = Path.GetFileName(directory);
            var info = Path.Combine(directory, TemplateInfoFile);
            var name = File.Exists(info) ? File.ReadAllText(info, Encoding.UTF8).Trim() : id;
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                result.Add(new ProjectTemplateInfo(id, name));
        }
        return result.OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Task SaveAsync(BookProject project, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();

        var id = Slugify(name) + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var destination = Path.Combine(GetTemplatesRoot(), id);
        Directory.CreateDirectory(destination);
        CopyDirectory(project.RootPath, destination, cancellationToken, excludeRuntimeData: true);
        File.WriteAllText(Path.Combine(destination, TemplateInfoFile), name.Trim(), Encoding.UTF8);
        return Task.CompletedTask;
    }

    public Task MaterializeAsync(ProjectTemplateInfo template, string destination, string title, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        cancellationToken.ThrowIfCancellationRequested();

        var source = Path.Combine(GetTemplatesRoot(), template.Id);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Template '{template.Name}' was not found.");
        Directory.CreateDirectory(destination);
        CopyDirectory(source, destination, cancellationToken, excludeRuntimeData: false, skipTemplateInfo: true);
        RewriteProjectTitle(destination, title.Trim());
        return Task.CompletedTask;
    }

    private static void CopyDirectory(
        string source,
        string destination,
        CancellationToken cancellationToken,
        bool excludeRuntimeData,
        bool skipTemplateInfo = false)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, directory);
            if (ShouldSkip(relative, excludeRuntimeData)) continue;
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            if ((skipTemplateInfo && relative.Equals(TemplateInfoFile, StringComparison.OrdinalIgnoreCase)) || ShouldSkip(relative, excludeRuntimeData))
                continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static bool ShouldSkip(string relative, bool excludeRuntimeData)
    {
        if (!excludeRuntimeData) return false;
        var normalized = relative.Replace('\\', '/');
        if (normalized.Equals("build", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("build/", StringComparison.OrdinalIgnoreCase)) return true;
        if (normalized.Equals(".typescribe/snapshots", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith(".typescribe/snapshots/", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void RewriteProjectTitle(string projectRoot, string title)
    {
        var manifest = Path.Combine(projectRoot, "typescribe.yaml");
        if (!File.Exists(manifest)) return;
        var lines = File.ReadAllLines(manifest, Encoding.UTF8);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("title:", StringComparison.Ordinal)) continue;
            var indent = lines[i][..(lines[i].Length - trimmed.Length)];
            lines[i] = indent + "title: \"" + title.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
            break;
        }
        File.WriteAllLines(manifest, lines, Encoding.UTF8);
    }

    private static string GetTemplatesRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Typescribe", "templates");

    private static string Slugify(string value)
    {
        var builder = new StringBuilder();
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(ch);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        return builder.ToString().Trim('-') is { Length: > 0 } slug ? slug : "template";
    }
}
