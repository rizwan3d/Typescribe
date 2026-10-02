using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Typescribe.Desktop;

/// <summary>
/// Keeps the Outliner visually stable after combo-box popups close and makes the custom
/// color picker large enough for its action buttons to remain visible.
/// </summary>
internal sealed class OutlinerComboRenderPolish
{
    private readonly StudioWorkspaceWindow _window;
    private readonly HashSet<ComboBox> _hookedCombos = [];
    private Grid? _outlinerSurface;
    private bool _disposed;

    private OutlinerComboRenderPolish(StudioWorkspaceWindow window)
    {
        _window = window;
    }

    public static void Apply(StudioWorkspaceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var feature = new OutlinerComboRenderPolish(window);
        window.Opened += feature.WindowOpened;
        window.LayoutUpdated += feature.WindowLayoutUpdated;
        window.Deactivated += feature.WindowDeactivated;
        window.Activated += feature.WindowActivated;
        window.Closed += feature.WindowClosed;
        feature.Scan();
    }

    private void WindowOpened(object? sender, EventArgs e)
    {
        Scan();
        ResizeCustomColorWindow();
    }

    private void WindowLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_disposed) Scan();
    }

    private void WindowDeactivated(object? sender, EventArgs e)
    {
        if (_disposed) return;
        Dispatcher.UIThread.Post(ResizeCustomColorWindow, DispatcherPriority.Background);
    }

    private void WindowActivated(object? sender, EventArgs e)
    {
        if (_disposed) return;
        Dispatcher.UIThread.Post(() =>
        {
            Scan();
            RerenderOutliner();
        }, DispatcherPriority.Background);
    }

    private void Scan()
    {
        if (_disposed) return;

        _outlinerSurface ??= _window.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(static grid => grid.Classes.Contains("reliable-outliner"));

        if (_outlinerSurface is null) return;

        foreach (var combo in _outlinerSurface.GetVisualDescendants().OfType<ComboBox>().ToArray())
        {
            if (!_hookedCombos.Add(combo)) continue;
            combo.PropertyChanged += ComboPropertyChanged;
        }

        foreach (var stale in _hookedCombos.Where(combo => !combo.IsAttachedToVisualTree()).ToArray())
        {
            stale.PropertyChanged -= ComboPropertyChanged;
            _hookedCombos.Remove(stale);
        }
    }

    private void ComboPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_disposed || sender is not ComboBox combo ||
            e.Property != ComboBox.IsDropDownOpenProperty || combo.IsDropDownOpen)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            combo.InvalidateMeasure();
            combo.InvalidateArrange();
            combo.InvalidateVisual();

            var row = combo.GetVisualAncestors().OfType<Grid>().FirstOrDefault();
            if (row is not null)
            {
                row.InvalidateMeasure();
                row.InvalidateArrange();
                row.InvalidateVisual();
            }

            RerenderOutliner();
        }, DispatcherPriority.Background);
    }

    private void RerenderOutliner()
    {
        var surface = _outlinerSurface;
        if (surface is null || !surface.IsAttachedToVisualTree())
        {
            _outlinerSurface = null;
            Scan();
            surface = _outlinerSurface;
        }

        if (surface is null) return;
        surface.InvalidateMeasure();
        surface.InvalidateArrange();
        surface.InvalidateVisual();
    }

    private static void ResizeCustomColorWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        foreach (var dialog in desktop.Windows.Where(static candidate =>
                     candidate.IsVisible &&
                     string.Equals(candidate.Title, "Select Custom Color", StringComparison.Ordinal)))
        {
            dialog.SizeToContent = SizeToContent.Manual;
            dialog.Width = Math.Max(dialog.Width, 535);
            dialog.Height = Math.Max(dialog.Height, 535);
            dialog.MinWidth = Math.Max(dialog.MinWidth, 500);
            dialog.MinHeight = Math.Max(dialog.MinHeight, 500);
        }
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= WindowOpened;
        _window.LayoutUpdated -= WindowLayoutUpdated;
        _window.Deactivated -= WindowDeactivated;
        _window.Activated -= WindowActivated;
        _window.Closed -= WindowClosed;

        foreach (var combo in _hookedCombos)
            combo.PropertyChanged -= ComboPropertyChanged;
        _hookedCombos.Clear();
    }
}
