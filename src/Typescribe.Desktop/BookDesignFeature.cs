using System.Collections;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Application.Abstractions;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop;

/// <summary>
/// Owns the project-level Book Design commands and the complete live-PDF workspace.
/// It supersedes the older partial style dialogs while leaving equation and code-block tools
/// in AdvancedTypesettingFeatures unchanged.
/// </summary>
internal sealed class BookDesignFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private bool _scheduled;
    private bool _installed;
    private bool _disposed;

    private BookDesignFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel, IDocumentParser parser)
    {
        LivePdfControlsFeature.Apply(window, viewModel);
        ContinuousPdfPreviewFeature.Apply(window, viewModel);
        EditorPreviewScopeFeature.Apply(window, viewModel);
        LegacyTableCommandSuppressionFeature.Apply(window);

        // Keep the publishing toolkit coupled to the book-design composition point so every
        // desktop host that gets Book Design also gets table/figure/citation/index/style tools.
        if (TrackingProjectRepository.ActiveInstance is { } repository)
            PublishingToolkitFeature.Apply(window, viewModel, repository, parser);

        var feature = new BookDesignFeature(window, viewModel);
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

        var hasDesign = items.OfType<MenuItem>().Any(static item => item.Classes.Contains("book-design-v2"));
        var hasStructure = items.OfType<MenuItem>().Any(static item => item.Classes.Contains("book-structure-v1"));
        if (hasDesign && hasStructure)
        {
            _installed = true;
            _window.LayoutUpdated -= OnLayoutUpdated;
            return;
        }

        foreach (var old in items.OfType<MenuItem>().Where(static item =>
                     HeaderEquals(item, "Book Style…") || HeaderEquals(item, "Book Design & LaTeX…")))
            old.IsVisible = false;

        if (!hasDesign)
        {
            var designCommand = new MenuItem { Header = "Book _Design && LaTeX…" };
            designCommand.Classes.Add("book-design-v2");
            designCommand.Click += async (_, _) =>
            {
                if (!_viewModel.HasProject) return;
                var dialog = new BookDesignDialog(_viewModel);
                BookDesignInputWiring.Apply(dialog);
                await dialog.ShowDialog<bool?>(_window);
            };
            items.Insert(0, designCommand);
        }

        if (!hasStructure)
        {
            var structureCommand = new MenuItem { Header = "Book _Structure…" };
            structureCommand.Classes.Add("book-structure-v1");
            structureCommand.Click += async (_, _) =>
            {
                if (!_viewModel.HasProject) return;
                var dialog = new BookStructureDialog(_viewModel);
                await dialog.ShowDialog<bool?>(_window);
            };
            var designIndex = items.FindIndex(item => item is MenuItem menuItem && menuItem.Classes.Contains("book-design-v2"));
            items.Insert(designIndex >= 0 ? designIndex + 1 : 0, structureCommand);
        }

        var structureIndex = items.FindIndex(item => item is MenuItem menuItem && menuItem.Classes.Contains("book-structure-v1"));
        if (structureIndex >= 0 && structureIndex + 1 < items.Count && items[structureIndex + 1] is not Separator)
            items.Insert(structureIndex + 1, new Separator());
        project.ItemsSource = items.ToArray();

        _installed = true;
        _window.LayoutUpdated -= OnLayoutUpdated;
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
