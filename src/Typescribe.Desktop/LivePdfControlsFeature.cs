using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop;

/// <summary>
/// Adds explicit document / heading / whole-book controls to the existing raster PDF preview.
/// The view model remains the source of truth for preview scope and compilation debouncing.
/// </summary>
internal sealed class LivePdfControlsFeature
{
    private static readonly string[] ScopeItems = ["Document", "Heading", "Whole Book"];

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private ComboBox? _scope;
    private ComboBox? _heading;
    private CheckBox? _legacyWholeBook;
    private TextBlock? _legacyScopeLabel;
    private OutlineItemViewModel[] _headings = [];
    private bool _installed;
    private bool _installScheduled;
    private bool _updateScheduled;
    private bool _updating;
    private bool _disposed;

    private LivePdfControlsFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var feature = new LivePdfControlsFeature(window, viewModel);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        viewModel.StateChanged += feature.OnViewModelStateChanged;
        feature.ScheduleInstall();
    }

    private void OnOpened(object? sender, EventArgs e) => ScheduleInstall();

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_installed) ScheduleInstall();
    }

    private void ScheduleInstall()
    {
        if (_disposed || _installed || _installScheduled) return;
        _installScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _installScheduled = false;
            if (!_disposed) TryInstall();
        }, DispatcherPriority.Background);
    }

    private void TryInstall()
    {
        if (_disposed || _installed) return;

        var inspectorTabs = _window.GetVisualDescendants()
            .OfType<TabControl>()
            .FirstOrDefault(HasPdfTab);
        if (inspectorTabs is null) return;

        var pdfTab = TabItems(inspectorTabs)
            .FirstOrDefault(static item => string.Equals(item.Header?.ToString(), "PDF", StringComparison.Ordinal));
        if (pdfTab?.Content is not Control content) return;

        _legacyWholeBook = FindControl<CheckBox>(content, static box =>
            string.Equals(box.Content?.ToString(), "Whole book", StringComparison.Ordinal));
        if (_legacyWholeBook?.Parent is not Grid controls) return;

        _legacyScopeLabel = controls.Children
            .OfType<TextBlock>()
            .FirstOrDefault(block => Grid.GetColumn(block) == 6);

        _legacyWholeBook.IsVisible = false;
        if (_legacyScopeLabel is not null) _legacyScopeLabel.IsVisible = false;

        _scope = new ComboBox
        {
            ItemsSource = ScopeItems,
            SelectedIndex = 0,
            Width = 116,
            MinHeight = 28,
            Margin = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(_scope, "Live PDF scope: current document, selected heading, or complete included book");

        _heading = new ComboBox
        {
            MinWidth = 140,
            MaxWidth = 380,
            MinHeight = 28,
            Margin = new Thickness(0, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false
        };
        ToolTip.SetTip(_heading, "Heading section rendered into the live PDF preview");

        Grid.SetColumn(_scope, 5);
        controls.Children.Add(_scope);
        Grid.SetColumn(_heading, 6);
        controls.Children.Add(_heading);

        _scope.SelectionChanged += (_, _) => ScopeSelectionChanged();
        _heading.SelectionChanged += (_, _) => HeadingSelectionChanged();

        _installed = true;
        _window.LayoutUpdated -= OnLayoutUpdated;
        UpdateFromViewModel();
    }

    private void ScopeSelectionChanged()
    {
        if (_updating || _scope is null) return;

        if (!_viewModel.HasDocument && _scope.SelectedIndex != 2)
        {
            _updating = true;
            _scope.SelectedIndex = 2;
            _updating = false;
            _viewModel.SetPreviewWholeBook(true);
            return;
        }

        switch (_scope.SelectedIndex)
        {
            case 2:
                _viewModel.SetPreviewWholeBook(true);
                break;
            case 1:
                _viewModel.SetPreviewWholeBook(false);
                if (_heading?.SelectedItem is OutlineItemViewModel selected)
                    _viewModel.SelectOutline(selected);
                else if (_viewModel.OutlineItems.FirstOrDefault() is { } first)
                    _viewModel.SelectOutline(first);
                else
                    _viewModel.SelectOutline(null);
                break;
            default:
                _viewModel.SetPreviewWholeBook(false);
                _viewModel.SelectOutline(null);
                break;
        }
    }

    private void HeadingSelectionChanged()
    {
        if (_updating || _scope?.SelectedIndex != 1) return;
        if (_heading?.SelectedItem is not OutlineItemViewModel item) return;

        _viewModel.SetPreviewWholeBook(false);
        _viewModel.SelectOutline(item);
    }

    private void OnViewModelStateChanged(object? sender, EventArgs e)
    {
        if (_disposed || !_installed || _updateScheduled) return;
        _updateScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _updateScheduled = false;
            if (!_disposed) UpdateFromViewModel();
        }, DispatcherPriority.Background);
    }

    private void UpdateFromViewModel()
    {
        if (_scope is null || _heading is null) return;

        _updating = true;
        try
        {
            var headings = _viewModel.OutlineItems.ToArray();
            if (!_headings.SequenceEqual(headings))
            {
                _headings = headings;
                _heading.ItemsSource = _headings;
            }

            var scopeIndex = !_viewModel.HasDocument
                ? 2
                : _viewModel.PreviewWholeBook
                    ? 2
                    : _viewModel.SelectedOutline is not null
                        ? 1
                        : 0;

            if (_scope.SelectedIndex != scopeIndex)
                _scope.SelectedIndex = scopeIndex;

            var selectedHeading = _viewModel.SelectedOutline;
            if (!ReferenceEquals(_heading.SelectedItem, selectedHeading))
                _heading.SelectedItem = selectedHeading;

            _scope.IsEnabled = _viewModel.HasProject;
            _heading.IsVisible = scopeIndex == 1;
            _heading.IsEnabled = _viewModel.HasDocument && _headings.Length > 0;
            ToolTip.SetTip(_heading, _headings.Length == 0
                ? "This document has no headings to preview"
                : "Heading section rendered into the live PDF preview");
        }
        finally
        {
            _updating = false;
        }
    }

    private static bool HasPdfTab(TabControl tabs)
        => TabItems(tabs).Any(static item => string.Equals(item.Header?.ToString(), "PDF", StringComparison.Ordinal));

    private static IEnumerable<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is IEnumerable source)
            return source.Cast<object?>().OfType<TabItem>();
        return tabs.Items.OfType<TabItem>();
    }

    private static T? FindControl<T>(Control root, Func<T, bool> predicate) where T : Control
    {
        if (root is T direct && predicate(direct)) return direct;

        if (root is Panel panel)
        {
            foreach (var child in panel.Children.OfType<Control>())
            {
                var found = FindControl(child, predicate);
                if (found is not null) return found;
            }
        }

        if (root is Decorator decorator && decorator.Child is Control decoratedChild)
        {
            var found = FindControl(decoratedChild, predicate);
            if (found is not null) return found;
        }

        if (root is ContentControl contentControl && contentControl.Content is Control content)
        {
            var found = FindControl(content, predicate);
            if (found is not null) return found;
        }

        return null;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        _viewModel.StateChanged -= OnViewModelStateChanged;
    }
}
