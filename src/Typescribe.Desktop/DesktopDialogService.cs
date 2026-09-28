using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

internal static class DesktopDialogService
{
    public static async Task<string?> PromptAsync(Window owner, string title, string label, string initialValue = "")
    {
        var input = new TextBox { Text = initialValue, MinWidth = 320 };
        var ok = new Button { Content = "OK", MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", MinWidth = 80 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, ok }
        };
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            Height = 190,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = label },
                    input,
                    buttons
                }
            }
        };

        ok.Click += (_, _) =>
        {
            var value = input.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(value)) dialog.Close(value);
        };
        cancel.Click += (_, _) => dialog.Close(null);
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                e.Handled = true;
                var value = input.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(value)) dialog.Close(value);
            }
        };

        var result = await dialog.ShowDialog<string?>(owner);
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string confirmText = "Delete")
    {
        var confirm = new Button { Content = confirmText, MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        var dialog = new Window
        {
            Title = title,
            Width = 500,
            Height = 200,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 18,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, confirm }
                    }
                }
            }
        };

        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(owner);
    }

    public static async Task<BookStyle?> EditStyleAsync(Window owner, BookStyle current)
    {
        var name = Input(current.Name);
        var font = Input(current.BodyFontFamily);
        var fontSize = Input(current.BodyFontSizePoints);
        var width = Input(current.PageWidthInches);
        var height = Input(current.PageHeightInches);
        var marginTop = Input(current.MarginTopInches);
        var marginBottom = Input(current.MarginBottomInches);
        var marginInner = Input(current.MarginInnerInches);
        var marginOuter = Input(current.MarginOuterInches);
        var lineSpacing = Input(current.LineSpacing);
        var paragraphIndent = Input(current.ParagraphIndentEm);
        var paragraphSpacing = Input(current.ParagraphSpacingPoints);
        var justify = new CheckBox { Content = "Justify body text", IsChecked = current.JustifyBody };
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var save = new Button { Content = "Save Style", MinWidth = 100 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };

        var fields = new StackPanel { Spacing = 7 };
        fields.Children.Add(Field("Style name", name));
        fields.Children.Add(Field("Body font", font));
        fields.Children.Add(Field("Body size (pt)", fontSize));
        fields.Children.Add(Field("Page width (in)", width));
        fields.Children.Add(Field("Page height (in)", height));
        fields.Children.Add(Field("Top margin (in)", marginTop));
        fields.Children.Add(Field("Bottom margin (in)", marginBottom));
        fields.Children.Add(Field("Inner margin (in)", marginInner));
        fields.Children.Add(Field("Outer margin (in)", marginOuter));
        fields.Children.Add(Field("Line spacing", lineSpacing));
        fields.Children.Add(Field("Paragraph indent (em)", paragraphIndent));
        fields.Children.Add(Field("Paragraph spacing (pt)", paragraphSpacing));
        fields.Children.Add(justify);
        fields.Children.Add(error);

        var dialog = new Window
        {
            Title = "Book Style",
            Width = 560,
            Height = 690,
            MinHeight = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Grid
            {
                RowDefinitions = new RowDefinitions("*,Auto"),
                Margin = new Thickness(20),
                Children =
                {
                    new ScrollViewer
                    {
                        Content = fields,
                        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                    }
                }
            }
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { cancel, save }
        };
        Grid.SetRow(buttons, 1);
        ((Grid)dialog.Content!).Children.Add(buttons);

        save.Click += (_, _) =>
        {
            try
            {
                var updated = current with
                {
                    Name = Required(name, "Style name"),
                    BodyFontFamily = Required(font, "Body font"),
                    BodyFontSizePoints = Number(fontSize, "Body size"),
                    PageWidthInches = Number(width, "Page width"),
                    PageHeightInches = Number(height, "Page height"),
                    MarginTopInches = Number(marginTop, "Top margin"),
                    MarginBottomInches = Number(marginBottom, "Bottom margin"),
                    MarginInnerInches = Number(marginInner, "Inner margin"),
                    MarginOuterInches = Number(marginOuter, "Outer margin"),
                    LineSpacing = Number(lineSpacing, "Line spacing"),
                    ParagraphIndentEm = Number(paragraphIndent, "Paragraph indent"),
                    ParagraphSpacingPoints = Number(paragraphSpacing, "Paragraph spacing"),
                    JustifyBody = justify.IsChecked == true
                };
                dialog.Close(updated.Validate());
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                error.Text = ex.Message;
            }
        };
        cancel.Click += (_, _) => dialog.Close(null);

        return await dialog.ShowDialog<BookStyle?>(owner);
    }

    private static Grid Field(string label, Control input)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*") };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(input, 1);
        row.Children.Add(input);
        return row;
    }

    private static TextBox Input(string value) => new() { Text = value };
    private static TextBox Input(double value) => new() { Text = value.ToString("0.###", CultureInfo.InvariantCulture) };

    private static string Required(TextBox input, string label)
    {
        var value = input.Text?.Trim();
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{label} is required.");
        return value;
    }

    private static double Number(TextBox input, string label)
    {
        var value = input.Text?.Trim() ?? string.Empty;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var current)) return current;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant)) return invariant;
        throw new FormatException($"{label} must be a number.");
    }
}
