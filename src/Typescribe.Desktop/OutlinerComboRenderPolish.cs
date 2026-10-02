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
    private readonly HashSet<ComboBox> _pendingTintCleanups = [];
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
        // LayoutUpdated is discovery-only. Any changes made by Scan are idempotent so they cannot
        // feed another measure/arrange pass forever.
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
            NormalizeLabelCell(combo);
            if (!_hookedCombos.Add(combo)) continue;
            combo.DropDownClosed += ComboDropDownClosed;
            combo.LostFocus += ComboLostFocus;
            combo.PropertyChanged += ComboPropertyChanged;
        }

        foreach (var stale in _hookedCombos.Where(combo => !combo.IsAttachedToVisualTree()).ToArray())
        {
            stale.DropDownClosed -= ComboDropDownClosed;
            stale.LostFocus -= ComboLostFocus;
            stale.PropertyChanged -= ComboPropertyChanged;
            _pendingTintCleanups.Remove(stale);
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

    private void ComboPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_disposed || sender is not ComboBox combo || !combo.IsEditable || !IsOutlinerLabelCombo(combo))
            return;

        if (e.Property != ComboBox.BackgroundProperty &&
            e.Property != ComboBox.BorderBrushProperty &&
            e.Property != ComboBox.BorderThicknessProperty)
            return;

        // EventDrivenLabelEditingFeature can apply a color tint after this polish has already
        // normalized the cell. Editable ComboBox templates expose that outer tint as clipped
        // corner fragments, so remove it as soon as it is reapplied.
        QueueTintCleanup(combo);
    }

    private void QueueTintCleanup(ComboBox combo)
    {
        if (!_pendingTintCleanups.Add(combo)) return;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_disposed || !combo.IsAttachedToVisualTree()) return;

                combo.ClearValue(ComboBox.BackgroundProperty);
                combo.ClearValue(ComboBox.BorderBrushProperty);
                combo.ClearValue(ComboBox.BorderThicknessProperty);
                combo.InvalidateVisual();

                var cell = combo.GetVisualAncestors()
                    .OfType<Grid>()
                    .FirstOrDefault(static grid => grid.Classes.Contains("ux-outliner-label-cell"));
                cell?.InvalidateVisual();
            }
            finally
            {
                _pendingTintCleanups.Remove(combo);
            }
        }, DispatcherPriority.Render);
    }

    private static bool IsOutlinerLabelCombo(ComboBox combo)
        => combo.GetVisualAncestors()
            .OfType<Grid>()
            .Any(static grid => grid.Classes.Contains("ux-outliner-label-cell"));

    private void QueueStableRerender(ComboBox combo)
    {
        // Repaint after the popup closes without forcing a new measure/arrange cycle. Forcing
        // layout from LayoutUpdated was the source of Avalonia's "Infinite layout loop detected".
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !combo.IsAttachedToVisualTree()) return;
            NormalizeLabelCell(combo);
            InvalidateComboAndRowVisuals(combo);
            RerenderOutliner();
        }, DispatcherPriority.Render);
    }

    private static void InvalidateComboAndRowVisuals(ComboBox combo)
    {
        combo.InvalidateVisual();

        var labelCell = combo.GetVisualAncestors()
            .OfType<Grid>()
            .FirstOrDefault(static grid => grid.Classes.Contains("ux-outliner-label-cell"));
        labelCell?.InvalidateVisual();

        var row = combo.GetVisualAncestors()
            .OfType<Grid>()
            .FirstOrDefault(grid => !grid.Classes.Contains("ux-outliner-label-cell"));
        row?.InvalidateVisual();
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

        // Never replace ColumnDefinitions on every LayoutUpdated. Mutate the existing definitions
        // only when necessary so this normalization is safe to run during layout discovery.
        if (cell.ColumnDefinitions.Count != 3)
        {
            cell.ColumnDefinitions = new ColumnDefinitions("*,108,38");
        }
        else
        {
            var expected = new[]
            {
                new GridLength(1, GridUnitType.Star),
                new GridLength(108),
                new GridLength(38)
            };
            for (var index = 0; index < expected.Length; index++)
            {
                if (cell.ColumnDefinitions[index].Width != expected[index])
                    cell.ColumnDefinitions[index].Width = expected[index];
            }
        }

        cell.ColumnSpacing = 4;
        cell.HorizontalAlignment = HorizontalAlignment.Stretch;
        cell.ClipToBounds = false;

        label.Height = 32;
        label.MinWidth = 150;
        label.Margin = new Thickness(1, 1, 0, 1);
        label.HorizontalAlignment = HorizontalAlignment.Stretch;
        label.VerticalContentAlignment = VerticalAlignment.Center;

        // Keep the editable label control on the native Avalonia theme. The adjacent color selector
        // and swatch carry the color information without corrupting the editable ComboBox border.
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
        if (swatch is null) return;
        swatch.Width = 34;
        swatch.Height = 32;
        swatch.Margin = new Thickness(0, 1);
        swatch.HorizontalAlignment = HorizontalAlignment.Center;
        swatch.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(swatch, 2);
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

        surface?.InvalidateVisual();
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
            if (double.IsNaN(dialog.Width) || dialog.Width < 535) dialog.Width = 535;
            if (double.IsNaN(dialog.Height) || dialog.Height < 535) dialog.Height = 535;
            if (dialog.MinWidth < 500) dialog.MinWidth = 500;
            if (dialog.MinHeight < 500) dialog.MinHeight = 500;
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
            combo.PropertyChanged -= ComboPropertyChanged;
        }
        _pendingTintCleanups.Clear();
        _hookedCombos.Clear();
    }
}
