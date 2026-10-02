using System.Buffers.Binary;
using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

/// <summary>Owns project-managed images under /assets and keeps manuscript references relative and portable.</summary>
public sealed class AssetManagerService(IProjectRepository repository, IDocumentParser parser)
{
    public const string AssetsDirectoryName = "assets";
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff"
    };

    public string EnsureAssetsDirectory(BookProject project)
    {
        var directory = Path.Combine(project.RootPath, AssetsDirectoryName);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public async Task<string> ImportAsync(BookProject project, string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Image file was not found.", sourcePath);
        var extension = Path.GetExtension(sourcePath);
        if (!SupportedExtensions.Contains(extension)) throw new InvalidOperationException($"Unsupported image format: {extension}");

        var directory = EnsureAssetsDirectory(project);
        var baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(sourcePath));
        if (baseName.Length == 0) baseName = "image";
        var candidate = Path.Combine(directory, baseName + extension.ToLowerInvariant());
        var number = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(directory, $"{baseName}-{number++}{extension.ToLowerInvariant()}");

        await CopyFileAsync(sourcePath, candidate, overwrite: false, cancellationToken);
        return ToProjectPath(project, candidate);
    }

    public async Task ReplaceAsync(BookProject project, string relativePath, string sourcePath, CancellationToken cancellationToken = default)
    {
        var destination = ResolveManagedPath(project, relativePath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Replacement image was not found.", sourcePath);
        await CopyFileAsync(sourcePath, destination, overwrite: true, cancellationToken);
    }

    public async Task<string> RenameAsync(BookProject project, string relativePath, string newFileName, CancellationToken cancellationToken = default)
    {
        var source = ResolveManagedPath(project, relativePath);
        if (!File.Exists(source)) throw new FileNotFoundException("Asset was not found.", source);
        var extension = Path.GetExtension(source);
        var requestedExtension = Path.GetExtension(newFileName);
        var stem = SanitizeFileName(Path.GetFileNameWithoutExtension(newFileName));
        if (stem.Length == 0) throw new InvalidOperationException("Asset name cannot be empty.");
        var finalExtension = string.IsNullOrWhiteSpace(requestedExtension) ? extension : requestedExtension;
        if (!SupportedExtensions.Contains(finalExtension)) throw new InvalidOperationException($"Unsupported image format: {finalExtension}");

        var destination = Path.Combine(EnsureAssetsDirectory(project), stem + finalExtension.ToLowerInvariant());
        if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) && File.Exists(destination))
            throw new IOException($"An asset named '{Path.GetFileName(destination)}' already exists.");

        File.Move(source, destination, overwrite: false);
        var newRelativePath = ToProjectPath(project, destination);
        try
        {
            await RewriteReferencesAsync(project, NormalizeRelativePath(relativePath), newRelativePath, cancellationToken);
        }
        catch
        {
            if (File.Exists(destination) && !File.Exists(source)) File.Move(destination, source);
            throw;
        }
        return newRelativePath;
    }

    public async Task<IReadOnlyList<ProjectAsset>> ListAsync(BookProject project, CancellationToken cancellationToken = default)
    {
        var usages = new Dictionary<string, List<AssetUsage>>(StringComparer.OrdinalIgnoreCase);
        await foreach (var (node, content) in repository.EnumerateDocumentsAsync(project, cancellationToken))
        {
            var ast = parser.Parse(content);
            foreach (var figure in ast.Blocks.OfType<FigureBlock>())
            {
                var relative = NormalizeRelativePath(figure.Source);
                if (!IsManagedPath(relative)) continue;
                if (!usages.TryGetValue(relative, out var locations)) usages[relative] = locations = [];
                locations.Add(new AssetUsage(node.PersistentId, node.Title, figure.SourceLine));
            }
        }

        var assets = new List<ProjectAsset>();
        var directory = EnsureAssetsDirectory(project);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .Where(path => SupportedExtensions.Contains(Path.GetExtension(path))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = ToProjectPath(project, file);
            var dimensions = ReadDimensions(file);
            usages.TryGetValue(relative, out var locations);
            assets.Add(new ProjectAsset(
                relative,
                Path.GetFileName(file),
                new FileInfo(file).Length,
                dimensions.Width,
                dimensions.Height,
                Missing: false,
                locations?.ToArray() ?? []));
        }

        foreach (var pair in usages)
        {
            if (assets.Any(asset => string.Equals(asset.RelativePath, pair.Key, StringComparison.OrdinalIgnoreCase))) continue;
            assets.Add(new ProjectAsset(pair.Key, Path.GetFileName(pair.Key), 0, null, null, Missing: true, pair.Value.ToArray()));
        }

        return assets.OrderBy(static asset => asset.Missing).ThenBy(static asset => asset.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public string ResolveManagedPath(BookProject project, string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        if (!IsManagedPath(normalized)) throw new InvalidOperationException("Only project-managed assets under /assets can be modified.");
        var root = Path.GetFullPath(project.RootPath);
        var path = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var assetsRoot = Path.GetFullPath(Path.Combine(root, AssetsDirectoryName)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Asset path escapes the project assets directory.");
        return path;
    }

    public static bool IsManagedPath(string source)
    {
        var normalized = NormalizeRelativePath(source);
        return normalized.StartsWith(AssetsDirectoryName + "/", StringComparison.OrdinalIgnoreCase) &&
               !Path.IsPathRooted(source) &&
               !normalized.Split('/').Contains("..", StringComparer.Ordinal);
    }

    public static string NormalizeRelativePath(string path)
        => (path ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');

    private async Task RewriteReferencesAsync(BookProject project, string oldPath, string newPath, CancellationToken cancellationToken)
    {
        var changes = new List<(ProjectNode Node, string Old, string New)>();
        await foreach (var (node, content) in repository.EnumerateDocumentsAsync(project, cancellationToken))
        {
            var updated = content
                .Replace("](" + oldPath + ")", "](" + newPath + ")", StringComparison.Ordinal)
                .Replace("](" + oldPath.Replace('/', '\\') + ")", "](" + newPath + ")", StringComparison.Ordinal);
            if (!string.Equals(content, updated, StringComparison.Ordinal)) changes.Add((node, content, updated));
        }

        var saved = new List<(ProjectNode Node, string Old)>();
        try
        {
            foreach (var change in changes)
            {
                await repository.SaveDocumentAsync(project, change.Node, change.New, cancellationToken);
                saved.Add((change.Node, change.Old));
            }
        }
        catch
        {
            foreach (var rollback in saved)
                await repository.SaveDocumentAsync(project, rollback.Node, rollback.Old, CancellationToken.None);
            throw;
        }
    }

    private static string ToProjectPath(BookProject project, string absolutePath)
        => NormalizeRelativePath(Path.GetRelativePath(project.RootPath, absolutePath));

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Trim().Where(ch => !invalid.Contains(ch) && ch is not '/' and not '\\').ToArray());
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var mode = overwrite ? FileMode.Create : FileMode.CreateNew;
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(destinationPath, mode, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    private static (int? Width, int? Height) ReadDimensions(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[32];
            var read = stream.Read(header);
            if (read >= 24 && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                return (BinaryPrimitives.ReadInt32BigEndian(header[16..20]), BinaryPrimitives.ReadInt32BigEndian(header[20..24]));
            if (read >= 10 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8)))
                return (BinaryPrimitives.ReadUInt16LittleEndian(header[6..8]), BinaryPrimitives.ReadUInt16LittleEndian(header[8..10]));
            if (read >= 2 && header[0] == 0xff && header[1] == 0xd8)
                return ReadJpegDimensions(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return (null, null);
    }

    private static (int? Width, int? Height) ReadJpegDimensions(Stream stream)
    {
        stream.Position = 2;
        while (stream.Position + 9 < stream.Length)
        {
            var prefix = stream.ReadByte();
            if (prefix != 0xff) continue;
            var marker = stream.ReadByte();
            while (marker == 0xff) marker = stream.ReadByte();
            if (marker < 0) break;
            if (marker is 0xd8 or 0xd9) continue;
            Span<byte> lengthBytes = stackalloc byte[2];
            if (stream.Read(lengthBytes) != 2) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
            if (length < 2) break;
            if (marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
            {
                Span<byte> dimensions = stackalloc byte[5];
                if (stream.Read(dimensions) != 5) break;
                return (BinaryPrimitives.ReadUInt16BigEndian(dimensions[3..5]), BinaryPrimitives.ReadUInt16BigEndian(dimensions[1..3]));
            }
            stream.Position += length - 2;
        }
        return (null, null);
    }
}
