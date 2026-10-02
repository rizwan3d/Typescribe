using Avalonia;

namespace Typescribe.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        PublicDictionaryBootstrapper.EnsureInstalled();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
