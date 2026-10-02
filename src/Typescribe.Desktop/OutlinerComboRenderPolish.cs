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
    private const double ColorColumnWidth = 52;
    private const double SwatchColumnWidth = 32;

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
        // Discovery only. Normalization is idempotent and never forces measure/arrange.
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

        // Label colors belong in the adjacent selector/swatch. Clearing these local values lets
        // the Fluent ComboBox theme own all normal, hover and focused chrome consistently.
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

        var label = cell.GetVisualDescendants()
            .OfType<ComboBox>()
            .FirstOrDefault(static candidate => candidate.IsEditable);
        var color = cell.Children.OfType<ComboBox>()
            .FirstOrDefault(static candidate => !candidate.IsEditable);
        if (label is null || color is null) return;

        // Remove the temporary wrapper introduced by the previous polish. The Fluent editable
        // ComboBox already has a complete Background border and a separate focused highlight;
        // adding another border underneath is what produced the detached left-hand brackets.
        UnwrapLabelFrame(cell, label);

        if (cell.ColumnDefinitions.Count != 3)
        {
            cell.ColumnDefinitions = new ColumnDefinitions($"*,{ColorColumnWidth},{SwatchColumnWidth}");
        }
        else
        {
            var expected = new[]
            {
                new GridLength(1, GridUnitType.Star),
                new GridLength(ColorColumnWidth),
                new GridLength(SwatchColumnWidth)
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

        label.Height = 32;
        label.MinWidth = 0;
        label.Margin = new Thickness(1);
        label.ClearValue(ComboBox.PaddingProperty);
        label.HorizontalAlignment = HorizontalAlignment.Stretch;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.VerticalContentAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 0);

        // Do not set Background, BorderBrush or BorderThickness here. Clearing local values keeps
        // the normal/hover/focus states all inside the same native Fluent control template.
        label.ClearValue(ComboBox.BackgroundProperty);
        label.ClearValue(ComboBox.BorderBrushProperty);
        label.ClearValue(ComboBox.BorderThicknessProperty);

        color.MinWidth = 0;
        color.Width = ColorColumnWidth;
        color.Height = 32;
        color.Margin = new Thickness(0, 1);
        color.HorizontalAlignment = HorizontalAlignment.Stretch;
        color.HorizontalContentAlignment = HorizontalAlignment.Center;
        color.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(color, 1);

        var swatch = cell.Children.OfType<Button>().FirstOrDefault();
        if (swatch is null) return;
        swatch.MinWidth = 0;
        swatch.Width = SwatchColumnWidth - 2;
        swatch.Height = 32;
        swatch.Margin = new Thickness(0, 1);
        swatch.HorizontalAlignment = HorizontalAlignment.Center;
        swatch.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(swatch, 2);
    }

    private static void UnwrapLabelFrame(Grid cell, ComboBox label)
    {
        var frame = cell.Children.OfType<Border>()
            .FirstOrDefault(static border => border.Classes.Contains("ux-outliner-label-frame"));
        if (frame is null) return;

        if (ReferenceEquals(frame.Child, label))
            frame.Child = null;
        cell.Children.Remove(frame);

        if (!cell.Children.Contains(label))
            cell.Children.Insert(0, label);
        Grid.SetColumn(label, 0);
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
