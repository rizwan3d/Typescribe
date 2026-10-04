using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Applies one-time editor defaults after the professional editor toolbar is created.
/// Analysis starts disabled, but the user can turn it on normally afterward.
/// Also keeps editor scrollbars consistent with the Wrap preference.
/// </summary>
internal sealed class EditorDefaultStateFeature
{
    private readonly StudioWorkspaceWindow _window;
    private ToggleButton? _wrapToggle;
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

        var toggles = _window.GetVisualDescendants().OfType<ToggleButton>().ToArray();
        var analysisToggle = toggles.FirstOrDefault(button => string.Equals(
            button.Content?.ToString(),
            "Analysis",
            StringComparison.OrdinalIgnoreCase));
        var wrapToggle = toggles.FirstOrDefault(button => string.Equals(
            button.Content?.ToString(),
            "Wrap",
            StringComparison.OrdinalIgnoreCase));
        if (analysisToggle is null || wrapToggle is null) return;

        // Setting IsChecked invokes ProfessionalEditorSuite's existing change handler,
        // which disables its analysis pass and clears analysis diagnostics.
        analysisToggle.IsChecked = false;

        foreach (var editor in _window.GetVisualDescendants().OfType<ManuscriptEditor>())
        {
            editor.SpellIndicatorsEnabled = false;
            editor.SetSpellingDiagnostics([]);
        }

        _wrapToggle = wrapToggle;
        _wrapToggle.IsCheckedChanged += OnWrapToggleChanged;
        ApplyEditorScrollBars(_wrapToggle.IsChecked == true);

        _applied = true;
        _window.LayoutUpdated -= OnLayoutUpdated;
    }

    private void OnWrapToggleChanged(object? sender, EventArgs e)
        => ApplyEditorScrollBars(_wrapToggle?.IsChecked == true);

    private void ApplyEditorScrollBars(bool wordWrap)
    {
        foreach (var editor in _window.GetVisualDescendants().OfType<ManuscriptEditor>())
        {
            // Long documents must always remain vertically scrollable. Horizontal scrolling
            // is useful only when soft wrapping is disabled; otherwise it adds dead chrome.
            editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            editor.HorizontalScrollBarVisibility = wordWrap
                ? ScrollBarVisibility.Disabled
                : ScrollBarVisibility.Auto;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        if (_wrapToggle is not null)
            _wrapToggle.IsCheckedChanged -= OnWrapToggleChanged;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
    }
}
