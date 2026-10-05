using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Typescribe.Application.Abstractions;

namespace Typescribe.Infrastructure.Services;

public sealed class LuaLatexPublishingEngine : IPdfPublishingEngine, IDisposable
{
    private const string TinyTexVersion = "2026.09";
    private const string ReleaseBaseUrl = "https://github.com/rstudio/tinytex-releases/releases/download";
    private const string PathMarkerFile = ".typescribe-lualatex-path";

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private readonly SemaphoreSlim _engineGate = new(1, 1);
    private readonly SemaphoreSlim _packageInstallGate = new(1, 1);
    private string? _resolvedExecutable;

    public string Name => $"LuaLaTeX (TinyTeX {TinyTexVersion})";
    public bool IsAvailable => ResolveExistingExecutable() is not null;

    public void Dispose()
    {
        _engineGate.Dispose();
        _packageInstallGate.Dispose();
    }

    public async Task EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (ResolveExistingExecutable() is not null) return;

        await _engineGate.WaitAsync(cancellationToken);
        try
        {
            if (ResolveExistingExecutable() is not null) return;

            var asset = GetDownloadAsset();
            var installRoot = GetInstalledEngineRoot();
            var parent = Path.GetDirectoryName(installRoot)
                ?? throw new InvalidOperationException("Could not determine the Typescribe engine directory.");
            Directory.CreateDirectory(parent);

            var work = Path.Combine(Path.GetTempPath(), "typescribe", "tinytex-install", Guid.NewGuid().ToString("N"));
            var archivePath = Path.Combine(work, asset.FileName);
            var extracted = Path.Combine(work, "extracted");
            Directory.CreateDirectory(extracted);

            try
            {
                await DownloadAndVerifyAsync(asset, archivePath, cancellationToken);
                await ExtractAsync(asset, archivePath, extracted, cancellationToken);

                var executable = FindLuaLatexUnder(extracted)
                    ?? throw new InvalidOperationException("The downloaded TinyTeX package did not contain LuaLaTeX.");

                var relativeExecutable = Path.GetRelativePath(extracted, executable);
                var stagedInstall = installRoot + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    Directory.Move(extracted, stagedInstall);
                    if (Directory.Exists(installRoot)) Directory.Delete(installRoot, recursive: true);
                    Directory.Move(stagedInstall, installRoot);
                }
                finally
                {
                    if (Directory.Exists(stagedInstall)) TryDeleteDirectory(stagedInstall);
                }

                var installedExecutable = Path.Combine(installRoot, relativeExecutable);
                if (!File.Exists(installedExecutable))
                    throw new InvalidOperationException("TinyTeX was extracted, but the LuaLaTeX executable could not be located after installation.");

                await File.WriteAllTextAsync(Path.Combine(installRoot, PathMarkerFile), relativeExecutable, cancellationToken);
                _resolvedExecutable = installedExecutable;
            }
            finally
            {
                TryDeleteDirectory(work);
            }
        }
        finally
        {
            _engineGate.Release();
        }
    }

    public async Task PublishAsync(
        string source,
        string outputPdfPath,
        int passes = 2,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPdfPath);
        if (passes is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(passes));

        var executable = ResolveExistingExecutable();
        if (executable is null)
            throw new InvalidOperationException("LuaLaTeX is not installed. Use 'Download LuaLaTeX' and try again.");

        var outputFullPath = Path.GetFullPath(outputPdfPath);
        var outputDirectory = Path.GetDirectoryName(outputFullPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory)) Directory.CreateDirectory(outputDirectory);

        var work = Path.Combine(Path.GetTempPath(), "typescribe", "lualatex", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var sourcePath = Path.Combine(work, "document.tex");
        await File.WriteAllTextAsync(sourcePath, source, cancellationToken);

        try
        {
            var recoveredPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var pass = 0; pass < passes; pass++)
                await RunLuaLatexPassWithPackageRecoveryAsync(
                    executable,
                    sourcePath,
                    work,
                    recoveredPackages,
                    cancellationToken);

            var producedPdf = Path.Combine(work, "document.pdf");
            if (!File.Exists(producedPdf))
                throw new InvalidOperationException("LuaLaTeX completed without producing document.pdf.");

            var tempOutput = outputFullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(producedPdf, tempOutput, overwrite: true);
                File.Move(tempOutput, outputFullPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempOutput)) File.Delete(tempOutput);
            }
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    private string? ResolveExistingExecutable()
    {
        if (_resolvedExecutable is { } cached && File.Exists(cached)) return cached;

        var configured = Environment.GetEnvironmentVariable("TYPESCRIBE_LUALATEX");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return _resolvedExecutable = Path.GetFullPath(configured);

        var installedRoot = GetInstalledEngineRoot();
        var marker = Path.Combine(installedRoot, PathMarkerFile);
        if (File.Exists(marker))
        {
            var relative = File.ReadAllText(marker).Trim();
            if (!string.IsNullOrWhiteSpace(relative))
            {
                var candidate = Path.GetFullPath(Path.Combine(installedRoot, relative));
                if (File.Exists(candidate)) return _resolvedExecutable = candidate;
            }
        }

        var system = FindOnPath(OperatingSystem.IsWindows() ? "lualatex.exe" : "lualatex");
        if (system is not null) return _resolvedExecutable = system;

        return null;
    }

    private async Task RunLuaLatexPassWithPackageRecoveryAsync(
        string executable,
        string sourcePath,
        string workingDirectory,
        HashSet<string> recoveredPackages,
        CancellationToken cancellationToken)
    {
        var result = await RunLuaLatexPassAsync(executable, sourcePath, workingDirectory, cancellationToken);
        if (result.ExitCode == 0) return;

        var output = result.CombinedOutput;
        if (TryGetMissingTexPackage(output, out var packageName) &&
            recoveredPackages.Add(packageName) &&
            await TryInstallTexPackageAsync(executable, packageName, cancellationToken))
        {
            result = await RunLuaLatexPassAsync(executable, sourcePath, workingDirectory, cancellationToken);
            if (result.ExitCode == 0) return;
            output = result.CombinedOutput;
        }

        var details = ExtractUsefulError(output);
        throw new InvalidOperationException($"LuaLaTeX failed with exit code {result.ExitCode}:{Environment.NewLine}{details}");
    }

    private static async Task<ProcessResult> RunLuaLatexPassAsync(
        string executable,
        string sourcePath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(CreateLuaLatexStartInfo(executable, sourcePath, workingDirectory), cancellationToken);
        return result;
    }

    private static ProcessStartInfo CreateLuaLatexStartInfo(
        string executable,
        string sourcePath,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-interaction=nonstopmode");
        startInfo.ArgumentList.Add("-halt-on-error");
        startInfo.ArgumentList.Add("-file-line-error");
        startInfo.ArgumentList.Add("-no-shell-escape");
        startInfo.ArgumentList.Add($"-output-directory={workingDirectory}");
        startInfo.ArgumentList.Add(sourcePath);
        AddEngineDirectoryToPath(startInfo, executable);
        return startInfo;
    }

    private async Task<bool> TryInstallTexPackageAsync(
        string executable,
        string packageName,
        CancellationToken cancellationToken)
    {
        var engineDirectory = Path.GetDirectoryName(executable);
        if (string.IsNullOrWhiteSpace(engineDirectory)) return false;

        var tlmgr = FindTlmgr(engineDirectory);
        if (tlmgr is null) return false;

        await _packageInstallGate.WaitAsync(cancellationToken);
        try
        {
            var kpsewhich = FindKpsewhich(engineDirectory);
            if (kpsewhich is not null &&
                await TexFileExistsAsync(kpsewhich, packageName + ".sty", cancellationToken))
                return true;

            var startInfo = new ProcessStartInfo
            {
                FileName = tlmgr,
                WorkingDirectory = engineDirectory,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("install");
            startInfo.ArgumentList.Add(packageName);
            AddEngineDirectoryToPath(startInfo, executable);

            var result = await RunProcessAsync(startInfo, cancellationToken);
            return result.ExitCode == 0;
        }
        finally
        {
            _packageInstallGate.Release();
        }
    }

    private static async Task<bool> TexFileExistsAsync(
        string kpsewhich,
        string fileName,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = kpsewhich,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(fileName);

        var result = await RunProcessAsync(startInfo, cancellationToken);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput);
    }

    private static async Task<ProcessResult> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Could not start {startInfo.FileName}.");
            using var cancellationRegistration = cancellationToken.Register(static state =>
            {
                var runningProcess = (Process)state!;
                try
                {
                    if (!runningProcess.HasExited) runningProcess.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }, process);

            var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;

            return new ProcessResult(process.ExitCode, stdOut, stdErr);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new ProcessResult(-1, string.Empty, $"Could not start {startInfo.FileName}.");
        }
    }

    private static void AddEngineDirectoryToPath(ProcessStartInfo startInfo, string executable)
    {
        var engineDirectory = Path.GetDirectoryName(executable);
        if (string.IsNullOrWhiteSpace(engineDirectory)) return;

        var path = startInfo.Environment.TryGetValue("PATH", out var currentPath)
            ? currentPath
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        startInfo.Environment["PATH"] = engineDirectory + Path.PathSeparator + path;
    }

    private static string? FindTlmgr(string engineDirectory)
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[] { "tlmgr.bat", "tlmgr.exe", "tlmgr" }
            : new[] { "tlmgr" };
        return candidates
            .Select(name => Path.Combine(engineDirectory, name))
            .FirstOrDefault(File.Exists);
    }

    private static string? FindKpsewhich(string engineDirectory)
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[] { "kpsewhich.exe", "kpsewhich" }
            : new[] { "kpsewhich" };
        return candidates
            .Select(name => Path.Combine(engineDirectory, name))
            .FirstOrDefault(File.Exists);
    }

    private static bool TryGetMissingTexPackage(string output, out string packageName)
    {
        packageName = string.Empty;
        var normalized = output.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (var line in normalized.Split('\n'))
        {
            if (!line.Contains("not found", StringComparison.OrdinalIgnoreCase)) continue;

            var start = line.IndexOf('`');
            var end = start >= 0 ? line.IndexOf('\'', start + 1) : -1;
            if (start < 0 || end <= start) continue;

            var missingFile = line[(start + 1)..end].Trim();
            if (!TryConvertMissingTexFileToPackage(missingFile, out packageName)) continue;
            return true;
        }

        return false;
    }

    private static bool TryConvertMissingTexFileToPackage(string missingFile, out string packageName)
    {
        packageName = string.Empty;
        var extension = Path.GetExtension(missingFile);
        if (!extension.Equals(".sty", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".cls", StringComparison.OrdinalIgnoreCase))
            return false;

        var name = Path.GetFileNameWithoutExtension(missingFile);
        if (string.IsNullOrWhiteSpace(name) ||
            name.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')))
            return false;

        packageName = name;
        return true;
    }

    private static async Task DownloadAndVerifyAsync(DownloadAsset asset, string archivePath, CancellationToken cancellationToken)
    {
        var url = $"{ReleaseBaseUrl}/v{TinyTexVersion}/{asset.FileName}";
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

        var actual = Convert.ToHexString(hasher.GetHashAndReset());
        if (!actual.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"TinyTeX download integrity check failed. Expected {asset.Sha256}, got {actual}.");
    }

    private static async Task ExtractAsync(
        DownloadAsset asset,
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        if (asset.SelfExtractingWindowsArchive)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = archivePath,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add($"-o{destination}");
            await RunExtractorAsync(startInfo, cancellationToken);
            return;
        }

        var tar = new ProcessStartInfo
        {
            FileName = "tar",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        tar.ArgumentList.Add("-xJf");
        tar.ArgumentList.Add(archivePath);
        tar.ArgumentList.Add("-C");
        tar.ArgumentList.Add(destination);
        await RunExtractorAsync(tar, cancellationToken);
    }

    private static async Task RunExtractorAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the TinyTeX archive extractor.");
            using var cancellationRegistration = cancellationToken.Register(static state =>
            {
                var runningProcess = (Process)state!;
                try
                {
                    if (!runningProcess.HasExited) runningProcess.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }, process);
            var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Could not extract TinyTeX: {stdErr}{Environment.NewLine}{stdOut}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("The operating system archive extractor could not be started.", ex);
        }
    }

    private static DownloadAsset GetDownloadAsset()
    {
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            return new DownloadAsset(
                "TinyTeX-windows-v2026.09.exe",
                "f2e7c2f6de04c958c0a9660f86af7f3d7f385b98cdc4cb715e915f899e0eec3b",
                SelfExtractingWindowsArchive: true);

        if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            return new DownloadAsset(
                "TinyTeX-linux-x86_64-v2026.09.tar.xz",
                "9551e932ed74ea77a36b4bd3fb6d852f540c044037b083f77a8b15d4b97ca3fc",
                SelfExtractingWindowsArchive: false);

        if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return new DownloadAsset(
                "TinyTeX-linux-arm64-v2026.09.tar.xz",
                "6fc321d7c57f133e62f07a711dbd52c79fda2d152a231afc421ab762b02e217b",
                SelfExtractingWindowsArchive: false);

        if (OperatingSystem.IsMacOS())
            return new DownloadAsset(
                "TinyTeX-darwin-v2026.09.tar.xz",
                "b8d9528b0d475b64e00b379111438b26b9e127a9b2b76782aecfc5db3326aa03",
                SelfExtractingWindowsArchive: false);

        throw new PlatformNotSupportedException(
            $"Automatic LuaLaTeX setup is not available for {RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}.");
    }

    private static string? FindLuaLatexUnder(string root)
    {
        var filename = OperatingSystem.IsWindows() ? "lualatex.exe" : "lualatex";
        return Directory.EnumerateFiles(root, filename, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, executable);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }
        return null;
    }

    private static string GetInstalledEngineRoot()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(basePath)) basePath = Path.GetTempPath();
        var platform = OperatingSystem.IsWindows()
            ? "windows-x64"
            : OperatingSystem.IsMacOS()
                ? $"macos-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}"
                : $"linux-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
        return Path.Combine(basePath, "Typescribe", "engines", $"tinytex-full-{TinyTexVersion}", platform);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Typescribe/0.1");
        return client;
    }

    private static string ExtractUsefulError(string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var errorIndex = Array.FindIndex(lines, static line => line.StartsWith("!", StringComparison.Ordinal) || line.Contains("LaTeX Error:", StringComparison.Ordinal));
        if (errorIndex >= 0)
        {
            var start = Math.Max(0, errorIndex - 4);
            var count = Math.Min(lines.Length - start, 20);
            return string.Join(Environment.NewLine, lines.Skip(start).Take(count));
        }
        return Tail(normalized, 6_000);
    }

    private static string Tail(string value, int maxLength)
        => value.Length <= maxLength ? value : value[^maxLength..];

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

    private sealed record DownloadAsset(string FileName, string Sha256, bool SelfExtractingWindowsArchive);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => StandardError + Environment.NewLine + StandardOutput;
    }
}
