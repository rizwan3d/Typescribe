using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Typescribe.Desktop;

/// <summary>
/// Keeps the live PDF page fitted completely inside the Inspector's PDF viewport.
/// It works from the rendered image's native aspect ratio, so changing Inspector size
/// automatically zooms the page in or out without clipping or manual scrolling.
/// </summary>
internal sealed class PdfAutoFitEnhancement
{
    private readonly StudioWorkspaceWindow _window;
    private TabControl? _inspectorTabs;
    private TabItem? _pdfTab;
    private ScrollViewer? _scroll;
    private Image? _image;
    private Size _lastViewport;
    private Size _lastSource;
    private bool _disposed;

    private PdfAutoFitEnhancement(StudioWorkspaceWindow window) => _window = window;

    public static void Apply(StudioWorkspaceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var feature = new PdfAutoFitEnhancement(window);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        feature.TryAttach();
    }

    private void OnOpened(object? sender, EventArgs e) => TryAttach();

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (_image is null || _scroll is null)
        {
            TryAttach();
            return;
        }

        FitPageIfNeeded();
    }

    private void TryAttach()
    {
        if (_disposed || _image is not null) return;

        _inspectorTabs = _window.GetVisualDescendants()
            .OfType<TabControl>()
            .FirstOrDefault(HasPdfTab);
        if (_inspectorTabs is null) return;

        _pdfTab = TabItems(_inspectorTabs)
            .FirstOrDefault(static item => string.Equals(item.Header?.ToString(), "PDF", StringComparison.Ordinal));
        if (_pdfTab?.Content is not Control content) return;

        _scroll = FindControl<ScrollViewer>(content);
        _image = FindControl<Image>(content);
        if (_scroll is null || _image is null) return;

        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _image.HorizontalAlignment = HorizontalAlignment.Center;
        _image.VerticalAlignment = VerticalAlignment.Center;
        _image.Stretch = Stretch.Uniform;

        _scroll.SizeChanged += OnViewportSizeChanged;
        _image.PropertyChanged += OnImagePropertyChanged;
        FitPageIfNeeded(force: true);
    }

    private void OnViewportSizeChanged(object? sender, SizeChangedEventArgs e) => FitPageIfNeeded(force: true);

    private void OnImagePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Image.SourceProperty)
            FitPageIfNeeded(force: true);
    }

    private void FitPageIfNeeded(bool force = false)
    {
        if (_scroll is null || _image?.Source is null) return;

        var viewport = new Size(Math.Max(0, _scroll.Bounds.Width - 18), Math.Max(0, _scroll.Bounds.Height - 18));
        var source = _image.Source.Size;
        if (viewport.Width <= 1 || viewport.Height <= 1 || source.Width <= 1 || source.Height <= 1) return;

        if (!force && NearlyEqual(viewport, _lastViewport) && NearlyEqual(source, _lastSource)) return;
        _lastViewport = viewport;
        _lastSource = source;

        var scale = Math.Min(viewport.Width / source.Width, viewport.Height / source.Height);
        scale = Math.Max(0.05, scale);
        _image.Width = Math.Floor(source.Width * scale);
        _image.Height = Math.Floor(source.Height * scale);
    }

    private static bool NearlyEqual(Size left, Size right)
        => Math.Abs(left.Width - right.Width) < 0.5 && Math.Abs(left.Height - right.Height) < 0.5;

    private static bool HasPdfTab(TabControl tabs)
        => TabItems(tabs).Any(static item => string.Equals(item.Header?.ToString(), "PDF", StringComparison.Ordinal));

    private static IEnumerable<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is IEnumerable source) return source.Cast<object?>().OfType<TabItem>();
        return tabs.Items.OfType<TabItem>();
    }

    private static T? FindControl<T>(Control root) where T : Control
    {
        if (root is T direct) return direct;
        if (root is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                var found = FindControl<T>(child);
                if (found is not null) return found;
            }
        }
        if (root is ContentControl content && content.Content is Control contentChild)
        {
            var found = FindControl<T>(contentChild);
            if (found is not null) return found;
        }
        if (root is Decorator decorator && decorator.Child is Control decoratedChild)
            return FindControl<T>(decoratedChild);
        return null;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        if (_scroll is not null) _scroll.SizeChanged -= OnViewportSizeChanged;
        if (_image is not null) _image.PropertyChanged -= OnImagePropertyChanged;
    }
}
