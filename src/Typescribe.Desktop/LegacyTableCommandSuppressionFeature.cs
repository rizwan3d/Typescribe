using System.Collections;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Typescribe.Desktop;

/// <summary>
/// RichEditorInsertFeature still owns non-table insert tools (UML, ABC and emoji), but its older
/// table commands predate TableEditingEngine. Hide only those duplicate table entries so every
/// visible table command routes through the unified table toolbar/context/editor surfaces.
/// </summary>
internal sealed class LegacyTableCommandSuppressionFeature
{
    private static readonly HashSet<string> LegacyTableItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "Quick Table…",
        "Edit Current Table…",
        "Table Row / Column…"
    };

    private readonly StudioWorkspaceWindow _window;
    private bool _scheduled;
    private bool _disposed;

    private LegacyTableCommandSuppressionFeature(StudioWorkspaceWindow window) => _window = window;

    public static void Apply(StudioWorkspaceWindow window)
    {
        var feature = new LegacyTableCommandSuppressionFeature(window);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        feature.Schedule();
    }

    private void OnOpened(object? sender, EventArgs e) => Schedule();
    private void OnLayoutUpdated(object? sender, EventArgs e) => Schedule();

    private void Schedule()
    {
        if (_disposed || _scheduled) return;
        _scheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scheduled = false;
            if (!_disposed) Suppress();
        }, DispatcherPriority.Background);
    }

    private void Suppress()
    {
        var menu = _window.GetVisualDescendants().OfType<Menu>().FirstOrDefault();
        if (menu?.ItemsSource is not IEnumerable source) return;
        var insert = source.Cast<object?>().OfType<MenuItem>()
            .FirstOrDefault(item => string.Equals(Normalize(item.Header?.ToString()), "Insert", StringComparison.OrdinalIgnoreCase));
        if (insert?.ItemsSource is not IEnumerable items) return;

        foreach (var item in items.Cast<object?>().OfType<MenuItem>())
        {
            if (LegacyTableItems.Contains(Normalize(item.Header?.ToString())))
                item.IsVisible = false;
        }
    }

    private static string Normalize(string? value)
        => (value ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal).Trim();

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
    }
}