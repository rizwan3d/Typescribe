using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Typescribe.Desktop;

/// <summary>
/// Keeps the live PDF page fitted completely inside the Inspector until the user explicitly
/// zooms. Manual +/- zoom is then applied relative to the fitted page size and survives page
/// changes and Inspector resizes without being overwritten by the auto-fit pass.
/// </summary>
internal sealed class PdfAutoFitEnhancement
{
    private readonly StudioWorkspaceWindow _window;
    private TabControl? _inspectorTabs;
    private TabItem? _pdfTab;
    private ScrollViewer? _scroll;
    private Image? _image;
    private Button? _zoomOutButton;
    private Button? _zoomInButton;
    private Size _lastViewport;
    private Size _lastSource;
    private double _fitWidth;
    private double _fitHeight;
    private double _manualScale = 1.0;
    private bool _manualZoom;
    private bool _disposed;

    private PdfAutoFitEnhancement(StudioWorkspaceWindow window) => _window = window;

    public static void Apply(StudioWorkspaceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        DocumentStructureFeature.Apply(window);
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
            .FirstOrDefault(IsPreviewTab);
        if (_pdfTab?.Content is not Control content) return;

        _scroll = FindControl<ScrollViewer>(content);
        _image = FindControl<Image>(content);
        if (_scroll is null || _image is null) return;

        _zoomOutButton = FindButton(content, "−");
        _zoomInButton = FindButton(content, "+");
        if (_zoomOutButton is not null) _zoomOutButton.Click += OnZoomOutClicked;
        if (_zoomInButton is not null) _zoomInButton.Click += OnZoomInClicked;

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

    private void OnZoomOutClicked(object? sender, RoutedEventArgs e) => EnterManualZoom(-0.1);

    private void OnZoomInClicked(object? sender, RoutedEventArgs e) => EnterManualZoom(0.1);

    private void EnterManualZoom(double delta)
    {
        _manualZoom = true;
        _manualScale = Math.Clamp(_manualScale + delta, 0.5, 2.5);
        if (_scroll is not null)
        {
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        ApplyManualZoom();
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
        _fitWidth = Math.Floor(source.Width * scale);
        _fitHeight = Math.Floor(source.Height * scale);

        if (_manualZoom)
        {
            ApplyManualZoom();
            return;
        }

        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _image.Width = _fitWidth;
        _image.Height = _fitHeight;
    }

    private void ApplyManualZoom()
    {
        if (_image is null || _fitWidth <= 0 || _fitHeight <= 0) return;
        _image.Width = Math.Floor(_fitWidth * _manualScale);
        _image.Height = Math.Floor(_fitHeight * _manualScale);
    }

    private static bool NearlyEqual(Size left, Size right)
        => Math.Abs(left.Width - right.Width) < 0.5 && Math.Abs(left.Height - right.Height) < 0.5;

    private static bool HasPdfTab(TabControl tabs)
    {
        var items = TabItems(tabs).ToArray();
        return items.Any(IsPreviewTab) &&
               items.Any(static item => string.Equals(item.Header?.ToString(), "Comments", StringComparison.Ordinal)) &&
               items.Any(static item => string.Equals(item.Header?.ToString(), "Snapshots", StringComparison.Ordinal));
    }

    private static bool IsPreviewTab(TabItem item)
    {
        var header = item.Header?.ToString();
        return string.Equals(header, "PDF", StringComparison.Ordinal) ||
               string.Equals(header, "Preview", StringComparison.Ordinal);
    }

    private static IEnumerable<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is IEnumerable source) return source.Cast<object?>().OfType<TabItem>();
        return tabs.Items.OfType<TabItem>();
    }

    private static Button? FindButton(Control root, string content)
    {
        if (root is Button direct && string.Equals(direct.Content?.ToString(), content, StringComparison.Ordinal))
            return direct;

        if (root is Panel panel)
        {
            foreach (var child in panel.Children.OfType<Control>())
            {
                var found = FindButton(child, content);
                if (found is not null) return found;
            }
        }

        if (root is ContentControl contentControl && contentControl.Content is Control contentChild)
        {
            var found = FindButton(contentChild, content);
            if (found is not null) return found;
        }

        if (root is Decorator decorator && decorator.Child is Control decoratedChild)
            return FindButton(decoratedChild, content);

        return null;
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
        if (_zoomOutButton is not null) _zoomOutButton.Click -= OnZoomOutClicked;
        if (_zoomInButton is not null) _zoomInButton.Click -= OnZoomInClicked;
    }
}
