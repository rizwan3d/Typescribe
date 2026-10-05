using System.Collections;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop;

/// <summary>
/// Keeps the live PDF surface focused on the active editor content. Publication-only whole-book
/// scope remains available through publish/export commands, but is intentionally not exposed by
/// the editor preview.
/// </summary>
internal sealed class EditorPreviewScopeFeature
{
    private static readonly string[] EditorScopes = ["Document", "Heading"];

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private bool _scheduled;
    private bool _forcingScope;
    private bool _disposed;

    private EditorPreviewScopeFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var feature = new EditorPreviewScopeFeature(window, viewModel);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        viewModel.StateChanged += feature.OnStateChanged;
        feature.Schedule();
    }

    private void OnOpened(object? sender, EventArgs e) => Schedule();
    private void OnLayoutUpdated(object? sender, EventArgs e) => Schedule();
    private void OnStateChanged(object? sender, EventArgs e) => Schedule();

    private void Schedule()
    {
        if (_disposed || _scheduled) return;
        _scheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scheduled = false;
            if (!_disposed) EnforceEditorPreview();
        }, DispatcherPriority.Background);
    }

    private void EnforceEditorPreview()
    {
        if (_viewModel.PreviewWholeBook && !_forcingScope)
        {
            _forcingScope = true;
            try { _viewModel.SetPreviewWholeBook(false); }
            finally { _forcingScope = false; }
        }

        foreach (var checkBox in _window.GetVisualDescendants().OfType<CheckBox>())
        {
            if (string.Equals(checkBox.Content?.ToString(), "Whole book", StringComparison.OrdinalIgnoreCase))
                checkBox.IsVisible = false;
        }

        foreach (var combo in _window.GetVisualDescendants().OfType<ComboBox>())
        {
            if (!ContainsWholeBook(combo.ItemsSource)) continue;
            var selected = combo.SelectedItem?.ToString();
            combo.ItemsSource = EditorScopes;
            combo.SelectedItem = string.Equals(selected, "Heading", StringComparison.Ordinal) ? "Heading" : "Document";
        }
    }

    private static bool ContainsWholeBook(IEnumerable? source)
    {
        if (source is null) return false;
        foreach (var item in source)
            if (string.Equals(item?.ToString(), "Whole Book", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        _viewModel.StateChanged -= OnStateChanged;
    }
}