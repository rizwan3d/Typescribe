using System.Collections;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Application.Abstractions;
using Typescribe.Desktop.ViewModels;
using Typescribe.Infrastructure.Services;

namespace Typescribe.Desktop;

/// <summary>Installs the author-facing semantic publishing tools as one coherent Project submenu.</summary>
internal sealed class PublishingToolkitFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly TrackingProjectRepository _repository;
    private readonly IDocumentParser _parser;
    private readonly AssetManagerService _assets;
    private readonly BibTeXDatabase _bibliography = new();
    private bool _scheduled;
    private bool _installed;
    private bool _disposed;

    private PublishingToolkitFeature(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository,
        IDocumentParser parser)
    {
        _window = window;
        _viewModel = viewModel;
        _repository = repository;
        _parser = parser;
        _assets = new AssetManagerService(repository, parser);
    }

    public static void Apply(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository,
        IDocumentParser parser)
    {
        var feature = new PublishingToolkitFeature(window, viewModel, repository, parser);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        feature.Schedule();
    }

    private void OnOpened(object? sender, EventArgs e) => Schedule();
    private void OnLayoutUpdated(object? sender, EventArgs e) => Schedule();

    private void Schedule()
    {
        if (_disposed || _scheduled || _installed) return;
        _scheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scheduled = false;
            if (!_disposed) TryInstall();
        }, DispatcherPriority.Background);
    }

    private void TryInstall()
    {
        var menu = _window.GetVisualDescendants().OfType<Menu>().FirstOrDefault();
        if (menu?.ItemsSource is not IEnumerable source) return;
        var project = source.Cast<object?>().OfType<MenuItem>().FirstOrDefault(static item => HeaderEquals(item, "Project"));
        if (project is null) return;

        var items = project.ItemsSource is IEnumerable projectSource
            ? projectSource.Cast<object?>().Where(static item => item is not null).Cast<object>().ToList()
            : [];
        if (items.OfType<MenuItem>().Any(static item => item.Classes.Contains("publishing-toolkit-v1")))
        {
            _installed = true;
            _window.LayoutUpdated -= OnLayoutUpdated;
            return;
        }

        var submenu = new MenuItem { Header = "Publishing _Tools" };
        submenu.Classes.Add("publishing-toolkit-v1");
        submenu.ItemsSource = new object[]
        {
            Command("_Table Designer…", async () => await SemanticTableDesignerWindow.ShowAsync(_window, _viewModel, _parser)),
            Command("_Figures && Images…", async () => await FigureImageManagerWindow.ShowAsync(_window, _viewModel, _repository, _parser, _assets)),
            Command("_Citation Manager…", async () => await CitationManagerWindow.ShowAsync(_window, _viewModel, _repository, _parser, _bibliography)),
            Command("_Index && Glossary…", async () => await IndexGlossaryToolsWindow.ShowAsync(_window, _viewModel)),
            new Separator(),
            Command("_Named Styles…", async () => await NamedStyleManagerWindow.ShowAsync(_window, _viewModel)),
            Command("_Visual Book Design…", async () => await VisualBookDesignWindow.ShowAsync(_window, _viewModel))
        };

        var structureIndex = items.FindIndex(item => item is MenuItem menuItem && menuItem.Classes.Contains("book-structure-v1"));
        var insert = structureIndex >= 0 ? structureIndex + 1 : Math.Min(2, items.Count);
        if (insert < items.Count && items[insert] is Separator) insert++;
        items.Insert(Math.Clamp(insert, 0, items.Count), submenu);
        project.ItemsSource = items.ToArray();

        _installed = true;
        _window.LayoutUpdated -= OnLayoutUpdated;
    }

    private MenuItem Command(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) =>
        {
            if (!_viewModel.HasProject) return;
            try { await action(); }
            catch (Exception ex) { await ShowErrorAsync(ex); }
        };
        return item;
    }

    private async Task ShowErrorAsync(Exception exception)
    {
        var close = new Button { Content = "Close", MinWidth = 88, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var dialog = new Window
        {
            Title = "Publishing tool error",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = exception.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    close
                }
            }
        };
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(_window);
    }

    private static bool HeaderEquals(MenuItem item, string text)
        => string.Equals((item.Header?.ToString() ?? string.Empty)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("&&", "&", StringComparison.Ordinal), text, StringComparison.OrdinalIgnoreCase);

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
    }
}
