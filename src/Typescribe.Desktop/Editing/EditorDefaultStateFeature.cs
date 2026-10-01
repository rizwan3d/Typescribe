using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Applies one-time editor defaults after the professional editor toolbar is created.
/// Analysis starts disabled, but the user can turn it on normally afterward.
/// </summary>
internal sealed class EditorDefaultStateFeature
{
    private readonly StudioWorkspaceWindow _window;
    private bool _applied;
    private bool _disposed;

    private EditorDefaultStateFeature(StudioWorkspaceWindow window)
    {
        _window = window;
    }

    public static void Apply(StudioWorkspaceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var feature = new EditorDefaultStateFeature(window);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        feature.QueueApply();
    }

    private void OnOpened(object? sender, EventArgs e) => QueueApply();

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_applied) QueueApply();
    }

    private void QueueApply()
    {
        if (_disposed || _applied) return;
        Dispatcher.UIThread.Post(TryApply, DispatcherPriority.Background);
    }

    private void TryApply()
    {
        if (_disposed || _applied) return;

        var analysisToggle = _window.GetVisualDescendants()
            .OfType<ToggleButton>()
            .FirstOrDefault(button => string.Equals(
                button.Content?.ToString(),
                "Analysis",
                StringComparison.OrdinalIgnoreCase));
        if (analysisToggle is null) return;

        // Setting IsChecked invokes ProfessionalEditorSuite's existing change handler,
        // which disables its analysis pass and clears analysis diagnostics.
        analysisToggle.IsChecked = false;

        foreach (var editor in _window.GetVisualDescendants().OfType<ManuscriptEditor>())
        {
            editor.SpellIndicatorsEnabled = false;
            editor.SetSpellingDiagnostics([]);
        }

        _applied = true;
        _window.LayoutUpdated -= OnLayoutUpdated;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
    }
}
