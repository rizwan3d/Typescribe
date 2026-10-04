using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Typescribe.Application.Abstractions;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;
using Typescribe.Infrastructure.Services;

namespace Typescribe.Desktop;

/// <summary>
/// Adds EPUB 3 publishing to the existing Publish menu and upgrades the standard DOCX
/// command so merged table geometry is preserved after the semantic Word export.
/// </summary>
internal sealed class EpubPublishingFeature
{
    private const string MergedDocxClass = "merged-table-docx-export";

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly TrackingProjectRepository _repository;
    private readonly IDocumentParser _parser;
    private readonly Epub3ExportService _exporter;
    private readonly DocxInterchangeService _docx;
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
        _parser = parser;
        _exporter = new Epub3ExportService(parser);
        _docx = new DocxInterchangeService(parser, new BibTeXDatabase());
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
        UpgradeDocxCommand(items);

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

    private void UpgradeDocxCommand(List<object> items)
    {
        if (items.OfType<MenuItem>().Any(static item => item.Classes.Contains(MergedDocxClass))) return;

        var existingIndex = items.FindIndex(item =>
            item is MenuItem menuItem &&
            (HeaderEquals(menuItem, "Export DOCX…") || HeaderEquals(menuItem, "Export DOCX...")));
        if (existingIndex < 0) return;

        items.RemoveAt(existingIndex);
        var command = new MenuItem { Header = "Export DOCX…" };
        command.Classes.Add(MergedDocxClass);
        command.Click += async (_, _) => await ExportDocxAsync();
        items.Insert(existingIndex, command);
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

                progress.Report(0.86, "Finalizing merged tables…");
                await MergedTableExportPostProcessor.ApplyEpubAsync(
                    destination,
                    documents.Select(static document => document.Content),
                    _parser);
                progress.Report(0.98, "Finishing EPUB 3 export…");
            });

            await ShowMessageAsync("EPUB export complete", $"EPUB 3 book written to:\n{destination}\n\nCover convention: place cover.png, cover.jpg, cover.jpeg, cover.gif, cover.svg, or cover.webp in the project assets folder.");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("EPUB export failed", ex.Message);
        }
    }

    private async Task ExportDocxAsync()
    {
        var project = _repository.CurrentProject;
        if (project is null) return;

        try
        {
            await _viewModel.SaveNowAsync();
            var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export DOCX",
                SuggestedFileName = SanitizeFileName(project.Title) + ".docx",
                FileTypeChoices = [new FilePickerFileType("Word document") { Patterns = ["*.docx"] }]
            });
            var destination = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(destination)) return;

            await PublishingProgressDialog.RunAsync(_window, "Exporting DOCX", async progress =>
            {
                progress.Report(0.12, "Collecting manuscript documents…");
                var documents = new List<(ProjectNode Node, string Content)>();
                await foreach (var item in _repository.EnumerateDocumentsAsync(project))
                    documents.Add(item);

                progress.Report(0.58, "Writing Word document…");
                await _docx.ExportAsync(project, documents, destination);

                progress.Report(0.86, "Finalizing merged tables…");
                await MergedTableExportPostProcessor.ApplyDocxAsync(
                    destination,
                    documents.Select(static document => document.Content),
                    _parser);
                progress.Report(0.98, "Finishing DOCX export…");
            });

            await ShowMessageAsync("DOCX export complete", $"Word document written to:\n{destination}");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("DOCX export failed", ex.Message);
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
