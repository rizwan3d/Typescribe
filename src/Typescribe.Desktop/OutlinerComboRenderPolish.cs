using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
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
            NormalizeAllLabelCells();
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
            NormalizeLabelCell(combo);
            if (!_hookedCombos.Add(combo)) continue;
            combo.DropDownClosed += ComboDropDownClosed;
            combo.LostFocus += ComboLostFocus;
        }

        foreach (var stale in _hookedCombos.Where(combo => !combo.IsAttachedToVisualTree()).ToArray())
        {
            stale.DropDownClosed -= ComboDropDownClosed;
            stale.LostFocus -= ComboLostFocus;
            _hookedCombos.Remove(stale);
        }
    }

    private void ComboDropDownClosed(object? sender, EventArgs e)
    {
        if (_disposed || sender is not ComboBox combo) return;
        QueueStableRerender(combo);
    }

    private void ComboLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_disposed || sender is not ComboBox combo || combo.IsDropDownOpen) return;
        QueueStableRerender(combo);
    }

    private void QueueStableRerender(ComboBox combo)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !combo.IsAttachedToVisualTree()) return;
            NormalizeLabelCell(combo);
            InvalidateComboAndRow(combo);
            RerenderOutliner();

            // Editable ComboBox templates settle one dispatcher turn after the popup closes.
            // A render-priority pass removes the clipped/half-border artifact seen in Label / Color.
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || !combo.IsAttachedToVisualTree()) return;
                NormalizeLabelCell(combo);
                InvalidateComboAndRow(combo);
                RerenderOutliner();
            }, DispatcherPriority.Render);
        }, DispatcherPriority.Background);
    }

    private static void InvalidateComboAndRow(ComboBox combo)
    {
        combo.InvalidateMeasure();
        combo.InvalidateArrange();
        combo.InvalidateVisual();

        var row = combo.GetVisualAncestors()
            .OfType<Grid>()
            .FirstOrDefault(grid => !grid.Classes.Contains("ux-outliner-label-cell"));
        if (row is null) return;
        row.InvalidateMeasure();
        row.InvalidateArrange();
        row.InvalidateVisual();
    }

    private void NormalizeAllLabelCells()
    {
        var surface = _outlinerSurface;
        if (surface is null) return;
        foreach (var combo in surface.GetVisualDescendants().OfType<ComboBox>())
            NormalizeLabelCell(combo);
    }

    private static void NormalizeLabelCell(ComboBox combo)
    {
        var cell = combo.GetVisualAncestors()
            .OfType<Grid>()
            .FirstOrDefault(static grid => grid.Classes.Contains("ux-outliner-label-cell"));
        if (cell is null) return;

        var combos = cell.Children.OfType<ComboBox>().ToArray();
        if (combos.Length < 2) return;

        var label = combos.FirstOrDefault(static candidate => candidate.IsEditable);
        var color = combos.FirstOrDefault(static candidate => !candidate.IsEditable);
        if (label is null || color is null) return;

        // Keep the three controls inside the Label / Color column with predictable spacing.
        cell.ColumnDefinitions = new ColumnDefinitions("*,108,38");
        cell.ColumnSpacing = 4;
        cell.HorizontalAlignment = HorizontalAlignment.Stretch;
        cell.ClipToBounds = false;

        label.Height = 32;
        label.MinWidth = 150;
        label.Margin = new Thickness(1, 1, 0, 1);
        label.HorizontalAlignment = HorizontalAlignment.Stretch;
        label.VerticalContentAlignment = VerticalAlignment.Center;

        // The editable ComboBox template does not render reliably when its outer background and
        // border are replaced at runtime. Use the native theme and leave color indication to the
        // dedicated color selector/swatch beside it.
        label.ClearValue(ComboBox.BackgroundProperty);
        label.ClearValue(ComboBox.BorderBrushProperty);
        label.ClearValue(ComboBox.BorderThicknessProperty);

        color.Width = 108;
        color.Height = 32;
        color.Margin = new Thickness(0, 1);
        color.HorizontalAlignment = HorizontalAlignment.Stretch;
        color.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(color, 1);

        var swatch = cell.Children.OfType<Button>().FirstOrDefault();
        if (swatch is not null)
        {
            swatch.Width = 34;
            swatch.Height = 32;
            swatch.Margin = new Thickness(0, 1);
            swatch.HorizontalAlignment = HorizontalAlignment.Center;
            swatch.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(swatch, 2);
        }

        cell.InvalidateMeasure();
        cell.InvalidateArrange();
        cell.InvalidateVisual();
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
        {
            combo.DropDownClosed -= ComboDropDownClosed;
            combo.LostFocus -= ComboLostFocus;
        }
        _hookedCombos.Clear();
    }
}
