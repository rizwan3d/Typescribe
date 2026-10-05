using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop;

internal static class VisualBookDesignWindow
{
    public static async Task ShowAsync(Window owner, WorkspaceViewModel viewModel)
    {
        if (!viewModel.HasProject) return;

        var current = viewModel.CurrentStyle;
        var trim = new ComboBox
        {
            ItemsSource = new[] { "Current / Custom", "6 × 9 in", "5.5 × 8.5 in", "A5 (5.83 × 8.27 in)", "7 × 10 in" },
            SelectedIndex = 0,
            MinWidth = 220
        };
        var bodySize = Slider(current.BodyFontSizePoints, 7, 20, 0.5);
        var lineSpacing = Slider(current.LineSpacing, .85, 2, .05);
        var paragraphIndent = Slider(current.ParagraphIndentEm, 0, 3, .1);
        var paragraphSpace = Slider(current.ParagraphSpacingPoints, 0, 18, .5);
        var topMargin = Slider(current.MarginTopInches, .25, 2, .05);
        var bottomMargin = Slider(current.MarginBottomInches, .25, 2, .05);
        var innerMargin = Slider(current.MarginInnerInches, .25, 2, .05);
        var outerMargin = Slider(current.MarginOuterInches, .25, 2, .05);
        var chapterSize = Slider(current.ChapterFontSizePoints, 12, 48, 1);
        var sectionSize = Slider(current.SectionFontSizePoints, 9, 30, 1);
        var captionSize = Slider(current.CaptionFontSizePoints, 6, 18, .5);
        var justify = new CheckBox { Content = "Justified body text", IsChecked = current.JustifyBody };
        var microtype = new CheckBox { Content = "Microtypography", IsChecked = current.EnableMicrotype };
        var widows = new CheckBox { Content = "Avoid widows/orphans", IsChecked = current.AvoidWidowsAndOrphans };
        var chaptersRight = new CheckBox { Content = "Chapters open on right-hand pages", IsChecked = current.OpenChaptersOnRight };

        var bodyFont = new TextBox { Text = current.BodyFontFamily, Watermark = "Body font" };
        var headingFont = new TextBox { Text = current.HeadingFontFamily, Watermark = "Heading font" };
        var bodyColor = new TextBox { Text = current.BodyColorHex, Watermark = "#202124" };
        var headingColor = new TextBox { Text = current.HeadingColorHex, Watermark = "#202124" };

        var previewTitle = new TextBlock { Text = "Chapter One", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var previewBody = new TextBlock
        {
            Text = "A visual preview makes typography and page geometry easier to judge than a page full of numeric fields. This sample responds immediately to the controls on the left.",
            TextWrapping = TextWrapping.Wrap
        };
        var previewCaption = new TextBlock { Text = "Figure 1 — Sample caption", FontStyle = FontStyle.Italic, TextWrapping = TextWrapping.Wrap };
        var previewPage = new Border
        {
            Background = Brushes.White,
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(34),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Spacing = 18,
                Children =
                {
                    previewTitle,
                    previewBody,
                    new Border { Height = 120, Background = new SolidColorBrush(Color.Parse("#EEEEEE")), Child = new TextBlock { Text = "Figure", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } },
                    previewCaption
                }
            }
        };

        var values = new TextBlock { Opacity = .72, TextWrapping = TextWrapping.Wrap };
        var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
        var apply = new Button { Content = "Apply", MinWidth = 88 };
        var saveClose = new Button { Content = "Save & Close", MinWidth = 110, Margin = new Thickness(8, 0, 0, 0) };
        var close = new Button { Content = "Cancel", MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };

        var controls = new StackPanel { Spacing = 10, Margin = new Thickness(12) };
        controls.Children.Add(new TextBlock { Text = "Page & typography", FontSize = 18, FontWeight = FontWeight.SemiBold });
        controls.Children.Add(Field("Trim preset", trim));
        controls.Children.Add(Field("Body font", bodyFont));
        controls.Children.Add(Field("Heading font", headingFont));
        controls.Children.Add(Field("Body color", bodyColor));
        controls.Children.Add(Field("Heading color", headingColor));
        controls.Children.Add(SliderField("Body size", bodySize));
        controls.Children.Add(SliderField("Line spacing", lineSpacing));
        controls.Children.Add(SliderField("Paragraph indent", paragraphIndent));
        controls.Children.Add(SliderField("Paragraph spacing", paragraphSpace));
        controls.Children.Add(SliderField("Chapter size", chapterSize));
        controls.Children.Add(SliderField("Section size", sectionSize));
        controls.Children.Add(SliderField("Caption size", captionSize));
        controls.Children.Add(new TextBlock { Text = "Margins", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        controls.Children.Add(SliderField("Top", topMargin));
        controls.Children.Add(SliderField("Bottom", bottomMargin));
        controls.Children.Add(SliderField("Inner", innerMargin));
        controls.Children.Add(SliderField("Outer", outerMargin));
        controls.Children.Add(justify);
        controls.Children.Add(microtype);
        controls.Children.Add(widows);
        controls.Children.Add(chaptersRight);
        controls.Children.Add(values);

        var left = new ScrollViewer { Content = controls, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var previewHost = new Grid { Margin = new Thickness(18), Children = { previewPage } };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("390,*"), ColumnSpacing = 12 };
        body.Children.Add(left);
        Grid.SetColumn(previewHost, 1);
        body.Children.Add(previewHost);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { apply, saveClose, close }
        };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(12) };
        root.Children.Add(new TextBlock
        {
            Text = "Visual Book Design — adjust common print-book choices with live feedback. Advanced LaTeX remains available in the full Book Design dialog.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Grid.SetRow(error, 2);
        error.Margin = new Thickness(4, 8, 4, 4);
        root.Children.Add(error);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "Visual Book Design",
            Width = 1060,
            Height = 760,
            MinWidth = 850,
            MinHeight = 580,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };

        var pageWidth = current.PageWidthInches;
        var pageHeight = current.PageHeightInches;

        void ApplyPreset()
        {
            (pageWidth, pageHeight) = trim.SelectedIndex switch
            {
                1 => (6.0, 9.0),
                2 => (5.5, 8.5),
                3 => (5.83, 8.27),
                4 => (7.0, 10.0),
                _ => (pageWidth, pageHeight)
            };
            RefreshPreview();
        }

        void RefreshPreview()
        {
            var scale = 52d;
            var availableWidth = Math.Max(2.5, pageWidth - innerMargin.Value - outerMargin.Value);
            previewPage.Width = Math.Clamp(pageWidth * scale, 260, 520);
            previewPage.Height = Math.Clamp(pageHeight * scale, 390, 690);
            previewPage.Padding = new Thickness(
                Math.Clamp(innerMargin.Value * scale, 12, 100),
                Math.Clamp(topMargin.Value * scale, 12, 100),
                Math.Clamp(outerMargin.Value * scale, 12, 100),
                Math.Clamp(bottomMargin.Value * scale, 12, 100));
            previewTitle.FontSize = chapterSize.Value * 1.18;
            previewTitle.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(headingFont.Text) ? current.HeadingFontFamily : headingFont.Text!);
            previewTitle.Foreground = TryBrush(headingColor.Text, Brushes.Black);
            previewBody.FontSize = bodySize.Value * 1.08;
            previewBody.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(bodyFont.Text) ? current.BodyFontFamily : bodyFont.Text!);
            previewBody.Foreground = TryBrush(bodyColor.Text, Brushes.Black);
            previewBody.LineHeight = Math.Max(previewBody.FontSize + 1, previewBody.FontSize * lineSpacing.Value);
            previewCaption.FontSize = captionSize.Value * 1.05;
            values.Text = $"{pageWidth:0.##} × {pageHeight:0.##} in • text width {availableWidth:0.##} in • body {bodySize.Value:0.#} pt • line {lineSpacing.Value:0.##}";
        }

        async Task SaveAsync(bool closeAfter)
        {
            try
            {
                error.Text = string.Empty;
                var updated = viewModel.CurrentStyle with
                {
                    PageWidthInches = pageWidth,
                    PageHeightInches = pageHeight,
                    MarginTopInches = topMargin.Value,
                    MarginBottomInches = bottomMargin.Value,
                    MarginInnerInches = innerMargin.Value,
                    MarginOuterInches = outerMargin.Value,
                    BodyFontFamily = Required(bodyFont.Text, "Body font"),
                    HeadingFontFamily = Required(headingFont.Text, "Heading font"),
                    BodyColorHex = Required(bodyColor.Text, "Body color"),
                    HeadingColorHex = Required(headingColor.Text, "Heading color"),
                    BodyFontSizePoints = bodySize.Value,
                    LineSpacing = lineSpacing.Value,
                    ParagraphIndentEm = paragraphIndent.Value,
                    ParagraphSpacingPoints = paragraphSpace.Value,
                    ChapterFontSizePoints = chapterSize.Value,
                    SectionFontSizePoints = sectionSize.Value,
                    CaptionFontSizePoints = captionSize.Value,
                    JustifyBody = justify.IsChecked == true,
                    EnableMicrotype = microtype.IsChecked == true,
                    AvoidWidowsAndOrphans = widows.IsChecked == true,
                    OpenChaptersOnRight = chaptersRight.IsChecked == true
                };
                await viewModel.UpdateStyleAsync(updated.Validate());
                if (closeAfter) dialog.Close(true);
            }
            catch (Exception ex) { error.Text = ex.Message; }
        }

        trim.SelectionChanged += (_, _) => ApplyPreset();
        foreach (var slider in new[] { bodySize, lineSpacing, paragraphIndent, paragraphSpace, topMargin, bottomMargin, innerMargin, outerMargin, chapterSize, sectionSize, captionSize })
            slider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) RefreshPreview(); };
        foreach (var box in new[] { bodyFont, headingFont, bodyColor, headingColor }) box.TextChanged += (_, _) => RefreshPreview();
        apply.Click += async (_, _) => await SaveAsync(false);
        saveClose.Click += async (_, _) => await SaveAsync(true);
        close.Click += (_, _) => dialog.Close(false);

        RefreshPreview();
        await dialog.ShowDialog<bool?>(owner);
    }

    private static Slider Slider(double value, double min, double max, double tick)
        => new() { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), TickFrequency = tick, IsSnapToTickEnabled = false };

    private static Control SliderField(string label, Slider slider)
    {
        var value = new TextBlock { Width = 60, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        void Refresh() => value.Text = slider.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        slider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) Refresh(); };
        Refresh();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*,65"), ColumnSpacing = 8 };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);
        return grid;
    }

    private static Control Field(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*"), ColumnSpacing = 8 };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private static IBrush TryBrush(string? text, IBrush fallback)
    {
        try { return new SolidColorBrush(Color.Parse(text?.Trim() ?? string.Empty)); }
        catch (FormatException) { return fallback; }
    }

    private static string Required(string? value, string label)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0) throw new InvalidOperationException($"{label} is required.");
        return text;
    }
}
