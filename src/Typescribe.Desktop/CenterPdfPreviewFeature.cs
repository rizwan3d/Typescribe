using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop;

/// <summary>
/// Adds a real PDF preview beside Editor while leaving the Inspector preview intact.
/// Both surfaces consume WorkspaceViewModel.LivePreviewPdfPath, so they always display
/// the same generated PDF instead of one surface being replaced by a shortcut.
/// </summary>
internal sealed class CenterPdfPreviewFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly PdfPreviewRenderer _renderer = new();
    private readonly Image _pageImage = new()
    {
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top
    };
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.68 };
    private readonly Button _previous = new() { Content = "‹", Width = 32, Height = 28 };
    private readonly Button _next = new() { Content = "›", Width = 32, Height = 28 };
    private readonly Button _zoomOut = new() { Content = "−", Width = 32, Height = 28 };
    private readonly Button _zoomIn = new() { Content = "+", Width = 32, Height = 28 };
    private readonly ScrollViewer _scroll = new()
    {
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
    };

    private TabControl? _centerTabs;
    private TabItem? _centerPdfTab;
    private TabItem? _inspectorPreviewTab;
    private CancellationTokenSource? _renderCts;
    private PdfPreviewPage? _renderedPage;
    private string? _pdfPath;
    private long _version = -1;
    private int _pageIndex;
    private int _pageCount;
    private double _zoom = 1.0;
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
        viewModel.StateChanged += feature.OnStateChanged;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        TryInstall();
        _ = ReloadIfNeededAsync(force: true);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_disposed || !_window.IsVisible) return;
        if (!_installed) TryInstall();
        NormalizeInspectorPreviewHeader();
    }

    private void TryInstall()
    {
        if (_installed || _disposed) return;

        var allTabs = _window.GetVisualDescendants().OfType<TabControl>().ToArray();
        _centerTabs = allTabs.FirstOrDefault(tabs =>
        {
            var items = TabItems(tabs);
            return items.Any(item => HeaderEquals(item, "Editor")) &&
                   items.Any(item => HeaderEquals(item, "Corkboard")) &&
                   items.Any(item => HeaderEquals(item, "Outliner"));
        });
        if (_centerTabs is null) return;

        var centerItems = TabItems(_centerTabs);
        _centerPdfTab = centerItems.FirstOrDefault(item => HeaderEquals(item, "PDF Preview"))
            ?? centerItems.FirstOrDefault(item => HeaderEquals(item, "Preview"));
        if (_centerPdfTab is null) return;

        _centerPdfTab.Header = "PDF Preview";
        _centerPdfTab.Content = BuildSurface();
        ToolTip.SetTip(_centerPdfTab, "The same generated PDF shown in Inspector Preview");
        _installed = true;
        NormalizeInspectorPreviewHeader();
    }

    private Control BuildSurface()
    {
        var toolbar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(7, 5),
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var control in new Control[]
                 {
                     _previous, _pageLabel, _next, _zoomOut, _zoomIn, _status
                 })
        {
            control.Margin = new Thickness(3, 0);
            toolbar.Children.Add(control);
        }

        var pageHost = new Border
        {
            Background = Brushes.White,
            Margin = new Thickness(18),
            Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Child = _pageImage
        };
        _scroll.Content = pageHost;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(toolbar);
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_scroll);

        _previous.Click += async (_, _) =>
        {
            if (_pageIndex <= 0) return;
            _pageIndex--;
            await RenderCurrentPageAsync();
        };
        _next.Click += async (_, _) =>
        {
            if (_pageCount > 0 && _pageIndex >= _pageCount - 1) return;
            _pageIndex++;
            await RenderCurrentPageAsync();
        };
        _zoomOut.Click += (_, _) => ChangeZoom(-0.1);
        _zoomIn.Click += (_, _) => ChangeZoom(0.1);

        UpdatePageUi();
        return root;
    }

    private void NormalizeInspectorPreviewHeader()
    {
        foreach (var tabs in _window.GetVisualDescendants().OfType<TabControl>())
        {
            if (ReferenceEquals(tabs, _centerTabs)) continue;
            var items = TabItems(tabs);
            if (!items.Any(item => HeaderEquals(item, "Comments")) ||
                !items.Any(item => HeaderEquals(item, "Snapshots")))
                continue;

            var preview = items.FirstOrDefault(item => HeaderEquals(item, "PDF") || HeaderEquals(item, "Preview"));
            if (preview is null) continue;
            _inspectorPreviewTab = preview;
            preview.Header = "Preview";
            ToolTip.SetTip(preview, "Live PDF preview of the same generated document shown beside Editor");
            return;
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !_installed) return;
            _status.Text = _viewModel.IsLivePreviewBuilding
                ? "Compiling…"
                : _viewModel.LivePreviewError is not null ? "Preview error" : "Live PDF";
            _ = ReloadIfNeededAsync();
        }, DispatcherPriority.Background);
    }

    private async Task ReloadIfNeededAsync(bool force = false)
    {
        var path = _viewModel.LivePreviewPdfPath;
        var version = _viewModel.LivePreviewVersion;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            if (_viewModel.IsLivePreviewBuilding) _status.Text = "Compiling…";
            else if (_viewModel.LivePreviewError is not null) _status.Text = "Preview error";
            else _status.Text = "Waiting for PDF…";
            return;
        }

        if (!force && version == _version && string.Equals(path, _pdfPath, StringComparison.Ordinal)) return;
        _pdfPath = path;
        _version = version;
        _pageIndex = 0;
        await RenderCurrentPageAsync();
    }

    private async Task RenderCurrentPageAsync()
    {
        if (string.IsNullOrWhiteSpace(_pdfPath) || !File.Exists(_pdfPath)) return;
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = new CancellationTokenSource();
        var token = _renderCts.Token;
        _status.Text = "Rendering…";

        try
        {
            var page = await _renderer.RenderAsync(_pdfPath, _pageIndex, token);
            if (token.IsCancellationRequested)
            {
                page.Dispose();
                return;
            }

            _renderedPage?.Dispose();
            _renderedPage = page;
            _pageIndex = page.PageIndex;
            _pageCount = page.PageCount;
            _pageImage.Source = page.Bitmap;
            ApplyZoom();
            UpdatePageUi();
            _status.Text = "Live PDF";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _status.Text = "Could not render: " + ex.Message;
        }
    }

    private void ChangeZoom(double delta)
    {
        _zoom = Math.Clamp(Math.Round(_zoom + delta, 2), 0.4, 2.5);
        ApplyZoom();
        UpdatePageUi();
    }

    private void ApplyZoom()
    {
        _pageImage.Width = 820 * _zoom;
        _pageImage.MaxWidth = double.PositiveInfinity;
    }

    private void UpdatePageUi()
    {
        _pageLabel.Text = _pageCount > 0
            ? $"Page {_pageIndex + 1} / {_pageCount} · {_zoom:P0}"
            : $"Page — · {_zoom:P0}";
        _previous.IsEnabled = _pageCount > 0 && _pageIndex > 0;
        _next.IsEnabled = _pageCount > 0 && _pageIndex < _pageCount - 1;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderedPage?.Dispose();
        _viewModel.StateChanged -= OnStateChanged;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
    }

    private static bool HeaderEquals(TabItem item, string header)
        => string.Equals(item.Header?.ToString(), header, StringComparison.OrdinalIgnoreCase);

    private static List<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is IEnumerable source)
            return source.Cast<object?>().OfType<TabItem>().ToList();
        return tabs.Items.Cast<object?>().OfType<TabItem>().ToList();
    }
}
