using System.Collections;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Typescribe.Desktop;

/// <summary>
/// Keeps the left project pane focused on the native Project Explorer and Bookmarks surfaces.
/// Project search is installed centrally in the top chrome; legacy Search, Collections and
/// Favorites tabs are removed so there is only one navigation model in the project pane.
/// </summary>
internal sealed class StudioNavigationPolish
{
    private static readonly HashSet<string> HiddenLeftTabs = new(StringComparer.Ordinal)
    {
        "Search",
        "Collections",
        "Favorites"
    };

    private readonly StudioWorkspaceWindow _window;
    private bool _disposed;

    private StudioNavigationPolish(StudioWorkspaceWindow window)
    {
        _window = window;
    }

    public static void Apply(StudioWorkspaceWindow window, ViewModels.WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        InspectorEditingFeature.Apply(window, viewModel);
        ReliableOutlinerFeature.Apply(window, viewModel);
        SnapshotHistoryFeature.Apply(window, viewModel);
        WorkspaceUxCompletionFeature.Apply(window, viewModel);
        CorkboardOutlinerUiRepairFeature.Apply(window, viewModel);
        CenterPdfPreviewFeature.Apply(window, viewModel);
        WorkspaceInteractionCompletenessFeature.Apply(window, viewModel);
        EventDrivenLabelEditingFeature.Apply(window, viewModel);
        OutlinerComboRenderPolish.Apply(window);

        var polish = new StudioNavigationPolish(window);
        polish.Attach();
    }

    private void Attach()
    {
        _window.Opened += WindowOpened;
        _window.LayoutUpdated += WindowLayoutUpdated;
        _window.Closed += WindowClosed;
        RemoveLegacyLeftTabs();
    }

    private void WindowOpened(object? sender, EventArgs e) => RemoveLegacyLeftTabs();

    private void WindowLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_disposed) RemoveLegacyLeftTabs();
    }

    private void RemoveLegacyLeftTabs()
    {
        var leftTabs = _window.GetVisualDescendants()
            .OfType<TabControl>()
            .FirstOrDefault(tab => GetTabItems(tab).Any(IsBinderTab));
        if (leftTabs is null) return;

        var items = GetTabItems(leftTabs);
        var filtered = items
            .Where(item => !HiddenLeftTabs.Contains(item.Header?.ToString() ?? string.Empty))
            .ToArray();

        if (filtered.Length != items.Count)
            leftTabs.ItemsSource = filtered;

        if (leftTabs.SelectedIndex < 0 && filtered.Length > 0)
            leftTabs.SelectedIndex = 0;
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= WindowOpened;
        _window.LayoutUpdated -= WindowLayoutUpdated;
        _window.Closed -= WindowClosed;
    }

    private static bool IsBinderTab(TabItem item)
        => string.Equals(item.Header?.ToString(), "Binder", StringComparison.Ordinal);

    private static List<TabItem> GetTabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is IEnumerable source)
            return source.Cast<object>().OfType<TabItem>().ToList();

        return tabs.Items.Cast<object>().OfType<TabItem>().ToList();
    }
}
