using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop;

/// <summary>
/// Promotes the live PDF surface to the main workspace so PDF Preview sits directly beside Editor.
/// The Inspector keeps a lightweight shortcut back to that main PDF surface.
/// </summary>
internal sealed class CenterPdfPreviewFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private TabControl? _centerTabs;
    private TabItem? _centerPdfTab;
    private TabControl? _inspectorTabs;
    private TabItem? _inspectorPreviewTab;
    private bool _installed;
    private bool _disposed;

    private CenterPdfPreviewFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var feature = new CenterPdfPreviewFeature(window, viewModel);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e) => TryInstall();

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_installed && !_disposed) TryInstall();
    }

    private void TryInstall()
    {
        if (_installed || _disposed) return;

        var allTabs = _window.GetVisualDescendants().OfType<TabControl>().ToArray();
        _centerTabs = allTabs.FirstOrDefault(tabs =>
        {
            var items = TabItems(tabs);
            return items.Any(static item => HeaderEquals(item, "Editor")) &&
                   items.Any(static item => HeaderEquals(item, "Corkboard")) &&
                   items.Any(static item => HeaderEquals(item, "Outliner"));
        });
        if (_centerTabs is null) return;

        var centerItems = TabItems(_centerTabs);
        _centerPdfTab = centerItems.FirstOrDefault(static item => HeaderEquals(item, "PDF Preview"))
            ?? centerItems.FirstOrDefault(static item => HeaderEquals(item, "Preview"));
        if (_centerPdfTab is null) return;

        _inspectorTabs = allTabs.FirstOrDefault(tabs =>
        {
            if (ReferenceEquals(tabs, _centerTabs)) return false;
            var items = TabItems(tabs);
            var isInspector = items.Any(static item => HeaderEquals(item, "Comments")) &&
                              items.Any(static item => HeaderEquals(item, "Snapshots")) &&
                              items.Any(static item => HeaderEquals(item, "Project"));
            var hasPreview = items.Any(static item => HeaderEquals(item, "PDF") || HeaderEquals(item, "Preview"));
            return isInspector && hasPreview;
        });
        if (_inspectorTabs is null) return;

        var inspectorItems = TabItems(_inspectorTabs);
        _inspectorPreviewTab = inspectorItems.FirstOrDefault(static item => HeaderEquals(item, "PDF") || HeaderEquals(item, "Preview"));
        if (_inspectorPreviewTab?.Content is not Control pdfSurface) return;

        // Detach the already-wired ContinuousPdfPreviewFeature surface before reparenting it.
        _inspectorPreviewTab.Content = BuildInspectorShortcut();
        _inspectorPreviewTab.Header = "Preview";

        _centerPdfTab.Header = "PDF Preview";
        _centerPdfTab.Content = pdfSurface;
        ToolTip.SetTip(_centerPdfTab, "Live PDF preview with thumbnails, scope, zoom and editor synchronization");

        _installed = true;
    }

    private Control BuildInspectorShortcut()
    {
        var title = new TextBlock
        {
            Text = "PDF Preview",
            FontSize = 16,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 5)
        };
        var message = new TextBlock
        {
            Text = "The live PDF preview now lives beside Editor in the main workspace.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 10)
        };
        var selected = new TextBlock
        {
            Text = _viewModel.HasDocument ? _viewModel.SelectedTitle : "No document selected",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Opacity = 0.55,
            Margin = new Thickness(0, 0, 0, 12)
        };
        var open = new Button
        {
            Content = "Open PDF Preview",
            MinWidth = 124,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        open.Click += (_, _) =>
        {
            if (_centerTabs is null || _centerPdfTab is null) return;
            _centerTabs.SelectedItem = _centerPdfTab;
            _centerPdfTab.Focus();
        };

        return new StackPanel
        {
            Margin = new Thickness(14),
            Spacing = 4,
            Children = { title, message, selected, open }
        };
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
    }

    private static bool HeaderEquals(TabItem item, string header)
        => string.Equals(item.Header?.ToString(), header, StringComparison.Ordinal);

    private static List<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is IEnumerable source)
            return source.Cast<object>().OfType<TabItem>().ToList();
        return tabs.Items.Cast<object>().OfType<TabItem>().ToList();
    }
}
