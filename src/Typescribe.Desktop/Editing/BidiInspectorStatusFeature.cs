using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Keeps the Typography inspector's RTL help text synchronized with the native in-flow BiDi
/// editor. This is intentionally separate from the inspector's formatting persistence logic.
/// </summary>
internal sealed class BidiInspectorStatusFeature
{
    private readonly StudioWorkspaceWindow _window;
    private bool _updated;

    private BidiInspectorStatusFeature(StudioWorkspaceWindow window)
        => _window = window;

    public static void Apply(StudioWorkspaceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var feature = new BidiInspectorStatusFeature(window);
        window.Opened += feature.WindowOpened;
        window.LayoutUpdated += feature.WindowLayoutUpdated;
        window.Closed += feature.WindowClosed;
        feature.TryUpdate();
    }

    private void WindowOpened(object? sender, EventArgs e) => TryUpdate();
    private void WindowLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_updated) TryUpdate();
    }

    private void TryUpdate()
    {
        var note = _window.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(static text => text.Text?.StartsWith("RTL note:", StringComparison.Ordinal) == true);
        if (note is null) return;

        note.Text = "RTL editing is active: Auto uses Unicode first-strong direction; Arabic, Urdu and Persian lines use the native BiDi/shaping editor. Mirrored page rulers and frame-level tab controls are part of the paged-layout work in #43.";
        _updated = true;
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _window.Opened -= WindowOpened;
        _window.LayoutUpdated -= WindowLayoutUpdated;
        _window.Closed -= WindowClosed;
    }
}
