using System.Collections;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop;

/// <summary>
/// Owns the project-level Book Design command. It intentionally supersedes the older
/// programmatically-built dialog from AdvancedTypesettingFeatures while leaving equation and
/// code-block tools there unchanged.
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

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
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

        if (items.OfType<MenuItem>().Any(static item => item.Classes.Contains("book-design-v2")))
        {
            _installed = true;
            _window.LayoutUpdated -= OnLayoutUpdated;
            return;
        }

        foreach (var old in items.OfType<MenuItem>().Where(static item => HeaderEquals(item, "Book Design & LaTeX…")))
            old.IsVisible = false;

        var command = new MenuItem { Header = "Book _Design && LaTeX…" };
        command.Classes.Add("book-design-v2");
        command.Click += async (_, _) =>
        {
            if (!_viewModel.HasProject) return;
            var dialog = new BookDesignDialog(_viewModel);
            await dialog.ShowDialog<bool?>(_window);
        };

        items.Insert(0, command);
        if (items.Count > 1 && items[1] is not Separator)
            items.Insert(1, new Separator());
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
