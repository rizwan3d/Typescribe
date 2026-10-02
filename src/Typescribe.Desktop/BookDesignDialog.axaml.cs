using System.Collections;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;
using NativeTextBlock = Avalonia.Controls.TextBlock;
using NativeTextBox = Avalonia.Controls.TextBox;

namespace Typescribe.Desktop;

public sealed partial class BookDesignDialog : Window
{
    private static readonly string[] CommonFonts =
    [
        "Libertinus Serif", "TeX Gyre Pagella", "TeX Gyre Termes", "TeX Gyre Schola",
        "Latin Modern Roman", "Noto Serif", "Noto Sans", "DejaVu Serif", "DejaVu Sans",
        "Noto Sans Mono", "DejaVu Sans Mono", "JetBrains Mono", "Fira Code",
        "STIX Two Math", "Libertinus Math", "Latin Modern Math"
    ];

    private static readonly ColorOption[] CommonColors =
    [
        new("Black", "#111111"), new("Charcoal", "#263238"), new("Slate", "#475569"),
        new("Gray", "#6B7280"), new("Indigo", "#4F46E5"), new("Blue", "#2563EB"),
        new("Cyan", "#0891B2"), new("Emerald", "#059669"), new("Amber", "#D97706"),
        new("Rose", "#E11D48"), new("Violet", "#7C3AED"), new("White", "#FFFFFF"),
        new("Paper", "#FAF7F0"), new("Code Gray", "#F3F4F6"), new("Code Dark", "#1F2937")
    ];

    private readonly WorkspaceViewModel? _workspace;
    private readonly Dictionary<string, ComboBox> _fontSelectors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ComboBox> _colorSelectors = new(StringComparer.Ordinal);

    private bool _enhanced;
    private bool _previewScheduled;
    private Border? _previewPage;
    private StackPanel? _previewContent;
    private NativeTextBlock? _previewHeader;
    private NativeTextBlock? _previewChapter;
    private NativeTextBlock? _previewSection;
    private NativeTextBlock? _previewBody;
    private NativeTextBlock? _previewQuote;
    private NativeTextBlock? _previewLink;
    private Border? _previewCodeBox;
    private NativeTextBlock? _previewCodeKeyword;
    private NativeTextBlock? _previewCodeText;
    private NativeTextBlock? _previewCodeString;
    private NativeTextBlock? _previewCodeComment;
    private NativeTextBlock? _previewFooter;

    public BookDesignDialog()
    {
        InitializeComponent();
        DataContext = BookDesignEditorViewModel.FromStyle(BookStyle.Default);
        Opened += OnOpened;
    }

    public BookDesignDialog(WorkspaceViewModel workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        InitializeComponent();
        DataContext = BookDesignEditorViewModel.FromStyle(workspace.CurrentStyle);
        Opened += OnOpened;
    }

    private BookDesignEditorViewModel Editor
        => DataContext as BookDesignEditorViewModel
           ?? throw new InvalidOperationException("Book Design editor model is unavailable.");

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOpened(object? sender, EventArgs e)
    {
        if (_enhanced) return;
        _enhanced = true;
        BuildProfessionalEditingSurface();
        QueuePreviewUpdate();
    }

    private async void OnApplyClick(object? sender, RoutedEventArgs e)
        => await ApplyAsync(closeWhenDone: false);

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
        => await ApplyAsync(closeWhenDone: true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnResetClick(object? sender, RoutedEventArgs e)
    {
        DataContext = BookDesignEditorViewModel.FromStyle(BookStyle.Default);
        SetError(string.Empty);
        SyncSelectorValues();
        QueuePreviewUpdate();
    }

    private async Task ApplyAsync(bool closeWhenDone)
    {
        if (_workspace is null) return;
        try
        {
            SetError(string.Empty);
            var style = Editor.ToStyle();
            await _workspace.UpdateStyleAsync(style);
            QueuePreviewUpdate();
            if (closeWhenDone) Close(true);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    private void SetError(string message)
    {
        // XAML creates Avalonia.Controls.TextBlock. Do not resolve the Typescribe convenience
        // subclass here or FindControl<T> will throw when validation needs to show an error.
        var error = this.FindControl<NativeTextBlock>("ErrorText");
        if (error is not null) error.Text = message ?? string.Empty;
    }

    private void BuildProfessionalEditingSurface()
    {
        if (Content is not Grid root) return;
        var tabs = root.Children.OfType<TabControl>().FirstOrDefault(control => Grid.GetRow(control) == 1);
        if (tabs is null) return;

        Width = Math.Max(Width, 1320);
        Height = Math.Max(Height, 820);
        MinWidth = Math.Max(MinWidth, 1040);
        MinHeight = Math.Max(MinHeight, 640);

        root.ColumnDefinitions = new ColumnDefinitions("*,14,400");
        Grid.SetColumn(tabs, 0);
        Grid.SetColumnSpan(tabs, 1);
        foreach (var child in root.Children.OfType<Control>().Where(child => !ReferenceEquals(child, tabs)).ToArray())
        {
            if (Grid.GetRow(child) != 1) Grid.SetColumnSpan(child, 3);
        }

        var preview = BuildPreviewSurface();
        Grid.SetRow(preview, 1);
        Grid.SetColumn(preview, 2);
        root.Children.Add(preview);

        ReplaceWithFontSelector(tabs, "Body font", nameof(BookDesignEditorViewModel.BodyFontFamily));
        ReplaceWithFontSelector(tabs, "Heading font", nameof(BookDesignEditorViewModel.HeadingFontFamily));
        ReplaceWithFontSelector(tabs, "Monospace font", nameof(BookDesignEditorViewModel.MonospaceFontFamily));
        ReplaceWithFontSelector(tabs, "Math font", nameof(BookDesignEditorViewModel.MathFontFamily));

        ReplaceWithColorSelector(tabs, "Body color", nameof(BookDesignEditorViewModel.BodyColorHex));
        ReplaceWithColorSelector(tabs, "Heading color", nameof(BookDesignEditorViewModel.HeadingColorHex));
        ReplaceWithColorSelector(tabs, "Link color", nameof(BookDesignEditorViewModel.LinkColorHex));
        ReplaceWithColorSelector(tabs, "Background", nameof(BookDesignEditorViewModel.CodeBackgroundHex));
        ReplaceWithColorSelector(tabs, "Text", nameof(BookDesignEditorViewModel.CodeTextHex));
        ReplaceWithColorSelector(tabs, "Keywords", nameof(BookDesignEditorViewModel.CodeKeywordHex));
        ReplaceWithColorSelector(tabs, "Strings", nameof(BookDesignEditorViewModel.CodeStringHex));
        ReplaceWithColorSelector(tabs, "Comments", nameof(BookDesignEditorViewModel.CodeCommentHex));
        ReplaceWithColorSelector(tabs, "Frame", nameof(BookDesignEditorViewModel.CodeFrameHex));

        // XAML controls are native Avalonia controls. Listening to the Typescribe convenience
        // subclasses misses every XAML-created editor and leaves the live preview frozen.
        foreach (var tab in TabItems(tabs))
        {
            if (tab.Content is not Control content) continue;
            foreach (var textBox in EnumerateControls(content).OfType<NativeTextBox>())
                textBox.TextChanged += (_, _) => QueuePreviewUpdate();
            foreach (var checkBox in EnumerateControls(content).OfType<CheckBox>())
                checkBox.Click += (_, _) => QueuePreviewUpdate();
            foreach (var comboBox in EnumerateControls(content).OfType<ComboBox>())
                comboBox.SelectionChanged += (_, _) => QueuePreviewUpdate();
        }

        SyncSelectorValues();
    }

    private Control BuildPreviewSurface()
    {
        _previewHeader = new NativeTextBlock
        {
            Text = "TYPESCRIBE • SAMPLE BOOK",
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.Parse("#4B5563")),
            TextAlignment = TextAlignment.Center
        };
        _previewChapter = new NativeTextBlock
        {
            Text = "Chapter Seven",
            FontSize = 28,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        _previewSection = new NativeTextBlock
        {
            Text = "The Archive Below",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        _previewBody = new NativeTextBlock
        {
            Text = "Rain had been falling over Meridian City since dawn. The preview page reacts immediately to your typeface, size, margins, spacing and color choices.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Justify
        };
        _previewQuote = new NativeTextBlock
        {
            Text = "“Typography should guide the reader without calling attention to itself.”",
            FontSize = 11,
            FontStyle = FontStyle.Italic,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 4)
        };
        _previewLink = new NativeTextBlock
        {
            Text = "Reference link → typescribe.example",
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap
        };

        _previewCodeKeyword = PreviewCodeLine("public sealed class Chapter");
        _previewCodeText = PreviewCodeLine("{    public string Title { get; init; } =");
        _previewCodeString = PreviewCodeLine("    \"The Archive\";");
        _previewCodeComment = PreviewCodeLine("    // LuaLaTeX-ready code block    }");
        var codeLines = new StackPanel
        {
            Spacing = 2,
            Children = { _previewCodeKeyword, _previewCodeText, _previewCodeString, _previewCodeComment }
        };
        _previewCodeBox = new Border
        {
            Padding = new Thickness(12),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = codeLines
        };
        _previewFooter = new NativeTextBlock
        {
            Text = "7",
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.Parse("#4B5563")),
            TextAlignment = TextAlignment.Center
        };

        _previewContent = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                _previewHeader,
                new Border { Height = 24, Background = Brushes.Transparent },
                _previewChapter,
                _previewSection,
                _previewBody,
                _previewQuote,
                _previewLink,
                _previewCodeBox,
                new Border { Height = 18, Background = Brushes.Transparent },
                _previewFooter
            }
        };

        _previewPage = new Border
        {
            Width = 330,
            Height = 520,
            Padding = new Thickness(34, 36),
            Background = new SolidColorBrush(Color.Parse("#FFFDF8")),
            BorderBrush = new SolidColorBrush(Color.Parse("#6B7280")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Child = _previewContent,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(18)
        };

        var label = new NativeTextBlock
        {
            Text = "Live design preview",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#F3F4F6")),
            Margin = new Thickness(12, 10, 12, 4)
        };
        var help = new NativeTextBlock
        {
            Text = "Changes below are simulated immediately. Apply/Save updates the real LuaLaTeX project style.",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#B9C1CC")),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 0, 12, 8)
        };
        var scroll = new ScrollViewer
        {
            Content = _previewPage,
            Background = new SolidColorBrush(Color.Parse("#20242A")),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        panel.Children.Add(label);
        Grid.SetRow(help, 1);
        panel.Children.Add(help);
        Grid.SetRow(scroll, 2);
        panel.Children.Add(scroll);
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#252A31")),
            BorderBrush = new SolidColorBrush(Color.Parse("#3B424C")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = panel
        };
    }

    private static NativeTextBlock PreviewCodeLine(string text)
        => new()
        {
            Text = text,
            FontFamily = new FontFamily("monospace"),
            FontSize = 10,
            TextWrapping = TextWrapping.NoWrap
        };

    private void ReplaceWithFontSelector(TabControl tabs, string label, string propertyName)
    {
        if (!TryFindLabeledTextBox(tabs, label, out var grid, out var editor)) return;
        var current = GetEditorString(propertyName);
        var choices = CommonFonts.Prepend(current)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var combo = new ComboBox { ItemsSource = choices, MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.SelectedItem = choices.FirstOrDefault(value => string.Equals(value, current, StringComparison.OrdinalIgnoreCase)) ?? choices.FirstOrDefault();
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string value) SetEditorString(propertyName, value);
            QueuePreviewUpdate();
        };
        ReplaceGridChild(grid, editor, combo);
        _fontSelectors[propertyName] = combo;
    }

    private void ReplaceWithColorSelector(TabControl tabs, string label, string propertyName)
    {
        if (!TryFindLabeledTextBox(tabs, label, out var grid, out var editor)) return;
        var current = GetEditorString(propertyName);
        var choices = CommonColors.ToList();
        if (!choices.Any(option => string.Equals(option.Hex, current, StringComparison.OrdinalIgnoreCase)))
            choices.Insert(0, new ColorOption("Current", current));
        var combo = new ComboBox { ItemsSource = choices, MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.SelectedItem = choices.FirstOrDefault(option => string.Equals(option.Hex, current, StringComparison.OrdinalIgnoreCase));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ColorOption option) SetEditorString(propertyName, option.Hex);
            QueuePreviewUpdate();
        };
        ReplaceGridChild(grid, editor, combo);
        _colorSelectors[propertyName] = combo;
    }

    private static void ReplaceGridChild(Grid grid, NativeTextBox oldControl, Control newControl)
    {
        var row = Grid.GetRow(oldControl);
        var column = Grid.GetColumn(oldControl);
        var rowSpan = Grid.GetRowSpan(oldControl);
        var columnSpan = Grid.GetColumnSpan(oldControl);
        grid.Children.Remove(oldControl);
        Grid.SetRow(newControl, row);
        Grid.SetColumn(newControl, column);
        Grid.SetRowSpan(newControl, rowSpan);
        Grid.SetColumnSpan(newControl, columnSpan);
        grid.Children.Add(newControl);
    }

    private static bool TryFindLabeledTextBox(TabControl tabs, string label, out Grid grid, out NativeTextBox editor)
    {
        foreach (var tab in TabItems(tabs))
        {
            if (tab.Content is not Control content) continue;
            foreach (var candidate in EnumerateControls(content).OfType<Grid>())
            {
                var labelBlock = candidate.Children.OfType<NativeTextBlock>()
                    .FirstOrDefault(block => string.Equals(block.Text, label, StringComparison.Ordinal));
                if (labelBlock is null) continue;
                var row = Grid.GetRow(labelBlock);
                var textBox = candidate.Children.OfType<NativeTextBox>()
                    .FirstOrDefault(box => Grid.GetRow(box) == row && Grid.GetColumn(box) == 1);
                if (textBox is null) continue;
                grid = candidate;
                editor = textBox;
                return true;
            }
        }
        grid = null!;
        editor = null!;
        return false;
    }

    private void SyncSelectorValues()
    {
        foreach (var pair in _fontSelectors)
        {
            var current = GetEditorString(pair.Key);
            var choices = (pair.Value.ItemsSource as IEnumerable)?.Cast<object?>().OfType<string>().ToList() ?? [];
            if (!choices.Contains(current, StringComparer.OrdinalIgnoreCase))
            {
                choices.Insert(0, current);
                pair.Value.ItemsSource = choices.ToArray();
            }
            pair.Value.SelectedItem = choices.FirstOrDefault(value => string.Equals(value, current, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var pair in _colorSelectors)
        {
            var current = GetEditorString(pair.Key);
            var choices = (pair.Value.ItemsSource as IEnumerable)?.Cast<object?>().OfType<ColorOption>().ToList() ?? [];
            if (!choices.Any(option => string.Equals(option.Hex, current, StringComparison.OrdinalIgnoreCase)))
            {
                choices.Insert(0, new ColorOption("Current", current));
                pair.Value.ItemsSource = choices.ToArray();
            }
            pair.Value.SelectedItem = choices.FirstOrDefault(option => string.Equals(option.Hex, current, StringComparison.OrdinalIgnoreCase));
        }
    }

    private string GetEditorString(string propertyName)
        => typeof(BookDesignEditorViewModel).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(Editor) as string ?? string.Empty;

    private void SetEditorString(string propertyName, string value)
        => typeof(BookDesignEditorViewModel).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.SetValue(Editor, value);

    private void QueuePreviewUpdate()
    {
        if (_previewScheduled) return;
        _previewScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _previewScheduled = false;
            UpdatePreview();
        }, DispatcherPriority.Background);
    }

    private void UpdatePreview()
    {
        if (_previewPage is null || _previewContent is null || _previewHeader is null ||
            _previewChapter is null || _previewSection is null || _previewBody is null ||
            _previewQuote is null || _previewLink is null || _previewCodeBox is null ||
            _previewCodeKeyword is null || _previewCodeText is null || _previewCodeString is null ||
            _previewCodeComment is null || _previewFooter is null)
            return;

        var pageWidth = ReadDouble(Editor.PageWidthInches, 6);
        var pageHeight = ReadDouble(Editor.PageHeightInches, 9);
        var ratio = pageHeight / Math.Max(0.1, pageWidth);
        _previewPage.Height = Math.Clamp(330 * ratio, 470, 690);
        _previewPage.Padding = new Thickness(
            Math.Clamp(ReadDouble(Editor.MarginInnerInches, 0.8) * 28, 16, 70),
            Math.Clamp(ReadDouble(Editor.MarginTopInches, 0.8) * 28, 16, 70),
            Math.Clamp(ReadDouble(Editor.MarginOuterInches, 0.7) * 28, 16, 70),
            Math.Clamp(ReadDouble(Editor.MarginBottomInches, 0.8) * 28, 16, 70));

        var bodyFont = SafeFont(Editor.BodyFontFamily, "serif");
        var headingFont = SafeFont(Editor.HeadingFontFamily, Editor.BodyFontFamily);
        var monoFont = SafeFont(Editor.MonospaceFontFamily, "monospace");
        var bodyBrush = SafeBrush(Editor.BodyColorHex, "#111111");
        var headingBrush = SafeBrush(Editor.HeadingColorHex, "#263238");
        var linkBrush = Editor.ColorLinks ? SafeBrush(Editor.LinkColorHex, "#2563EB") : bodyBrush;
        var bodySize = Math.Clamp(ReadDouble(Editor.BodyFontSizePoints, 11) * 1.02, 8, 18);
        var lineSpacing = Math.Clamp(ReadDouble(Editor.LineSpacing, 1.15), 0.8, 2.5);

        _previewContent.Spacing = Math.Clamp(6 + ReadDouble(Editor.ParagraphSpacingPoints, 3) * 0.45, 5, 18);
        _previewHeader.FontFamily = bodyFont;
        _previewHeader.Foreground = bodyBrush;
        _previewHeader.FontSize = Math.Clamp(ReadDouble(Editor.HeaderFooterFontSizePoints, 9) * 0.92, 7, 13);
        _previewHeader.Text = BuildHeaderPreview();

        _previewChapter.FontFamily = headingFont;
        _previewChapter.Foreground = headingBrush;
        _previewChapter.FontSize = Math.Clamp(ReadDouble(Editor.ChapterFontSizePoints, 26) * 0.9, 17, 38);
        _previewSection.FontFamily = headingFont;
        _previewSection.Foreground = headingBrush;
        _previewSection.FontSize = Math.Clamp(ReadDouble(Editor.SectionFontSizePoints, 18) * 0.9, 12, 28);

        _previewBody.FontFamily = bodyFont;
        _previewBody.Foreground = bodyBrush;
        _previewBody.FontSize = bodySize;
        _previewBody.LineHeight = Math.Max(bodySize, bodySize * lineSpacing);
        _previewBody.TextAlignment = Editor.JustifyBody ? TextAlignment.Justify : TextAlignment.Left;
        _previewQuote.FontFamily = bodyFont;
        _previewQuote.Foreground = bodyBrush;
        _previewQuote.FontSize = Math.Clamp(ReadDouble(Editor.QuoteFontSizePoints, bodySize) * 0.95, 8, 17);
        _previewQuote.FontStyle = Editor.QuoteItalic ? FontStyle.Italic : FontStyle.Normal;
        _previewQuote.Margin = new Thickness(Math.Clamp(ReadDouble(Editor.QuoteIndentEm, 2) * 8, 8, 40), 4);
        _previewLink.FontFamily = bodyFont;
        _previewLink.Foreground = linkBrush;
        _previewLink.FontSize = Math.Clamp(bodySize * 0.94, 8, 16);

        _previewCodeBox.Background = SafeBrush(Editor.CodeBackgroundHex, "#F3F4F6");
        _previewCodeBox.BorderBrush = SafeBrush(Editor.CodeFrameHex, "#D1D5DB");
        var codeSize = Math.Clamp(ReadDouble(Editor.CodeFontSizePoints, 9.5), 7, 14);
        foreach (var line in new[] { _previewCodeKeyword, _previewCodeText, _previewCodeString, _previewCodeComment })
        {
            line.FontFamily = monoFont;
            line.FontSize = codeSize;
        }
        _previewCodeKeyword.Foreground = SafeBrush(Editor.CodeKeywordHex, "#4F46E5");
        _previewCodeText.Foreground = SafeBrush(Editor.CodeTextHex, "#1F2937");
        _previewCodeString.Foreground = SafeBrush(Editor.CodeStringHex, "#059669");
        _previewCodeComment.Foreground = SafeBrush(Editor.CodeCommentHex, "#6B7280");

        _previewFooter.FontFamily = bodyFont;
        _previewFooter.Foreground = bodyBrush;
        _previewFooter.FontSize = Math.Clamp(ReadDouble(Editor.HeaderFooterFontSizePoints, 9) * 0.92, 7, 13);
        _previewFooter.Text = BuildFooterPreview();
    }

    private string BuildHeaderPreview()
    {
        var parts = new[] { Editor.HeaderLeft, Editor.HeaderCenter, Editor.HeaderRight }
            .Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return parts.Length == 0 ? "TYPESCRIBE • SAMPLE BOOK" : string.Join("     ", parts);
    }

    private string BuildFooterPreview()
    {
        var parts = new[] { Editor.FooterLeft, Editor.FooterCenter, Editor.FooterRight }
            .Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (parts.Length > 0) return string.Join("     ", parts);
        return Editor.ShowPageNumbers ? "7" : string.Empty;
    }

    private static double ReadDouble(string? value, double fallback)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var local)) return local;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant)) return invariant;
        return fallback;
    }

    private static FontFamily SafeFont(string? value, string fallback)
    {
        try { return new FontFamily(string.IsNullOrWhiteSpace(value) ? fallback : value.Trim()); }
        catch { return new FontFamily(fallback); }
    }

    private static IBrush SafeBrush(string? value, string fallback)
    {
        try { return new SolidColorBrush(Color.Parse(string.IsNullOrWhiteSpace(value) ? fallback : value.Trim())); }
        catch { return new SolidColorBrush(Color.Parse(fallback)); }
    }

    private static IEnumerable<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is IEnumerable source) return source.Cast<object?>().OfType<TabItem>();
        return tabs.Items.OfType<TabItem>();
    }

    private static IEnumerable<Control> EnumerateControls(Control root)
    {
        yield return root;
        if (root is Panel panel)
        {
            foreach (var child in panel.Children)
                foreach (var descendant in EnumerateControls(child))
                    yield return descendant;
        }
        if (root is ContentControl content && content.Content is Control contentChild)
        {
            foreach (var descendant in EnumerateControls(contentChild)) yield return descendant;
        }
        if (root is Decorator decorator && decorator.Child is Control decoratedChild)
        {
            foreach (var descendant in EnumerateControls(decoratedChild)) yield return descendant;
        }
    }

    private sealed record ColorOption(string Name, string Hex)
    {
        public override string ToString() => $"{Name}   {Hex}";
    }
}
