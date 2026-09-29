using System.Collections;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Typescribe.Desktop;

internal sealed class StudioUxPolish
{
    private readonly StudioWorkspaceWindow _window;
    private ConditionalWeakTable<Control, object> _styled = new();
    private Border? _editorEmptyState;
    private TextBox? _editor;
    private TabControl? _inspectorTabs;
    private bool _applyScheduled;
    private bool _isApplying;
    private bool _disposed;

    private StudioUxPolish(StudioWorkspaceWindow window)
    {
        _window = window;
    }

    public static void Apply(StudioWorkspaceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var polish = new StudioUxPolish(window);
        window.Classes.Add("typescribe-studio");
        window.Opened += polish.OnOpened;
        window.LayoutUpdated += polish.OnLayoutUpdated;
        window.ActualThemeVariantChanged += polish.OnThemeChanged;
        window.Closed += polish.OnClosed;
        polish.ScheduleApply();
    }

    private void OnOpened(object? sender, EventArgs e) => ScheduleApply();

    private void OnLayoutUpdated(object? sender, EventArgs e) => ScheduleApply();

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _styled = new ConditionalWeakTable<Control, object>();
        ScheduleApply();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.ActualThemeVariantChanged -= OnThemeChanged;
        _window.Closed -= OnClosed;
    }

    private void ScheduleApply()
    {
        if (_disposed || _applyScheduled) return;
        _applyScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _applyScheduled = false;
            if (!_disposed) ApplyTree();
        }, DispatcherPriority.Background);
    }

    private void ApplyTree()
    {
        if (_disposed || _isApplying) return;
        _isApplying = true;
        try
        {
            NormalizeWindowTitle();
            ApplyShellClasses();
            StyleOnce(_window);

            var controls = _window.GetVisualDescendants().OfType<Control>().ToArray();
            foreach (var control in controls) StyleOnce(control);

            UpdateEmptyState();
        }
        finally
        {
            _isApplying = false;
        }
    }

    private void NormalizeWindowTitle()
    {
        if (string.Equals(_window.Title, "Typescribe — Typescribe", StringComparison.Ordinal))
            _window.Title = "Typescribe";
    }

    private void ApplyShellClasses()
    {
        if (_window.Content is not Grid root) return;

        foreach (var child in root.Children.OfType<Control>())
        {
            switch (Grid.GetRow(child))
            {
                case 1:
                    AddClass(child, "toolbar-panel");
                    if (child is WrapPanel toolbar) PolishToolbar(toolbar);
                    break;
                case 3:
                    AddClass(child, "status-panel");
                    break;
            }
        }

        var workspace = root.Children.OfType<Grid>().FirstOrDefault(grid => Grid.GetRow(grid) == 2);
        if (workspace is null) return;
        AddClass(workspace, "workspace-grid");

        foreach (var child in workspace.Children.OfType<Control>())
        {
            var column = Grid.GetColumn(child);
            if (column == 0 && child is Border navigator && navigator.Child is TabControl navigatorTabs)
            {
                AddClass(navigator, "navigator-pane");
                AddClass(navigatorTabs, "sidebar-tabs");
            }
            else if (column == 2 && child is TabControl centerTabs)
            {
                AddClass(centerTabs, "center-tabs");
            }
            else if (column == 4 && child is Border inspector && inspector.Child is TabControl inspectorTabs)
            {
                AddClass(inspector, "inspector-pane");
                AddClass(inspectorTabs, "inspector-tabs");
                inspectorTabs.TabStripPlacement = Dock.Top;
                MakeInspectorTabsCompact(inspectorTabs);
                _inspectorTabs = inspectorTabs;
            }
        }
    }

    private static void MakeInspectorTabsCompact(TabControl tabs)
    {
        IEnumerable<TabItem> items = tabs.ItemsSource is IEnumerable source
            ? source.Cast<object?>().OfType<TabItem>()
            : tabs.Items.OfType<TabItem>();

        foreach (var tab in items)
        {
            tab.Width = double.NaN;
            tab.MinWidth = 0;
            tab.MinHeight = 30;
            tab.Padding = new Thickness(7, 5);
            tab.FontSize = 11;
            tab.HorizontalContentAlignment = HorizontalAlignment.Center;
        }
    }

    private static void PolishToolbar(WrapPanel toolbar)
    {
        foreach (var button in toolbar.Children.OfType<Button>())
        {
            var label = button.Content?.ToString()?.Trim() ?? string.Empty;
            if (label is "Editor" or "Corkboard" or "Outliner" or "Inspector" or "Snapshot" or "Composition" or "Refresh PDF")
            {
                button.IsVisible = false;
                continue;
            }

            if (label == "+ Chapter")
                button.Margin = new Thickness(10, 0, 0, 0);
            else if (label.Contains("Publish PDF", StringComparison.OrdinalIgnoreCase))
                button.Margin = new Thickness(18, 0, 0, 0);
        }
    }

    private void StyleOnce(Control control)
    {
        if (_styled.TryGetValue(control, out _)) return;
        _styled.Add(control, new object());

        switch (control)
        {
            case Button button:
                StyleButton(button);
                break;
            case TextBox textBox:
                StyleTextBox(textBox);
                break;
            case Border border:
                StyleBorder(border);
                break;
            case StackPanel stack when stack.Spacing <= 1.1:
                AddClass(stack, "outliner-rows");
                break;
            case TextBlock textBlock:
                StyleTextBlock(textBlock);
                break;
        }
    }

    private static void StyleButton(Button button)
    {
        var label = button.Content?.ToString()?.Trim() ?? string.Empty;
        if (label.Contains("Publish PDF", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("Publish", StringComparison.OrdinalIgnoreCase))
        {
            AddClass(button, "primary");
        }

        if (label is "‹" or "›" or "−" or "+" || label.StartsWith('+'))
            AddClass(button, "compact");
    }

    private void StyleTextBox(TextBox textBox)
    {
        if (textBox.AcceptsReturn && textBox.AcceptsTab)
        {
            AddClass(textBox, "editor-canvas");
            textBox.FontSize = 16;
            textBox.PlaceholderText = "Select a manuscript document in Project Explorer to begin writing.";
            _editor = textBox;
            EnsureEditorEmptyState(textBox);
        }
    }

    private static void StyleBorder(Border border)
    {
        if (border.Child is TabControl)
        {
            AddClass(border, "panel-surface");
            border.ClearValue(Border.BackgroundProperty);
            border.ClearValue(Border.BorderBrushProperty);
            border.ClearValue(Border.CornerRadiusProperty);
            return;
        }

        if (border.Width is >= 230 and <= 270 && border.MinHeight >= 150)
        {
            AddClass(border, "card-surface");
            border.ClearValue(Border.BackgroundProperty);
            border.ClearValue(Border.BorderBrushProperty);
            border.ClearValue(Border.CornerRadiusProperty);
        }
    }

    private void EnsureEditorEmptyState(TextBox editor)
    {
        if (_editorEmptyState is not null || editor.Parent is not Grid parent) return;

        var title = new TextBlock
        {
            Text = "Your manuscript workspace",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        AddClass(title, "empty-state-title");

        var body = new TextBlock
        {
            Text = "Select a chapter in Project Explorer, or create one to start writing.\nUse Corkboard and Outliner when you want to plan before drafting.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.72
        };
        AddClass(body, "muted");

        var hint = new TextBlock
        {
            Text = "Tip: F11 enters Composition Mode",
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 12,
            Opacity = 0.55
        };
        AddClass(hint, "muted");

        _editorEmptyState = new Border
        {
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            Child = new StackPanel
            {
                Spacing = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { title, body, hint }
            }
        };
        AddClass(_editorEmptyState, "editor-empty-state");
        Grid.SetRow(_editorEmptyState, Grid.GetRow(editor));
        Grid.SetColumn(_editorEmptyState, Grid.GetColumn(editor));
        parent.Children.Add(_editorEmptyState);
    }

    private void UpdateEmptyState()
    {
        if (_editor is null || _editorEmptyState is null) return;
        _editorEmptyState.IsVisible = !_editor.IsEnabled;

        if (_inspectorTabs?.Items is { Count: > 0 } items && items[0] is TabItem firstTab && firstTab.Content is Control content)
        {
            var noDocument = !_editor.IsEnabled && string.Equals(_window.Title, "Typescribe", StringComparison.Ordinal);
            content.Opacity = noDocument ? 0.48 : 1;
        }
    }

    private static void StyleTextBlock(TextBlock textBlock)
    {
        if (textBlock.FontSize >= 18 && textBlock.FontWeight == FontWeight.SemiBold)
            AddClass(textBlock, "section-title");
        if (textBlock.Opacity is > 0 and < 0.85)
            AddClass(textBlock, "muted");

        if (textBlock.Text is "Synopsis" or "Notes" or "Status" or "Label" or "Keywords" or "Document word target" or "Custom metadata")
            AddClass(textBlock, "field-label");
    }

    private static void AddClass(StyledElement element, string className)
    {
        if (!element.Classes.Contains(className)) element.Classes.Add(className);
    }
}