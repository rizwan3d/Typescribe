using System.Diagnostics;

namespace Typescribe.Desktop;

internal static class ExternalFileLauncher
{
    public static void Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Preview file was not found.", fullPath);

        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fullPath,
                UseShellExecute = true
            });
            return;
        }

        var command = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        var info = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add(fullPath);
        Process.Start(info);
    }
}
