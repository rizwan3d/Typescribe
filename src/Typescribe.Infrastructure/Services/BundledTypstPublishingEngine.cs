using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Typescribe.Application.Abstractions;

namespace Typescribe.Infrastructure.Services;

public sealed class BundledTypstPublishingEngine : IPdfPublishingEngine
{
    private const string ResourceName = "Typescribe.Engine.typst";
    private const string EngineVersion = "0.15.1";
    private const string ReleaseBaseUrl = "https://github.com/typst/typst/releases/download";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private readonly Assembly _assembly = typeof(BundledTypstPublishingEngine).Assembly;
    private readonly SemaphoreSlim _engineGate = new(1, 1);
    private byte[]? _embeddedHash;

    public string Name => $"Typst {EngineVersion}";

    public bool IsAvailable => HasEmbeddedEngine() || File.Exists(GetInstalledEnginePath());

    public async Task EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (IsAvailable) return;

        await _engineGate.WaitAsync(cancellationToken);
        try
        {
            if (IsAvailable) return;

            var asset = GetDownloadAsset();
            var work = Path.Combine(Path.GetTempPath(), "typescribe", "engine-download", Guid.NewGuid().ToString("N"));
            var archivePath = Path.Combine(work, asset.FileName);
            var extractedPath = Path.Combine(work, "extracted");

            Directory.CreateDirectory(work);
            Directory.CreateDirectory(extractedPath);

            try
            {
                await DownloadAndVerifyAsync(asset, archivePath, cancellationToken);
                await ExtractArchiveAsync(asset, archivePath, extractedPath, cancellationToken);

                var executableName = OperatingSystem.IsWindows() ? "typst.exe" : "typst";
                var extractedExecutable = Directory
                    .EnumerateFiles(extractedPath, executableName, SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (extractedExecutable is null)
                    throw new InvalidOperationException("The downloaded Typst package did not contain the publishing engine executable.");

                var enginePath = GetInstalledEnginePath();
                var engineDirectory = Path.GetDirectoryName(enginePath)
                    ?? throw new InvalidOperationException("Could not determine the Typescribe engine directory.");
                Directory.CreateDirectory(engineDirectory);

                var tempEnginePath = enginePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.Copy(extractedExecutable, tempEnginePath, overwrite: false);
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(tempEnginePath,
                            UnixFileMode.UserRead |
                            UnixFileMode.UserWrite |
                            UnixFileMode.UserExecute);
                    }

                    File.Move(tempEnginePath, enginePath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tempEnginePath)) File.Delete(tempEnginePath);
                }
            }
            finally
            {
                TryDeleteDirectory(work);
            }

            if (!File.Exists(GetInstalledEnginePath()))
                throw new InvalidOperationException("The PDF publishing engine download completed, but the engine could not be installed.");
        }
        finally
        {
            _engineGate.Release();
        }
    }

    public async Task PublishAsync(string typstSource, string outputPdfPath, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("The PDF publishing engine is not installed. Use 'Download PDF Engine' and try again.");

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

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not launch the PDF publishing engine.");
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
            TryDeleteDirectory(work);
        }
    }

    private async Task<string> EnsureEngineExtractedAsync(CancellationToken cancellationToken)
    {
        var enginePath = GetInstalledEnginePath();

        await _engineGate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(enginePath))
            {
                if (!HasEmbeddedEngine()) return enginePath;

                var embeddedHash = GetEmbeddedHash();
                if (HashEquals(enginePath, embeddedHash)) return enginePath;
            }

            await using var source = _assembly.GetManifestResourceStream(ResourceName);
            if (source is null)
                throw new InvalidOperationException("The PDF publishing engine is not installed. Use 'Download PDF Engine' and try again.");

            var expectedHash = GetEmbeddedHash();
            var directory = Path.GetDirectoryName(enginePath)
                ?? throw new InvalidOperationException("Could not determine the Typescribe engine directory.");
            Directory.CreateDirectory(directory);

            var tempPath = enginePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var destination = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
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
            _engineGate.Release();
        }
    }

    private bool HasEmbeddedEngine()
    {
        using var stream = _assembly.GetManifestResourceStream(ResourceName);
        return stream is not null;
    }

    private byte[] GetEmbeddedHash()
    {
        if (_embeddedHash is not null) return _embeddedHash;
        using var stream = _assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The embedded publishing engine resource is missing.");
        _embeddedHash = SHA256.HashData(stream);
        return _embeddedHash;
    }

    private static async Task DownloadAndVerifyAsync(DownloadAsset asset, string archivePath, CancellationToken cancellationToken)
    {
        var url = $"{ReleaseBaseUrl}/v{EngineVersion}/{asset.FileName}";
        using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[128 * 1024];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        await destination.FlushAsync(cancellationToken);
        var actualHash = Convert.ToHexString(hasher.GetHashAndReset());
        if (!actualHash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Typst download integrity check failed. Expected {asset.Sha256}, got {actualHash}.");
    }

    private static async Task ExtractArchiveAsync(
        DownloadAsset asset,
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        if (asset.IsZip)
        {
            ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: true);
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "tar",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-xJf");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(destination);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the operating system archive extractor.");
            var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Could not extract the Typst download: {stdErr}\n{stdOut}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("The operating system 'tar' utility is required to install Typst on this platform.", ex);
        }
    }

    private static DownloadAsset GetDownloadAsset()
    {
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            return new DownloadAsset(
                "typst-x86_64-pc-windows-msvc.zip",
                "19ce3551153c2fe7ee9fa2f95208310c8f4d3209fedb699e0333faf8913f6736",
                IsZip: true);

        if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            return new DownloadAsset(
                "typst-x86_64-unknown-linux-musl.tar.xz",
                "a6d077d0a95eed5a2eba715b2dae06be954f624ccbf85758a03f389ded33118c",
                IsZip: false);

        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            return new DownloadAsset(
                "typst-x86_64-apple-darwin.tar.xz",
                "7f9fdd9584866245de9a79e0add8f9236fae6f40a8a45e2c4771ccc14db4e0fa",
                IsZip: false);

        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return new DownloadAsset(
                "typst-aarch64-apple-darwin.tar.xz",
                "48f62ed034aa3a7978309579ac6ca00045e2ef0da73114e8af27cfd8e74dc05a",
                IsZip: false);

        throw new PlatformNotSupportedException(
            $"Automatic Typst download is not available for {RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}.");
    }

    private static string GetInstalledEnginePath()
    {
        var executable = OperatingSystem.IsWindows() ? "typst.exe" : "typst";
        var platform = $"{Environment.OSVersion.Platform}-{RuntimeInformation.ProcessArchitecture}";
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(basePath)) basePath = Path.GetTempPath();
        return Path.Combine(basePath, "Typescribe", "engines", $"typst-{EngineVersion}", platform, executable);
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

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Typescribe/0.1");
        return client;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record DownloadAsset(string FileName, string Sha256, bool IsZip);
}
