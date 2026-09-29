using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
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
            StudioNavigationPolish.Apply(window, viewModel);
            StudioScriveningsFeatures.Apply(window, viewModel, repository);
            BookmarkNavigationPolish.Apply(window, viewModel);

            // Native hierarchical Project Explorer. It replaces the legacy flat Binder visual.
            ProjectExplorerFeature.Apply(window, viewModel, repository, parser);
            PdfAutoFitEnhancement.Apply(window);

            ManuscriptEditorUpgrade.Apply(window, viewModel);
            AdvancedTypesettingFeatures.Apply(window, viewModel);
            BookDesignFeature.Apply(window, viewModel);

            // Install this last so later workspace enhancements cannot replace or hide the search row.
            InstallProjectSearch(window, viewModel);
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void InstallProjectSearch(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        if (window.Content is not Grid root) return;

        var rowZeroControls = root.Children
            .OfType<Control>()
            .Where(control => Grid.GetRow(control) == 0)
            .ToArray();

        var menu = rowZeroControls
            .Select(FindMenu)
            .FirstOrDefault(candidate => candidate is not null);
        if (menu is null) return;

        // Detach the real Menu from whichever helper/container currently owns it.
        if (menu.Parent is Panel menuParent)
            menuParent.Children.Remove(menu);

        // Remove any earlier experimental row-0 wrapper/search host. We rebuild one deterministic row.
        foreach (var control in rowZeroControls)
            root.Children.Remove(control);

        menu.HorizontalAlignment = HorizontalAlignment.Left;
        menu.VerticalAlignment = VerticalAlignment.Center;

        var searchBox = new Avalonia.Controls.TextBox
        {
            Name = "ProjectSearchBox",
            Height = 28,
            MinWidth = 280,
            MaxWidth = 360,
            Watermark = "Search project (Ctrl+F)",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 2, 8, 2),
            Padding = new Thickness(8, 2)
        };

        var topBar = new Grid
        {
            Name = "TopMenuSearchBar",
            ColumnDefinitions = new ColumnDefinitions("Auto,380,*"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };
        topBar.Children.Add(menu);
        Grid.SetColumn(searchBox, 1);
        topBar.Children.Add(searchBox);
        Grid.SetRow(topBar, 0);
        root.Children.Add(topBar);

        searchBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await RunProjectSearchAsync(window, viewModel, searchBox);
        };

        // Handle Ctrl/Cmd+F in the tunnel phase and even if an older handler has already marked it handled.
        window.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) =>
            {
                var primary = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                              e.KeyModifiers.HasFlag(KeyModifiers.Meta);
                if (!primary || e.Key != Key.F) return;

                e.Handled = true;
                searchBox.Focus();
                searchBox.SelectAll();
            },
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        // Keep Edit -> Find in Project useful even though the legacy left Search tab is removed.
        var findItem = EnumerateMenuItems(menu.ItemsSource)
            .FirstOrDefault(item => string.Equals(
                NormalizeHeader(item.Header?.ToString()),
                "Find in Project",
                StringComparison.OrdinalIgnoreCase));
        if (findItem is not null)
        {
            findItem.Click += (_, _) =>
            {
                searchBox.Focus();
                searchBox.SelectAll();
            };
        }
    }

    private static Menu? FindMenu(Control? control)
    {
        if (control is Menu menu) return menu;

        if (control is Panel panel)
        {
            foreach (var child in panel.Children.OfType<Control>())
            {
                var found = FindMenu(child);
                if (found is not null) return found;
            }
        }

        if (control is Decorator decorator && decorator.Child is Control decoratedChild)
            return FindMenu(decoratedChild);

        if (control is ContentControl contentControl && contentControl.Content is Control content)
            return FindMenu(content);

        return null;
    }

    private static async Task RunProjectSearchAsync(
        Window owner,
        WorkspaceViewModel viewModel,
        Avalonia.Controls.TextBox searchBox)
    {
        var query = searchBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;

        try
        {
            await viewModel.SearchAsync(query);
            var results = viewModel.SearchResults.ToArray();
            if (results.Length == 0)
            {
                ToolTip.SetTip(searchBox, $"No results for '{query}'");
                return;
            }

            ToolTip.SetTip(searchBox, $"{results.Length} result(s)");
            if (results.Length == 1)
            {
                await viewModel.GoToSearchHitAsync(results[0]);
                return;
            }

            var rows = results
                .Select(static hit => hit.Line == 0
                    ? $"{hit.Title} [title]  {hit.Preview}"
                    : $"{hit.Title}:{hit.Line}  {hit.Preview}")
                .ToArray();

            var list = new ListBox
            {
                ItemsSource = rows,
                SelectedIndex = 0,
                MinHeight = 260
            };
            var open = new Button { Content = "Open", MinWidth = 88 };
            var cancel = new Button { Content = "Cancel", MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { open, cancel }
            };

            var dialogContent = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                Margin = new Thickness(12)
            };
            dialogContent.Children.Add(new TextBlock
            {
                Text = $"{results.Length} result(s) for '{query}'",
                Margin = new Thickness(0, 0, 0, 8)
            });
            Grid.SetRow(list, 1);
            dialogContent.Children.Add(list);
            Grid.SetRow(buttons, 2);
            dialogContent.Children.Add(buttons);

            var dialog = new Window
            {
                Title = $"Search - {query}",
                Width = 760,
                Height = 480,
                MinWidth = 520,
                MinHeight = 320,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = dialogContent
            };

            open.Click += (_, _) => dialog.Close(list.SelectedIndex >= 0 ? list.SelectedIndex : null);
            cancel.Click += (_, _) => dialog.Close(null);
            list.DoubleTapped += (_, _) => dialog.Close(list.SelectedIndex >= 0 ? list.SelectedIndex : null);

            var selectedIndex = await dialog.ShowDialog<int?>(owner);
            if (selectedIndex is int index && index >= 0 && index < results.Length)
                await viewModel.GoToSearchHitAsync(results[index]);
        }
        catch (Exception ex)
        {
            ToolTip.SetTip(searchBox, $"Search failed: {ex.Message}");
        }
    }

    private static IEnumerable<MenuItem> EnumerateMenuItems(object? source)
    {
        if (source is not IEnumerable enumerable) yield break;

        foreach (var entry in enumerable)
        {
            if (entry is not MenuItem item) continue;
            yield return item;
            foreach (var child in EnumerateMenuItems(item.ItemsSource))
                yield return child;
        }
    }

    private static string NormalizeHeader(string? header)
        => (header ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal);
}
