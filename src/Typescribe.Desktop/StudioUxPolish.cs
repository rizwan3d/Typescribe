using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Typescribe.Desktop;

internal sealed class StudioUxPolish
{
    private readonly StudioWorkspaceWindow _window;
    private ConditionalWeakTable<Control, object> _styled = new();

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
        ApplyShellClasses();
        StyleOnce(_window);
        foreach (var control in _window.GetVisualDescendants().OfType<Control>()) StyleOnce(control);
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
            if (Grid.GetColumn(child) == 2 && child is TabControl)
                AddClass(child, "center-tabs");
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

    private static void StyleTextBox(TextBox textBox)
    {
        if (textBox.AcceptsReturn && textBox.AcceptsTab)
        {
            AddClass(textBox, "editor-canvas");
            textBox.FontSize = 16;
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

    private static void StyleTextBlock(TextBlock textBlock)
    {
        if (textBlock.FontSize >= 18 && textBlock.FontWeight == Avalonia.Media.FontWeight.SemiBold)
            AddClass(textBlock, "section-title");
        if (textBlock.Opacity is > 0 and < 0.85)
            AddClass(textBlock, "muted");
    }

    private static void AddClass(StyledElement element, string className)
    {
        if (!element.Classes.Contains(className)) element.Classes.Add(className);
    }
}
