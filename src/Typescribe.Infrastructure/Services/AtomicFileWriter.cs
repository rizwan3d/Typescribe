using System.Text;

namespace Typescribe.Infrastructure.Services;

internal static class AtomicFileWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task WriteTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("The destination has no parent directory.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            // Backups enumerate the project while metadata may be written. Allow a concurrent
            // read of the temporary file so the archive walk cannot fail with a sharing
            // violation. The writer still has exclusive write ownership; only reads/deletes
            // are shared, and the final destination is replaced atomically after flush/close.
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, Utf8NoBom, 16 * 1024, leaveOpen: true))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            ReplaceAtomically(tempPath, fullPath);
        }
        finally
        {
            TryDeleteTemporaryFile(tempPath);
        }
    }

    private static void ReplaceAtomically(string tempPath, string destinationPath)
    {
        if (!File.Exists(destinationPath))
        {
            File.Move(tempPath, destinationPath);
            return;
        }

        try
        {
            File.Replace(tempPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        catch (IOException)
        {
            File.Move(tempPath, destinationPath, overwrite: true);
        }
    }

    private static void TryDeleteTemporaryFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
        catch (IOException)
        {
            // Antivirus/indexing can briefly retain a handle after the writer closes. A stale
            // dot-prefixed temp file is safer than failing a successful atomic save.
        }
        catch (UnauthorizedAccessException)
        {
            // Same principle as above: cleanup must never turn a completed save into an error.
        }
    }
}
