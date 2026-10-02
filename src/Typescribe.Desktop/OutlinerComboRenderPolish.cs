using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
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
        // Discovery only. Normalization below is idempotent and never forces measure/arrange.
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

        // The label feature may apply a semantic tint to the editable ComboBox. Avalonia's
        // editable template draws that outer tint in disconnected pieces around its inner editor.
        // Keep the ComboBox chrome transparent and let the dedicated frame draw one clean border.
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

                combo.Background = Brushes.Transparent;
                combo.BorderBrush = Brushes.Transparent;
                combo.BorderThickness = new Thickness(0);
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
        // Repaint after the popup closes without forcing a new measure/arrange cycle.
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
        labelCell?.Children.OfType<Border>()
            .FirstOrDefault(static border => border.Classes.Contains("ux-outliner-label-frame"))
            ?.InvalidateVisual();

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

        var label = cell.GetVisualDescendants()
            .OfType<ComboBox>()
            .FirstOrDefault(static candidate => candidate.IsEditable);
        var color = cell.Children.OfType<ComboBox>()
            .FirstOrDefault(static candidate => !candidate.IsEditable);
        if (label is null || color is null) return;

        // Keep the whole Label / Color editor compact enough for the 230px legacy host width.
        if (cell.ColumnDefinitions.Count != 3)
        {
            cell.ColumnDefinitions = new ColumnDefinitions("*,92,28");
        }
        else
        {
            var expected = new[]
            {
                new GridLength(1, GridUnitType.Star),
                new GridLength(92),
                new GridLength(28)
            };
            for (var index = 0; index < expected.Length; index++)
            {
                if (cell.ColumnDefinitions[index].Width != expected[index])
                    cell.ColumnDefinitions[index].Width = expected[index];
            }
        }

        cell.ColumnSpacing = 4;
        cell.HorizontalAlignment = HorizontalAlignment.Stretch;
        cell.ClipToBounds = true;

        var frame = EnsureLabelFrame(cell, label);
        frame.Height = 32;
        frame.MinWidth = 0;
        frame.Margin = new Thickness(1);
        frame.HorizontalAlignment = HorizontalAlignment.Stretch;
        frame.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(frame, 0);

        label.Height = 30;
        label.MinWidth = 0;
        label.Margin = new Thickness(0);
        label.Padding = new Thickness(7, 2, 4, 2);
        label.HorizontalAlignment = HorizontalAlignment.Stretch;
        label.VerticalAlignment = VerticalAlignment.Stretch;
        label.VerticalContentAlignment = VerticalAlignment.Center;
        label.Background = Brushes.Transparent;
        label.BorderBrush = Brushes.Transparent;
        label.BorderThickness = new Thickness(0);

        color.MinWidth = 0;
        color.Width = double.NaN;
        color.Height = 32;
        color.Margin = new Thickness(0, 1);
        color.HorizontalAlignment = HorizontalAlignment.Stretch;
        color.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(color, 1);

        var swatch = cell.Children.OfType<Button>().FirstOrDefault();
        if (swatch is null) return;
        swatch.MinWidth = 0;
        swatch.Width = 26;
        swatch.Height = 32;
        swatch.Margin = new Thickness(0, 1);
        swatch.HorizontalAlignment = HorizontalAlignment.Center;
        swatch.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(swatch, 2);
    }

    private static Border EnsureLabelFrame(Grid cell, ComboBox label)
    {
        var frame = cell.Children.OfType<Border>()
            .FirstOrDefault(static border => border.Classes.Contains("ux-outliner-label-frame"));
        if (frame is not null) return frame;

        // Reparent once: the wrapper owns the visual border, while the editable ComboBox owns only
        // text editing and its drop-down button. This avoids the split/cut border seen in the
        // editable Avalonia ComboBox template when runtime colors are applied.
        if (cell.Children.Contains(label))
            cell.Children.Remove(label);

        frame = new Border
        {
            Child = label,
            BorderBrush = new SolidColorBrush(Color.Parse("#5A5A5F")),
            Background = new SolidColorBrush(Color.Parse("#202020")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true
        };
        frame.Classes.Add("ux-outliner-label-frame");
        cell.Children.Insert(0, frame);

        var normal = new SolidColorBrush(Color.Parse("#5A5A5F"));
        var focused = new SolidColorBrush(Color.Parse("#7A7A80"));
        label.GotFocus += (_, _) => frame.BorderBrush = focused;
        label.LostFocus += (_, _) => frame.BorderBrush = normal;
        return frame;
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
