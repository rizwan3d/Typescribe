using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

internal sealed class StudioUxPolish
{
    private readonly StudioWorkspaceWindow _window;
    private ConditionalWeakTable<Control, object> _styled = new();
    private Border? _editorEmptyState;
    private TextBox? _editor;
    private TabControl? _inspectorTabs;

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
        polish.ApplyTree();
    }

    private void OnOpened(object? sender, EventArgs e) => ApplyTree();

    private void OnLayoutUpdated(object? sender, EventArgs e) => ApplyTree();

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _styled = new ConditionalWeakTable<Control, object>();
        ApplyTree();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.ActualThemeVariantChanged -= OnThemeChanged;
        _window.Closed -= OnClosed;
    }

    private void ApplyTree()
    {
        NormalizeWindowTitle();
        ApplyShellClasses();
        StyleOnce(_window);
        foreach (var control in _window.GetVisualDescendants().OfType<Control>()) StyleOnce(control);
        UpdateEmptyState();
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
                inspectorTabs.TabStripPlacement = Dock.Left;
                _inspectorTabs = inspectorTabs;
            }
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
            case ListBox listBox:
                StyleListBox(listBox);
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
            textBox.PlaceholderText = "Select a manuscript document in the Binder to begin writing.";
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

    private static void StyleListBox(ListBox listBox)
    {
        if (listBox.ContextMenu is null) return;
        AddClass(listBox, "binder-list");
        listBox.ItemTemplate = new FuncDataTemplate<BinderRowViewModel>(
            static (row, _) => BuildBinderRow(row),
            supportsRecycling: true);
    }

    private static Control BuildBinderRow(BinderRowViewModel row)
    {
        var icon = new TextBlock
        {
            Text = BinderIcon(row.Node.Kind),
            Width = 20,
            FontSize = 12,
            Opacity = 0.72,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        AddClass(icon, "binder-icon");

        var title = new TextBlock
        {
            Text = row.Node.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = row.Node.IsContainer ? FontWeight.SemiBold : FontWeight.Normal
        };
        AddClass(title, "binder-title");

        var compileState = new TextBlock
        {
            Text = row.Node.IncludeInCompilation ? "●" : "○",
            FontSize = 9,
            Opacity = row.Node.IncludeInCompilation ? 0.82 : 0.32,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Width = 18
        };
        AddClass(compileState, "binder-compile-state");

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(Math.Clamp(row.Depth, 0, 8) * 14, 0, 0, 0)
        };
        grid.Children.Add(icon);
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        Grid.SetColumn(compileState, 2);
        grid.Children.Add(compileState);
        return grid;
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
            Text = "Select a chapter in the Binder, or create one to start writing.\nUse Corkboard and Outliner when you want to plan before drafting.",
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

    private static string BinderIcon(NodeKind kind) => kind switch
    {
        NodeKind.Book => "▣",
        NodeKind.Part => "◆",
        NodeKind.Folder => "▸",
        NodeKind.Chapter => "▤",
        NodeKind.Section => "§",
        NodeKind.Scene => "▪",
        NodeKind.Research => "⌕",
        NodeKind.Note => "✎",
        _ => "•"
    };

    private static void AddClass(StyledElement element, string className)
    {
        if (!element.Classes.Contains(className)) element.Classes.Add(className);
    }
}
