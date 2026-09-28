using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Typescribe.Application.Abstractions;

namespace Typescribe.Infrastructure.Services;

public sealed class BundledTypstPublishingEngine : IPdfPublishingEngine
{
    private const string ResourceName = "Typescribe.Engine.typst";
    private const string EngineVersion = "0.15.1";
    private readonly Assembly _assembly = typeof(BundledTypstPublishingEngine).Assembly;
    private readonly SemaphoreSlim _extractGate = new(1, 1);
    private byte[]? _embeddedHash;

    public string Name => $"Bundled Typst {EngineVersion}";

    public bool IsAvailable
    {
        get
        {
            using var stream = _assembly.GetManifestResourceStream(ResourceName);
            return stream is not null;
        }
    }

    public async Task PublishAsync(string typstSource, string outputPdfPath, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("This development build does not contain the publishing engine. Build a release bundle with scripts/publish.* so Typst is embedded automatically.");

        var enginePath = await EnsureEngineExtractedAsync(cancellationToken);
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPdfPath));
        if (!string.IsNullOrWhiteSpace(outputDirectory)) Directory.CreateDirectory(outputDirectory);

        var work = Path.Combine(Path.GetTempPath(), "typescribe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var sourcePath = Path.Combine(work, "document.typ");
        await File.WriteAllTextAsync(sourcePath, typstSource, cancellationToken);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = enginePath,
                WorkingDirectory = work,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("compile");
            startInfo.ArgumentList.Add(sourcePath);
            startInfo.ArgumentList.Add(Path.GetFullPath(outputPdfPath));

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not launch the bundled publishing engine.");
            var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Typst failed with exit code {process.ExitCode}: {stdErr}\n{stdOut}");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<string> EnsureEngineExtractedAsync(CancellationToken cancellationToken)
    {
        var executable = OperatingSystem.IsWindows() ? "typst.exe" : "typst";
        var platform = $"{Environment.OSVersion.Platform}-{RuntimeInformation.ProcessArchitecture}";
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(basePath)) basePath = Path.GetTempPath();
        var directory = Path.Combine(basePath, "Typescribe", "engines", $"typst-{EngineVersion}", platform);
        var enginePath = Path.Combine(directory, executable);

        await _extractGate.WaitAsync(cancellationToken);
        try
        {
            var expectedHash = GetEmbeddedHash();
            if (File.Exists(enginePath) && HashEquals(enginePath, expectedHash)) return enginePath;

            Directory.CreateDirectory(directory);
            var tempPath = enginePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var source = _assembly.GetManifestResourceStream(ResourceName)
                    ?? throw new InvalidOperationException("The embedded publishing engine resource is missing."))
                await using (var destination = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await source.CopyToAsync(destination, 128 * 1024, cancellationToken);
                    await destination.FlushAsync(cancellationToken);
                }

                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(tempPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }

                File.Move(tempPath, enginePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }

            if (!HashEquals(enginePath, expectedHash))
                throw new InvalidOperationException("The extracted publishing engine failed its integrity check.");

            return enginePath;
        }
        finally
        {
            _extractGate.Release();
        }
    }

    private byte[] GetEmbeddedHash()
    {
        if (_embeddedHash is not null) return _embeddedHash;
        using var stream = _assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The embedded publishing engine resource is missing.");
        _embeddedHash = SHA256.HashData(stream);
        return _embeddedHash;
    }

    private static bool HashEquals(string path, byte[] expected)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
            var actual = SHA256.HashData(stream);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
