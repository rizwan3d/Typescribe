using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using AvaloniaApplication = Avalonia.Application;
using Typescribe.Application.Services;
using Typescribe.Desktop.Editing;
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
            Source = new Uri("avares://Typescribe/Styles/ManuscriptEditorTheme.axaml")
        });
        Styles.Add(new StyleInclude(new Uri("avares://Typescribe/"))
        {
            Source = new Uri("avares://Typescribe/Styles/StudioTheme.axaml")
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var repository = new TrackingProjectRepository(new FileSystemProjectRepository());
            var parser = new DocumentParser();
            var renderer = new LuaLatexSafeDocumentRenderer();
            var publishingEngine = new LuaLatexPublishingEngine();
            var exportService = new DocumentExportService(parser, renderer, publishingEngine);
            var searchService = new ProjectSearchService(repository);
            var viewModel = new WorkspaceViewModel(repository, parser, renderer, new WordCountService(), searchService, exportService);
            var window = new StudioWorkspaceWindow(viewModel);

            if (window.Content is Grid root && root.RowDefinitions.Count >= 4)
            {
                var toolbar = root.Children.OfType<Control>().FirstOrDefault(control => Grid.GetRow(control) == 1);
                if (toolbar is not null) toolbar.IsVisible = false;
                root.RowDefinitions[1].Height = new GridLength(0);
            }

            StudioUxPolish.Apply(window);
            StudioAuthoringEnhancements.Apply(window, viewModel);
            StudioScriveningsFeatures.Apply(window, viewModel, repository);

            // Native hierarchical Project Explorer. It replaces the legacy flat Binder visual.
            ProjectExplorerFeature.Apply(window, viewModel, repository, parser);
            PdfAutoFitEnhancement.Apply(window);

            ManuscriptEditorUpgrade.Apply(window, viewModel);
            AdvancedTypesettingFeatures.Apply(window, viewModel);
            BookDesignFeature.Apply(window, viewModel);
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
