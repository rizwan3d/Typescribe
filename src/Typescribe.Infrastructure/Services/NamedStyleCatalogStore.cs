using System.Text.Json;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

/// <summary>
/// Persists reusable semantic publishing styles independently from the legacy scalar book.style file.
/// Keeping this as a versioned JSON sidecar allows the style system to evolve without making the
/// human-readable book.style format brittle. Desktop repository composition rehydrates BookStyle.NamedStyles.
/// </summary>
public sealed class NamedStyleCatalogStore
{
    private const string FolderName = "styles";
    private const string FileName = "named-styles.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public NamedStyleCatalog Load(string projectRoot)
    {
        var path = GetPath(projectRoot);
        if (!File.Exists(path)) return NamedStyleCatalog.Default;
        try
        {
            var json = File.ReadAllText(path);
            var catalog = JsonSerializer.Deserialize<NamedStyleCatalog>(json, Options);
            return (catalog ?? NamedStyleCatalog.Default).Validate();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return NamedStyleCatalog.Default;
        }
    }

    public Task SaveAsync(string projectRoot, NamedStyleCatalog catalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        catalog.Validate();
        var path = GetPath(projectRoot);
        var json = JsonSerializer.Serialize(catalog, Options) + Environment.NewLine;
        return AtomicFileWriter.WriteTextAsync(path, json, cancellationToken);
    }

    private static string GetPath(string projectRoot)
    {
        var folder = Path.Combine(projectRoot, FolderName);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, FileName);
    }
}
