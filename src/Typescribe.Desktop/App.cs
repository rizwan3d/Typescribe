using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using AvaloniaApplication = Avalonia.Application;
using Typescribe.Application.Services;
using Typescribe.Desktop.ViewModels;
using Typescribe.Infrastructure.Services;

namespace Typescribe.Desktop;

public sealed class App : AvaloniaApplication
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Default;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Typescribe/"))
        {
            Source = new Uri("avares://Typescribe/Styles/StudioTheme.axaml")
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var repository = new FileSystemProjectRepository();
            var parser = new DocumentParser();
            var renderer = new DocumentRenderer();
            var publishingEngine = new LuaLatexPublishingEngine();
            var exportService = new DocumentExportService(parser, renderer, publishingEngine);
            var searchService = new ProjectSearchService(repository);
            var viewModel = new WorkspaceViewModel(repository, parser, renderer, new WordCountService(), searchService, exportService);
            var window = new StudioWorkspaceWindow(viewModel);
            StudioUxPolish.Apply(window);
            StudioAuthoringEnhancements.Apply(window);
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
