using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Typescribe.Application.Abstractions;
using Typescribe.Desktop.ViewModels;
using Typescribe.Infrastructure.Services;

namespace Typescribe.Desktop;

/// <summary>Adds EPUB 3 publishing to the existing Publish menu.</summary>
internal sealed class EpubPublishingFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly TrackingProjectRepository _repository;
    private readonly Epub3ExportService _exporter;
    private bool _installed;
    private bool _disposed;

    private EpubPublishingFeature(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository,
        IDocumentParser parser)
    {
        _window = window;
        _viewModel = viewModel;
        _repository = repository;
        _exporter = new Epub3ExportService(parser);
    }

    public static void Apply(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository,
        IDocumentParser parser)
    {
        var feature = new EpubPublishingFeature(window, viewModel, repository, parser);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        feature.TryInstall();
    }

    private void OnOpened(object? sender, EventArgs e) => TryInstall();
    private void OnLayoutUpdated(object? sender, EventArgs e) => TryInstall();

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Opened -= OnOpened;
        _window.Closed -= OnClosed;
    }

    private void TryInstall()
    {
        if (_disposed || _installed) return;
        var menu = _window.GetVisualDescendants().OfType<Menu>().FirstOrDefault();
        if (menu?.ItemsSource is not IEnumerable source) return;
        var publish = source.Cast<object>().OfType<MenuItem>()
            .FirstOrDefault(item => HeaderEquals(item, "Publish"));
        if (publish is null) return;

        var items = MenuItems(publish.ItemsSource);
        if (!items.OfType<MenuItem>().Any(item => HeaderEquals(item, "Export EPUB 3…") || HeaderEquals(item, "Export EPUB 3...")))
        {
            var command = new MenuItem { Header = "Export EPUB 3…" };
            command.Click += async (_, _) => await ExportEpubAsync();
            items.Insert(Math.Min(1, items.Count), command);
        }

        publish.ItemsSource = items.ToArray();
        _installed = true;
        _window.LayoutUpdated -= OnLayoutUpdated;
    }

    private async Task ExportEpubAsync()
    {
        var project = _repository.CurrentProject;
        if (project is null) return;

        try
        {
            await _viewModel.SaveNowAsync();
            var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export EPUB 3",
                SuggestedFileName = SanitizeFileName(project.Title) + ".epub",
                FileTypeChoices = [new FilePickerFileType("EPUB 3 ebook") { Patterns = ["*.epub"] }]
            });
            var destination = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(destination)) return;

            await PublishingProgressDialog.RunAsync(_window, "Exporting EPUB 3", async progress =>
            {
                progress.Report(0.12, "Collecting manuscript documents…");
                var documents = new List<EpubDocumentSource>();
                await foreach (var (node, content) in _repository.EnumerateDocumentsAsync(project))
                    documents.Add(new EpubDocumentSource(node, content));

                progress.Report(0.55, "Writing EPUB 3 package…");
                await _exporter.ExportAsync(project, documents, destination);
                progress.Report(0.98, "Finishing EPUB 3 export…");
            });

            await ShowMessageAsync("EPUB export complete", $"EPUB 3 book written to:\n{destination}\n\nCover convention: place cover.png, cover.jpg, cover.jpeg, cover.gif, cover.svg, or cover.webp in the project assets folder.");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("EPUB export failed", ex.Message);
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var close = new Button { Content = "Close", MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right };
        var text = new TextBox
        {
            Text = message,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        var panel = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(14) };
        panel.Children.Add(text);
        Grid.SetRow(close, 1);
        close.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(close);
        var dialog = new Window
        {
            Title = title,
            Width = 620,
            Height = 300,
            MinWidth = 480,
            MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(_window);
    }

    private static List<object> MenuItems(object? source)
        => source is IEnumerable enumerable ? enumerable.Cast<object>().ToList() : [];

    private static bool HeaderEquals(MenuItem item, string text)
        => string.Equals(Normalize(item.Header?.ToString()), Normalize(text), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? text)
        => (text ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal).Trim();

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "book" : cleaned;
    }
}
