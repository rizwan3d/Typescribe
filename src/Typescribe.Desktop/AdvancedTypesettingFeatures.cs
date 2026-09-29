using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Typescribe.Desktop.Editing;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

/// <summary>
/// UI for the publishing capabilities that are deliberately kept out of the permanent
/// studio chrome: book design, advanced LuaLaTeX settings, equation construction, and
/// language-tagged code insertion / editor coloring.
/// </summary>
internal sealed class AdvancedTypesettingFeatures
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly Dictionary<ManuscriptEditor, CodeSyntaxColorizer> _codeColorizers = [];
    private Menu? _menu;
    private ManuscriptEditor? _lastEditor;
    private bool _menuInjected;
    private bool _discoverScheduled;
    private bool _disposed;

    private AdvancedTypesettingFeatures(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        var host = new AdvancedTypesettingFeatures(window, viewModel);
        window.Opened += host.OnOpened;
        window.LayoutUpdated += host.OnLayoutUpdated;
        window.Closed += host.OnClosed;
        viewModel.StateChanged += host.OnStateChanged;
        host.ScheduleDiscover();
    }

    private void OnOpened(object? sender, EventArgs e) => ScheduleDiscover();
    private void OnLayoutUpdated(object? sender, EventArgs e) => ScheduleDiscover();
    private void OnStateChanged(object? sender, EventArgs e)
    {
        foreach (var colorizer in _codeColorizers.Values) colorizer.RefreshTheme();
        if (_codeColorizers.Count == 0) ScheduleDiscover();
    }

    private void ScheduleDiscover()
    {
        if (_disposed || _discoverScheduled) return;
        _discoverScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _discoverScheduled = false;
            if (!_disposed) Discover();
        }, DispatcherPriority.Background);
    }

    private void Discover()
    {
        var controls = _window.GetVisualDescendants().OfType<Control>().ToArray();
        _menu ??= controls.OfType<Menu>().FirstOrDefault();
        InjectMenus();

        foreach (var editor in controls.OfType<ManuscriptEditor>())
        {
            if (_codeColorizers.ContainsKey(editor)) continue;
            var colorizer = new CodeSyntaxColorizer(editor, () => _viewModel.CurrentStyle);
            _codeColorizers[editor] = colorizer;
            editor.GotFocus += EditorGotFocus;
            _lastEditor ??= editor;
        }

        // Once the main editor exists the expensive layout discovery can stop. Dynamic
        // Scrivenings editors still trigger view-model state transitions and are discovered
        // when necessary without walking the visual tree on every layout pass forever.
        if (_menuInjected && _codeColorizers.Count > 0)
            _window.LayoutUpdated -= OnLayoutUpdated;
    }

    private void EditorGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is ManuscriptEditor editor) _lastEditor = editor;
    }

    private void InjectMenus()
    {
        if (_menuInjected || _menu?.ItemsSource is not IEnumerable menuSource) return;
        var top = menuSource.Cast<object?>().OfType<MenuItem>().ToArray();
        var insert = top.FirstOrDefault(item => HeaderEquals(item, "Insert"));
        var project = top.FirstOrDefault(item => HeaderEquals(item, "Project"));
        if (insert is null || project is null) return;

        var insertItems = MenuItems(insert.ItemsSource);
        if (!insertItems.OfType<MenuItem>().Any(item => HeaderEquals(item, "Equation…")))
        {
            insertItems.Add(new Separator());
            insertItems.Add(Command("Advanced _Equation…", ShowEquationEditorAsync, new KeyGesture(Key.E, PrimaryModifier() | KeyModifiers.Shift)));
            insertItems.Add(Command("_Code Block…", ShowCodeBlockEditorAsync));
            insert.ItemsSource = insertItems.ToArray();
        }

        var projectItems = MenuItems(project.ItemsSource);
        if (!projectItems.OfType<MenuItem>().Any(item => HeaderEquals(item, "Book Design & LaTeX…")))
        {
            projectItems.Insert(0, Command("Book _Design && LaTeX…", ShowBookDesignAsync));
            projectItems.Insert(1, new Separator());
            project.ItemsSource = projectItems.ToArray();
        }

        _menuInjected = true;
    }

    private async Task ShowEquationEditorAsync()
    {
        if (!_viewModel.HasDocument) return;
        var active = ResolveEditor();
        var selectedText = active is null || active.SelectionLength <= 0
            ? string.Empty
            : active.Document.GetText(active.SelectionStart, active.SelectionLength);

        var source = new TextBox
        {
            Text = selectedText,
            AcceptsReturn = true,
            AcceptsTab = true,
            MinHeight = 110,
            FontFamily = new FontFamily("monospace"),
            TextWrapping = TextWrapping.Wrap
        };
        var display = new RadioButton { Content = "Display equation", IsChecked = true, GroupName = "math-mode" };
        var inline = new RadioButton { Content = "Inline equation", GroupName = "math-mode", Margin = new Thickness(12, 0, 0, 0) };
        var preview = new TextBlock
        {
            Text = RenderMathPreview(source.Text ?? string.Empty),
            FontSize = 20,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(14),
            VerticalAlignment = VerticalAlignment.Center
        };
        source.TextChanged += (_, _) => preview.Text = RenderMathPreview(source.Text ?? string.Empty);

        void InsertSnippet(string snippet, int caretBack = 0)
        {
            var text = source.Text ?? string.Empty;
            var start = Math.Clamp(source.SelectionStart, 0, text.Length);
            var end = Math.Clamp(source.SelectionEnd, start, text.Length);
            source.Text = text[..start] + snippet + text[end..];
            source.CaretIndex = Math.Clamp(start + snippet.Length - caretBack, 0, source.Text.Length);
            source.SelectionStart = source.CaretIndex;
            source.SelectionEnd = source.CaretIndex;
            source.Focus();
        }

        var structures = Palette(
            ("Fraction", "\\frac{a}{b}"), ("√ Root", "\\sqrt{x}"), ("Power", "x^{n}"), ("Subscript", "x_{i}"),
            ("Integral", "\\int_{a}^{b} f(x)\\,dx"), ("Sum", "\\sum_{i=1}^{n} x_i"),
            ("Product", "\\prod_{i=1}^{n} x_i"), ("Limit", "\\lim_{x \\to 0} f(x)"),
            ("2×2 Matrix", "\\begin{bmatrix}a & b \\\\ c & d\\end{bmatrix}"),
            ("Cases", "\\begin{cases}a, & x < 0 \\\\ b, & x \\ge 0\\end{cases}"),
            ("Aligned", "\\begin{aligned}a &= b + c \\\\ d &= e + f\\end{aligned}"));
        foreach (var button in structures.Children.OfType<Button>())
        {
            var snippet = button.Tag?.ToString() ?? string.Empty;
            button.Click += (_, _) => InsertSnippet(snippet);
        }

        var symbols = Palette(
            ("α", "\\alpha"), ("β", "\\beta"), ("γ", "\\gamma"), ("δ", "\\delta"), ("θ", "\\theta"),
            ("λ", "\\lambda"), ("μ", "\\mu"), ("π", "\\pi"), ("σ", "\\sigma"), ("φ", "\\phi"), ("ω", "\\omega"),
            ("∞", "\\infty"), ("≤", "\\le"), ("≥", "\\ge"), ("≠", "\\ne"), ("±", "\\pm"),
            ("×", "\\times"), ("→", "\\to"), ("∂", "\\partial"), ("∇", "\\nabla"), ("∈", "\\in"));
        foreach (var button in symbols.Children.OfType<Button>())
        {
            var snippet = button.Tag?.ToString() ?? string.Empty;
            button.Click += (_, _) => InsertSnippet(snippet);
        }

        var tabs = new TabControl
        {
            ItemsSource = new object[]
            {
                new TabItem { Header = "Structures", Content = new ScrollViewer { Content = structures } },
                new TabItem { Header = "Symbols", Content = new ScrollViewer { Content = symbols } }
            },
            SelectedIndex = 0,
            MinHeight = 145
        };

        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        var insert = new Button { Content = "Insert Equation", MinWidth = 120 };
        insert.Classes.Add("primary");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, insert } };
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Children = { display, inline } };
        var previewBorder = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), MinHeight = 78, Child = preview };

        var content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Equation Builder", FontSize = 20, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Build with the palette or type any LuaLaTeX math expression directly.", Opacity = 0.72 },
                modes,
                tabs,
                new TextBlock { Text = "LaTeX source", FontWeight = FontWeight.SemiBold },
                source,
                new TextBlock { Text = "Readable preview", FontWeight = FontWeight.SemiBold },
                previewBorder,
                buttons
            }
        };
        var dialog = new Window
        {
            Title = "Insert Equation",
            Width = 760,
            Height = 690,
            MinWidth = 620,
            MinHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content
        };
        cancel.Click += (_, _) => dialog.Close<EquationResult?>(null);
        insert.Click += (_, _) =>
        {
            var latex = (source.Text ?? string.Empty).Trim();
            if (latex.Length > 0) dialog.Close<EquationResult?>(new EquationResult(latex, inline.IsChecked == true));
        };
        source.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(PrimaryModifier()))
            {
                e.Handled = true;
                var latex = (source.Text ?? string.Empty).Trim();
                if (latex.Length > 0) dialog.Close<EquationResult?>(new EquationResult(latex, inline.IsChecked == true));
            }
        };

        var result = await dialog.ShowDialog<EquationResult?>(_window);
        if (result is null) return;
        var insertion = result.Inline
            ? $"${result.Latex}$"
            : $"\n$$\n{result.Latex}\n$$\n";
        InsertAtCaret(insertion);
    }

    private async Task ShowCodeBlockEditorAsync()
    {
        if (!_viewModel.HasDocument) return;
        var active = ResolveEditor();
        var selectedText = active is null || active.SelectionLength <= 0
            ? string.Empty
            : active.Document.GetText(active.SelectionStart, active.SelectionLength);

        string[] languages =
        [
            "C#", "C++", "C", "Java", "JavaScript", "TypeScript", "Python", "SQL", "HTML", "XML",
            "JSON", "Bash", "PowerShell", "Rust", "Go", "LaTeX", "Plain text"
        ];
        var language = new ComboBox { ItemsSource = languages, SelectedIndex = 0, MinWidth = 180 };
        var code = new TextBox
        {
            Text = selectedText,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily(_viewModel.CurrentStyle.MonospaceFontFamily),
            FontSize = Math.Max(10, _viewModel.CurrentStyle.CodeFontSizePoints),
            MinHeight = 290,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        var style = _viewModel.CurrentStyle;
        try
        {
            code.Background = new SolidColorBrush(Color.Parse(style.CodeBackgroundHex));
            code.Foreground = new SolidColorBrush(Color.Parse(style.CodeTextHex));
        }
        catch { }

        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        var insert = new Button { Content = "Insert Code Block", MinWidth = 130 };
        insert.Classes.Add("primary");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, insert } };
        var dialog = new Window
        {
            Title = "Insert Code Block",
            Width = 760,
            Height = 610,
            MinWidth = 580,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Code Block", FontSize = 20, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "The language becomes the fenced-code tag and is used for PDF syntax highlighting.", Opacity = 0.72 },
                    Field("Language", language),
                    code,
                    buttons
                }
            }
        };
        cancel.Click += (_, _) => dialog.Close<CodeInsert?>(null);
        insert.Click += (_, _) => dialog.Close<CodeInsert?>(new CodeInsert(LanguageTag(language.SelectedItem?.ToString()), code.Text ?? string.Empty));
        var result = await dialog.ShowDialog<CodeInsert?>(_window);
        if (result is null) return;
        var fence = result.Language.Length == 0 ? "```" : $"```{result.Language}";
        InsertAtCaret($"\n{fence}\n{result.Code.TrimEnd()}\n```\n");
    }

    private async Task ShowBookDesignAsync()
    {
        if (!_viewModel.HasProject) return;
        var current = _viewModel.CurrentStyle;

        var name = Input(current.Name);
        var docClass = Input(current.DocumentClass);
        var classOptions = Input(current.DocumentClassOptions);
        var width = Input(current.PageWidthInches); var height = Input(current.PageHeightInches);
        var mt = Input(current.MarginTopInches); var mb = Input(current.MarginBottomInches);
        var mi = Input(current.MarginInnerInches); var mo = Input(current.MarginOuterInches);
        var toc = Check("Include table of contents", current.IncludeTableOfContents);
        var tocDepth = Input(current.TableOfContentsDepth); var secDepth = Input(current.SectionNumberDepth);
        var openRight = Check("Open chapters on right-hand pages", current.OpenChaptersOnRight);

        var bodyFont = Input(current.BodyFontFamily); var headingFont = Input(current.HeadingFontFamily);
        var monoFont = Input(current.MonospaceFontFamily); var mathFont = Input(current.MathFontFamily);
        var bodySize = Input(current.BodyFontSizePoints); var lineSpacing = Input(current.LineSpacing);
        var parIndent = Input(current.ParagraphIndentEm); var parSpacing = Input(current.ParagraphSpacingPoints);
        var justify = Check("Justify body text", current.JustifyBody);
        var bodyColor = Input(current.BodyColorHex); var headingColor = Input(current.HeadingColorHex);
        var linkColor = Input(current.LinkColorHex); var colorLinks = Check("Use colored hyperlinks", current.ColorLinks);
        var chapterSize = Input(current.ChapterFontSizePoints); var sectionSize = Input(current.SectionFontSizePoints);
        var subsectionSize = Input(current.SubsectionFontSizePoints); var subsubSize = Input(current.SubsubsectionFontSizePoints);
        var chapterBefore = Input(current.ChapterBeforeSpacingPoints); var chapterAfter = Input(current.ChapterAfterSpacingPoints);
        var sectionBefore = Input(current.SectionBeforeSpacingPoints); var sectionAfter = Input(current.SectionAfterSpacingPoints);
        var quoteSize = Input(current.QuoteFontSizePoints); var quoteIndent = Input(current.QuoteIndentEm);
        var quoteItalic = Check("Italicize block quotes", current.QuoteItalic);
        var listSpacing = Input(current.ListItemSpacingPoints); var captionSize = Input(current.CaptionFontSizePoints);
        var footnoteSize = Input(current.FootnoteFontSizePoints);

        var codeSize = Input(current.CodeFontSizePoints);
        var codeBg = Input(current.CodeBackgroundHex); var codeText = Input(current.CodeTextHex);
        var codeKeyword = Input(current.CodeKeywordHex); var codeString = Input(current.CodeStringHex);
        var codeComment = Input(current.CodeCommentHex); var codeFrame = Input(current.CodeFrameHex);
        var codeNumbers = Check("Show line numbers in published code blocks", current.CodeLineNumbers);

        var hfSize = Input(current.HeaderFooterFontSizePoints);
        var hl = Input(current.HeaderLeft); var hc = Input(current.HeaderCenter); var hr = Input(current.HeaderRight);
        var fl = Input(current.FooterLeft); var fc = Input(current.FooterCenter); var fr = Input(current.FooterRight);
        var pageNumbers = Check("Show page numbers", current.ShowPageNumbers);
        var microtype = Check("Enable microtypographic protrusion and expansion", current.EnableMicrotype);
        var widows = Check("Aggressively avoid widows and orphans", current.AvoidWidowsAndOrphans);
        var packages = Multiline(current.ExtraPackages, 90, "Package names separated by commas/new lines, e.g. csquotes,booktabs");
        var preamble = Multiline(current.CustomPreamble, 190, "Raw LuaLaTeX preamble commands. Advanced users only.");

        var pageForm = Form(
            ("Style name", name), ("Document class", docClass), ("Class options", classOptions),
            ("Page width (in)", width), ("Page height (in)", height),
            ("Top margin (in)", mt), ("Bottom margin (in)", mb), ("Inner margin (in)", mi), ("Outer margin (in)", mo),
            ("TOC depth", tocDepth), ("Number sections through", secDepth));
        pageForm.Children.Add(toc); pageForm.Children.Add(openRight);

        var typeForm = Form(
            ("Body font", bodyFont), ("Heading font", headingFont), ("Monospace font", monoFont), ("Math font", mathFont),
            ("Body size (pt)", bodySize), ("Line spacing", lineSpacing), ("Paragraph indent (em)", parIndent), ("Paragraph spacing (pt)", parSpacing),
            ("Body color", bodyColor), ("Heading color", headingColor), ("Link color", linkColor),
            ("Chapter size (pt)", chapterSize), ("Section size (pt)", sectionSize), ("Subsection size (pt)", subsectionSize), ("Subsubsection size (pt)", subsubSize),
            ("Chapter space before (pt)", chapterBefore), ("Chapter space after (pt)", chapterAfter),
            ("Section space before (pt)", sectionBefore), ("Section space after (pt)", sectionAfter),
            ("Quote size (pt)", quoteSize), ("Quote indent (em)", quoteIndent), ("List item spacing (pt)", listSpacing),
            ("Caption size (pt)", captionSize), ("Footnote size (pt)", footnoteSize));
        typeForm.Children.Add(justify); typeForm.Children.Add(colorLinks); typeForm.Children.Add(quoteItalic);

        var codeForm = Form(
            ("Code size (pt)", codeSize), ("Background color", codeBg), ("Text color", codeText),
            ("Keyword color", codeKeyword), ("String color", codeString), ("Comment color", codeComment), ("Frame color", codeFrame));
        codeForm.Children.Add(codeNumbers);
        codeForm.Children.Add(new TextBlock
        {
            Text = "Fenced blocks such as ```csharp, ```python, and ```sql are published with language-aware listings styling.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
            Margin = new Thickness(0, 8)
        });

        var latexForm = Form(
            ("Header/footer size (pt)", hfSize),
            ("Header left", hl), ("Header center", hc), ("Header right", hr),
            ("Footer left", fl), ("Footer center", fc), ("Footer right", fr));
        latexForm.Children.Add(pageNumbers); latexForm.Children.Add(microtype); latexForm.Children.Add(widows);
        latexForm.Children.Add(new TextBlock { Text = "Additional packages", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        latexForm.Children.Add(packages);
        latexForm.Children.Add(new TextBlock { Text = "Custom preamble", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        latexForm.Children.Add(preamble);

        var tabs = new TabControl
        {
            ItemsSource = new object[]
            {
                DesignTab("Page", pageForm), DesignTab("Typography", typeForm), DesignTab("Code", codeForm), DesignTab("Advanced LaTeX", latexForm)
            },
            SelectedIndex = 0
        };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        var save = new Button { Content = "Save Book Design", MinWidth = 130 };
        save.Classes.Add("primary");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, save } };
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto"), Margin = new Thickness(16) };
        root.Children.Add(tabs);
        Grid.SetRow(error, 1); error.Margin = new Thickness(4, 8); root.Children.Add(error);
        Grid.SetRow(actions, 2); root.Children.Add(actions);

        var dialog = new Window
        {
            Title = "Book Design & LaTeX",
            Width = 830,
            Height = 760,
            MinWidth = 650,
            MinHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };
        cancel.Click += (_, _) => dialog.Close();
        save.Click += async (_, _) =>
        {
            try
            {
                var updated = current with
                {
                    Name = Required(name, "Style name"), DocumentClass = Required(docClass, "Document class"), DocumentClassOptions = classOptions.Text?.Trim() ?? string.Empty,
                    PageWidthInches = Number(width, "Page width"), PageHeightInches = Number(height, "Page height"),
                    MarginTopInches = Number(mt, "Top margin"), MarginBottomInches = Number(mb, "Bottom margin"), MarginInnerInches = Number(mi, "Inner margin"), MarginOuterInches = Number(mo, "Outer margin"),
                    IncludeTableOfContents = toc.IsChecked == true, TableOfContentsDepth = Integer(tocDepth, "TOC depth"), SectionNumberDepth = Integer(secDepth, "Section number depth"), OpenChaptersOnRight = openRight.IsChecked == true,
                    BodyFontFamily = Required(bodyFont, "Body font"), HeadingFontFamily = Required(headingFont, "Heading font"), MonospaceFontFamily = Required(monoFont, "Monospace font"), MathFontFamily = Required(mathFont, "Math font"),
                    BodyFontSizePoints = Number(bodySize, "Body size"), LineSpacing = Number(lineSpacing, "Line spacing"), ParagraphIndentEm = Number(parIndent, "Paragraph indent"), ParagraphSpacingPoints = Number(parSpacing, "Paragraph spacing"), JustifyBody = justify.IsChecked == true,
                    BodyColorHex = Required(bodyColor, "Body color"), HeadingColorHex = Required(headingColor, "Heading color"), LinkColorHex = Required(linkColor, "Link color"), ColorLinks = colorLinks.IsChecked == true,
                    ChapterFontSizePoints = Number(chapterSize, "Chapter size"), SectionFontSizePoints = Number(sectionSize, "Section size"), SubsectionFontSizePoints = Number(subsectionSize, "Subsection size"), SubsubsectionFontSizePoints = Number(subsubSize, "Subsubsection size"),
                    ChapterBeforeSpacingPoints = Number(chapterBefore, "Chapter space before"), ChapterAfterSpacingPoints = Number(chapterAfter, "Chapter space after"), SectionBeforeSpacingPoints = Number(sectionBefore, "Section space before"), SectionAfterSpacingPoints = Number(sectionAfter, "Section space after"),
                    QuoteFontSizePoints = Number(quoteSize, "Quote size"), QuoteItalic = quoteItalic.IsChecked == true, QuoteIndentEm = Number(quoteIndent, "Quote indent"), ListItemSpacingPoints = Number(listSpacing, "List item spacing"), CaptionFontSizePoints = Number(captionSize, "Caption size"), FootnoteFontSizePoints = Number(footnoteSize, "Footnote size"),
                    CodeFontSizePoints = Number(codeSize, "Code size"), CodeBackgroundHex = Required(codeBg, "Code background"), CodeTextHex = Required(codeText, "Code text"), CodeKeywordHex = Required(codeKeyword, "Keyword color"), CodeStringHex = Required(codeString, "String color"), CodeCommentHex = Required(codeComment, "Comment color"), CodeFrameHex = Required(codeFrame, "Frame color"), CodeLineNumbers = codeNumbers.IsChecked == true,
                    HeaderFooterFontSizePoints = Number(hfSize, "Header/footer size"), HeaderLeft = hl.Text ?? string.Empty, HeaderCenter = hc.Text ?? string.Empty, HeaderRight = hr.Text ?? string.Empty, FooterLeft = fl.Text ?? string.Empty, FooterCenter = fc.Text ?? string.Empty, FooterRight = fr.Text ?? string.Empty, ShowPageNumbers = pageNumbers.IsChecked == true,
                    EnableMicrotype = microtype.IsChecked == true, AvoidWidowsAndOrphans = widows.IsChecked == true, ExtraPackages = packages.Text ?? string.Empty, CustomPreamble = preamble.Text ?? string.Empty
                }.Validate();
                await _viewModel.UpdateStyleAsync(updated);
                foreach (var colorizer in _codeColorizers.Values) colorizer.RefreshTheme();
                dialog.Close();
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                error.Text = ex.Message;
            }
        };
        await dialog.ShowDialog(_window);
    }

    private ManuscriptEditor? ResolveEditor()
    {
        var focused = _codeColorizers.Keys.FirstOrDefault(static editor => editor.IsKeyboardFocusWithin);
        if (focused is not null) return focused;
        if (_lastEditor?.Parent is not null && _lastEditor.IsEnabled && !_lastEditor.IsReadOnly) return _lastEditor;
        return _codeColorizers.Keys.FirstOrDefault(static editor => editor.Parent is not null && editor.IsVisible && editor.IsEnabled && !editor.IsReadOnly);
    }

    private void InsertAtCaret(string insertion)
    {
        var editor = ResolveEditor();
        if (editor is not null)
        {
            var start = Math.Clamp(editor.SelectionStart, 0, editor.Document.TextLength);
            var length = Math.Clamp(editor.SelectionLength, 0, editor.Document.TextLength - start);
            editor.Document.Replace(start, length, insertion);
            editor.CaretOffset = start + insertion.Length;
            editor.Select(editor.CaretOffset, 0);
            editor.Focus();
            return;
        }

        var text = _viewModel.EditorText ?? string.Empty;
        _viewModel.UpdateEditorText(text + insertion);
    }

    private MenuItem Command(string header, Func<Task> action, KeyGesture? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGesture = gesture };
        item.Click += async (_, _) =>
        {
            try { await action(); }
            catch { }
        };
        return item;
    }

    private static WrapPanel Palette(params (string Label, string Latex)[] entries)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        foreach (var (label, latex) in entries)
            panel.Children.Add(new Button { Content = label, Tag = latex, Margin = new Thickness(3), MinWidth = 54 });
        return panel;
    }

    private static string RenderMathPreview(string latex)
    {
        var result = latex;
        (string From, string To)[] replacements =
        [
            ("\\alpha", "α"), ("\\beta", "β"), ("\\gamma", "γ"), ("\\delta", "δ"), ("\\theta", "θ"),
            ("\\lambda", "λ"), ("\\mu", "μ"), ("\\pi", "π"), ("\\sigma", "σ"), ("\\phi", "φ"), ("\\omega", "ω"),
            ("\\infty", "∞"), ("\\le", "≤"), ("\\ge", "≥"), ("\\ne", "≠"), ("\\pm", "±"), ("\\times", "×"),
            ("\\to", "→"), ("\\partial", "∂"), ("\\nabla", "∇"), ("\\in", "∈"), ("\\cdot", "·")
        ];
        foreach (var replacement in replacements) result = result.Replace(replacement.From, replacement.To, StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(result) ? "Equation preview appears here" : result;
    }

    private static string LanguageTag(string? display) => display switch
    {
        "C#" => "csharp", "C++" => "cpp", "C" => "c", "Java" => "java", "JavaScript" => "javascript",
        "TypeScript" => "typescript", "Python" => "python", "SQL" => "sql", "HTML" => "html", "XML" => "xml",
        "JSON" => "json", "Bash" => "bash", "PowerShell" => "powershell", "Rust" => "rust", "Go" => "go",
        "LaTeX" => "latex", _ => string.Empty
    };

    private static List<object> MenuItems(object? source)
        => source is IEnumerable enumerable ? enumerable.Cast<object?>().Where(static item => item is not null).Cast<object>().ToList() : [];

    private static bool HeaderEquals(MenuItem item, string text)
        => string.Equals((item.Header?.ToString() ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal).Replace("&&", "&", StringComparison.Ordinal), text, StringComparison.OrdinalIgnoreCase);

    private static KeyModifiers PrimaryModifier() => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private static TextBox Input(string value) => new() { Text = value };
    private static TextBox Input(double value) => new() { Text = value.ToString("0.###", CultureInfo.InvariantCulture) };
    private static TextBox Input(int value) => new() { Text = value.ToString(CultureInfo.InvariantCulture) };
    private static CheckBox Check(string label, bool value) => new() { Content = label, IsChecked = value, Margin = new Thickness(0, 4) };
    private static TextBox Multiline(string value, double height, string watermark) => new()
    {
        Text = value,
        AcceptsReturn = true,
        AcceptsTab = true,
        MinHeight = height,
        TextWrapping = TextWrapping.Wrap,
        Watermark = watermark,
        FontFamily = new FontFamily("monospace")
    };

    private static StackPanel Form(params (string Label, Control Input)[] rows)
    {
        var panel = new StackPanel { Spacing = 7, Margin = new Thickness(12) };
        foreach (var row in rows) panel.Children.Add(Field(row.Label, row.Input));
        return panel;
    }

    private static Grid Field(string label, Control input)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("210,*") };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(input, 1); grid.Children.Add(input);
        return grid;
    }

    private static TabItem DesignTab(string title, Control content)
        => new()
        {
            Header = title,
            Content = new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            }
        };

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

    private static int Integer(TextBox input, string label)
    {
        var value = input.Text?.Trim() ?? string.Empty;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var current)) return current;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var invariant)) return invariant;
        throw new FormatException($"{label} must be an integer.");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        _viewModel.StateChanged -= OnStateChanged;
        foreach (var pair in _codeColorizers)
        {
            pair.Key.GotFocus -= EditorGotFocus;
            pair.Value.Dispose();
        }
        _codeColorizers.Clear();
    }

    private sealed record EquationResult(string Latex, bool Inline);
    private sealed record CodeInsert(string Language, string Code);

    private sealed class CodeSyntaxColorizer : DocumentColorizingTransformer, IDisposable
    {
        private static readonly Dictionary<string, HashSet<string>> Keywords = new(StringComparer.OrdinalIgnoreCase)
        {
            ["csharp"] = new(["using", "namespace", "class", "record", "struct", "interface", "public", "private", "protected", "internal", "static", "readonly", "async", "await", "new", "return", "if", "else", "switch", "case", "for", "foreach", "while", "try", "catch", "finally", "throw", "var", "string", "int", "double", "bool", "void", "null", "true", "false"]),
            ["java"] = new(["package", "import", "class", "interface", "public", "private", "protected", "static", "final", "new", "return", "if", "else", "switch", "case", "for", "while", "try", "catch", "throw", "int", "double", "boolean", "void", "null", "true", "false"]),
            ["python"] = new(["def", "class", "import", "from", "as", "return", "if", "elif", "else", "for", "while", "try", "except", "finally", "raise", "with", "lambda", "async", "await", "True", "False", "None", "and", "or", "not", "in", "is"]),
            ["javascript"] = new(["const", "let", "var", "function", "class", "new", "return", "if", "else", "switch", "case", "for", "while", "try", "catch", "throw", "async", "await", "import", "export", "from", "true", "false", "null", "undefined"]),
            ["typescript"] = new(["const", "let", "var", "function", "class", "interface", "type", "enum", "public", "private", "protected", "readonly", "new", "return", "if", "else", "for", "while", "async", "await", "import", "export", "string", "number", "boolean", "unknown", "never"]),
            ["sql"] = new(["select", "from", "where", "join", "inner", "left", "right", "on", "group", "by", "order", "having", "insert", "into", "update", "delete", "create", "table", "alter", "drop", "and", "or", "not", "null", "as", "distinct"])
        };

        private readonly ManuscriptEditor _editor;
        private readonly Func<BookStyle> _styleProvider;
        private readonly DispatcherTimer _indexTimer;
        private readonly Dictionary<int, string> _codeLines = [];
        private readonly HashSet<int> _fenceLines = [];
        private IBrush _text = Brushes.Gray;
        private IBrush _background = Brushes.Transparent;
        private IBrush _keyword = Brushes.MediumPurple;
        private IBrush _string = Brushes.SeaGreen;
        private IBrush _comment = Brushes.Gray;
        private bool _disposed;

        public CodeSyntaxColorizer(ManuscriptEditor editor, Func<BookStyle> styleProvider)
        {
            _editor = editor;
            _styleProvider = styleProvider;
            _indexTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            _indexTimer.Tick += IndexTimerTick;
            editor.TextChanged += EditorTextChanged;
            editor.TextArea.TextView.LineTransformers.Add(this);
            RefreshTheme();
            RebuildIndex();
        }

        public void RefreshTheme()
        {
            if (_disposed) return;
            try
            {
                var style = _styleProvider();
                _text = new SolidColorBrush(Color.Parse(style.CodeTextHex));
                _background = new SolidColorBrush(Color.Parse(style.CodeBackgroundHex));
                _keyword = new SolidColorBrush(Color.Parse(style.CodeKeywordHex));
                _string = new SolidColorBrush(Color.Parse(style.CodeStringHex));
                _comment = new SolidColorBrush(Color.Parse(style.CodeCommentHex));
                _editor.TextArea.TextView.Redraw();
            }
            catch { }
        }

        private void EditorTextChanged(object? sender, EventArgs e)
        {
            _indexTimer.Stop();
            _indexTimer.Start();
        }

        private void IndexTimerTick(object? sender, EventArgs e)
        {
            _indexTimer.Stop();
            RebuildIndex();
        }

        private void RebuildIndex()
        {
            _codeLines.Clear();
            _fenceLines.Clear();
            string? language = null;
            foreach (var line in _editor.Document.Lines)
            {
                var text = _editor.Document.GetText(line).Trim();
                if (text.StartsWith("```", StringComparison.Ordinal))
                {
                    _fenceLines.Add(line.LineNumber);
                    if (language is null) language = text[3..].Trim().ToLowerInvariant();
                    else language = null;
                    continue;
                }
                if (language is not null) _codeLines[line.LineNumber] = language;
            }
            _editor.TextArea.TextView.Redraw();
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            if (_fenceLines.Contains(line.LineNumber))
            {
                if (line.Length > 0) ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(_comment));
                return;
            }
            if (!_codeLines.TryGetValue(line.LineNumber, out var language) || line.Length <= 0) return;

            var text = CurrentContext.Document.GetText(line);
            ChangeLinePart(line.Offset, line.EndOffset, element =>
            {
                element.TextRunProperties.SetForegroundBrush(_text);
                element.TextRunProperties.SetBackgroundBrush(_background);
            });

            ApplyStrings(line.Offset, text);
            ApplyComment(line.Offset, text, language);
            ApplyKeywords(line.Offset, text, language);
        }

        private void ApplyStrings(int lineStart, string text)
        {
            var quote = '\0';
            var start = -1;
            var escaped = false;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (start < 0)
                {
                    if (ch is '\'' or '"') { quote = ch; start = i; }
                    continue;
                }
                if (escaped) { escaped = false; continue; }
                if (ch == '\\') { escaped = true; continue; }
                if (ch != quote) continue;
                var end = i + 1;
                ChangeLinePart(lineStart + start, lineStart + end, element => element.TextRunProperties.SetForegroundBrush(_string));
                start = -1;
            }
        }

        private void ApplyComment(int lineStart, string text, string language)
        {
            var marker = language switch
            {
                "python" or "bash" or "powershell" => "#",
                "sql" => "--",
                "latex" or "tex" => "%",
                _ => "//"
            };
            var index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
                ChangeLinePart(lineStart + index, lineStart + text.Length, element => element.TextRunProperties.SetForegroundBrush(_comment));
        }

        private void ApplyKeywords(int lineStart, string text, string language)
        {
            language = NormalizeLanguage(language);
            if (!Keywords.TryGetValue(language, out var keywords)) return;
            var index = 0;
            while (index < text.Length)
            {
                while (index < text.Length && !(char.IsLetter(text[index]) || text[index] == '_')) index++;
                if (index >= text.Length) break;
                var start = index++;
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_')) index++;
                var token = text[start..index];
                if (keywords.Contains(token))
                    ChangeLinePart(lineStart + start, lineStart + index, element => element.TextRunProperties.SetForegroundBrush(_keyword));
            }
        }

        private static string NormalizeLanguage(string value) => value switch
        {
            "c#" or "cs" => "csharp",
            "js" => "javascript",
            "ts" => "typescript",
            "py" => "python",
            _ => value
        };

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _indexTimer.Stop();
            _indexTimer.Tick -= IndexTimerTick;
            _editor.TextChanged -= EditorTextChanged;
            _editor.TextArea.TextView.LineTransformers.Remove(this);
        }
    }
}
