using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Application.Abstractions;
using Typescribe.Application.Services;
using Typescribe.Desktop.Editing;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

/// <summary>
/// Adds View -> Page Layout. The page canvas is always rebuilt from the currently selected
/// manuscript's Markdown AST; inspector changes write back to named page styles or the same
/// TypeScribe block metadata consumed by every exporter. There is no parallel page document.
/// </summary>
internal sealed class PagedLayoutEditingFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly IDocumentParser _parser;
    private bool _installed;
    private PagedLayoutWindow? _layoutWindow;

    private PagedLayoutEditingFeature(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        IDocumentParser parser)
    {
        _window = window;
        _viewModel = viewModel;
        _parser = parser;
    }

    public static void Apply(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        IDocumentParser parser)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(parser);

        var feature = new PagedLayoutEditingFeature(window, viewModel, parser);
        window.Opened += feature.OnWindowReady;
        window.LayoutUpdated += feature.OnWindowReady;
        feature.TryInstall();
    }

    private void OnWindowReady(object? sender, EventArgs e) => TryInstall();

    private void TryInstall()
    {
        if (_installed) return;
        var menu = _window.GetVisualDescendants().OfType<Menu>().FirstOrDefault();
        if (menu?.ItemsSource is not IEnumerable top) return;

        var view = top.Cast<object>()
            .OfType<MenuItem>()
            .FirstOrDefault(item => HeaderEquals(item.Header, "View"));
        if (view is null) return;

        var items = ToItems(view.ItemsSource);
        if (!items.OfType<MenuItem>().Any(item => HeaderEquals(item.Header, "Page Layout")))
        {
            items.Add(new Separator());
            var command = new MenuItem
            {
                Header = "Page _Layout…",
                InputGesture = new KeyGesture(Key.P, PrimaryModifier() | KeyModifiers.Shift)
            };
            command.Click += (_, _) => OpenLayoutWindow();
            items.Add(command);
            view.ItemsSource = items.ToArray();
        }

        _installed = true;
        _window.LayoutUpdated -= OnWindowReady;
    }

    private void OpenLayoutWindow()
    {
        if (!_viewModel.HasDocument) return;
        if (_layoutWindow is not null)
        {
            _layoutWindow.Activate();
            return;
        }

        _layoutWindow = new PagedLayoutWindow(_window, _viewModel, _parser);
        _layoutWindow.Closed += (_, _) => _layoutWindow = null;
        _layoutWindow.Show(_window);
    }

    private static List<object> ToItems(IEnumerable? source)
        => source?.Cast<object>().ToList() ?? [];

    private static bool HeaderEquals(object? header, string expected)
        => string.Equals(
            header?.ToString()?.Replace("_", string.Empty, StringComparison.Ordinal).Trim(),
            expected,
            StringComparison.OrdinalIgnoreCase);

    private static KeyModifiers PrimaryModifier()
        => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
}

internal sealed class PagedLayoutWindow : Window
{
    private const double PageScale = .78;
    private readonly StudioWorkspaceWindow _owner;
    private readonly WorkspaceViewModel _viewModel;
    private readonly IDocumentParser _parser;
    private readonly PagedLayoutEngine _engine = new();
    private readonly WrapPanel _pages = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _selectionSummary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _pageStyle = new();
    private readonly TextBox _pageWidth = Field();
    private readonly TextBox _pageHeight = Field();
    private readonly TextBox _marginTop = Field();
    private readonly TextBox _marginBottom = Field();
    private readonly TextBox _marginInner = Field();
    private readonly TextBox _marginOuter = Field();
    private readonly TextBox _columns = Field();
    private readonly TextBox _columnGap = Field();
    private readonly ComboBox _sectionStart = EnumPicker<SectionStartMode>();
    private readonly ComboBox _numberStyle = EnumPicker<PageNumberStyle>();
    private readonly TextBox _numberStart = Field();
    private readonly CheckBox _facingPages = new() { Content = "Facing pages" };
    private readonly CheckBox _baselineGrid = new() { Content = "Baseline grid" };
    private readonly TextBox _baselineIncrement = Field();
    private readonly TextBox _frameId = Field();
    private readonly TextBox _nextFrameId = Field();
    private readonly TextBox _frameColumns = Field();
    private readonly TextBox _anchorId = Field();
    private readonly ComboBox _placement = EnumPicker<FloatPlacementMode>();
    private readonly ComboBox _wrap = EnumPicker<TextWrapMode>();
    private readonly TextBlock _warnings = new() { TextWrapping = TextWrapping.Wrap };

    private DocumentAst? _ast;
    private PagedLayoutResult? _result;
    private AstBlock? _selectedBlock;
    private bool _refreshQueued;
    private bool _disposed;

    public PagedLayoutWindow(
        StudioWorkspaceWindow owner,
        WorkspaceViewModel viewModel,
        IDocumentParser parser)
    {
        _owner = owner;
        _viewModel = viewModel;
        _parser = parser;

        Title = "Page Layout — Typescribe";
        Width = 1380;
        Height = 900;
        MinWidth = 980;
        MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildUi();

        _viewModel.StateChanged += OnWorkspaceChanged;
        Closed += OnClosed;
        Opened += (_, _) => Refresh();
    }

    private Control BuildUi()
    {
        var refresh = new Button { Content = "Refresh", MinWidth = 86 };
        refresh.Click += (_, _) => Refresh();
        var source = new Button { Content = "Go to source", MinWidth = 100 };
        source.Click += (_, _) => GoToSource();

        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            Margin = new Thickness(10, 8),
            ColumnSpacing = 8
        };
        toolbar.Children.Add(refresh);
        Grid.SetColumn(source, 1);
        toolbar.Children.Add(source);
        Grid.SetColumn(_status, 2);
        _status.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(_status);
        var legend = new TextBlock
        {
            Text = "Click a frame/block to edit its source metadata",
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = .7
        };
        Grid.SetColumn(legend, 3);
        toolbar.Children.Add(legend);

        var scroll = new ScrollViewer
        {
            Content = _pages,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Padding = new Thickness(16)
        };

        var inspectorScroll = new ScrollViewer
        {
            Content = BuildInspector(),
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("*,330") };
        split.Children.Add(scroll);
        Grid.SetColumn(inspectorScroll, 1);
        split.Children.Add(inspectorScroll);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(toolbar);
        Grid.SetRow(split, 1);
        root.Children.Add(split);
        return root;
    }

    private Control BuildInspector()
    {
        var root = new StackPanel { Margin = new Thickness(12), Spacing = 10 };
        root.Children.Add(Heading("Selection"));
        root.Children.Add(_selectionSummary);

        root.Children.Add(Heading("Parent page / geometry"));
        root.Children.Add(Labelled("Parent page style", _pageStyle));
        root.Children.Add(TwoFields("Width (in)", _pageWidth, "Height (in)", _pageHeight));
        root.Children.Add(TwoFields("Top margin", _marginTop, "Bottom margin", _marginBottom));
        root.Children.Add(TwoFields("Inner margin", _marginInner, "Outer margin", _marginOuter));
        var applyPage = new Button { Content = "Apply parent page geometry" };
        applyPage.Click += async (_, _) => await ApplyPageStyleAsync();
        root.Children.Add(applyPage);

        root.Children.Add(Heading("Section flow"));
        root.Children.Add(TwoFields("Columns", _columns, "Gap (pt)", _columnGap));
        root.Children.Add(Labelled("Section start", _sectionStart));
        root.Children.Add(Labelled("Number style", _numberStyle));
        root.Children.Add(Labelled("Number restart (blank = continue)", _numberStart));
        root.Children.Add(_facingPages);
        root.Children.Add(_baselineGrid);
        root.Children.Add(Labelled("Baseline increment (pt)", _baselineIncrement));
        var applySection = new Button { Content = "Apply section to selected block" };
        applySection.Click += (_, _) => ApplySection();
        root.Children.Add(applySection);

        root.Children.Add(Heading("Threaded text frame"));
        root.Children.Add(Labelled("Frame ID", _frameId));
        root.Children.Add(Labelled("Next frame ID", _nextFrameId));
        root.Children.Add(Labelled("Frame columns", _frameColumns));
        var applyFrame = new Button { Content = "Apply frame metadata" };
        applyFrame.Click += (_, _) => ApplyFrame();
        root.Children.Add(applyFrame);

        root.Children.Add(Heading("Anchor / wrap"));
        root.Children.Add(Labelled("Anchor ID", _anchorId));
        root.Children.Add(Labelled("Placement", _placement));
        root.Children.Add(Labelled("Text wrap", _wrap));
        var applyAnchor = new Button { Content = "Apply anchor metadata" };
        applyAnchor.Click += (_, _) => ApplyAnchor();
        root.Children.Add(applyAnchor);

        root.Children.Add(Heading("Preflight"));
        root.Children.Add(_warnings);
        return root;
    }

    private void OnWorkspaceChanged(object? sender, EventArgs e)
    {
        if (_disposed || _refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            if (!_disposed) Refresh();
        }, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        if (!_viewModel.HasDocument)
        {
            _pages.Children.Clear();
            _status.Text = "Select a manuscript document to lay out.";
            return;
        }

        try
        {
            _ast = _parser.Parse(_viewModel.EditorText);
            _result = _engine.Paginate(_ast, _viewModel.CurrentStyle);
            PopulatePageStylePicker();
            RenderPages();
            UpdateStatus();
            RebindSelection();
        }
        catch (Exception ex)
        {
            _status.Text = $"Page layout failed: {ex.Message}";
        }
    }

    private void RenderPages()
    {
        _pages.Children.Clear();
        if (_result is null) return;

        foreach (var page in _result.Pages)
            _pages.Children.Add(BuildPageCard(page));
    }

    private Control BuildPageCard(PageLayoutPage page)
    {
        var pageWidth = page.WidthPoints * PageScale;
        var pageHeight = page.HeightPoints * PageScale;
        var canvas = new Canvas
        {
            Width = pageWidth,
            Height = pageHeight,
            Background = Brushes.White,
            ClipToBounds = true
        };

        DrawGuides(canvas, page);
        DrawBaselineGrid(canvas, page);

        foreach (var column in page.Columns)
            foreach (var fragment in column.Fragments)
                canvas.Children.Add(BuildFragment(fragment));
        foreach (var fragment in page.FloatingObjects)
            canvas.Children.Add(BuildFragment(fragment));
        foreach (var fragment in page.Footnotes)
            canvas.Children.Add(BuildFragment(fragment));

        var pageSurface = new Border
        {
            Child = canvas,
            BorderBrush = page.IsBlank ? Brushes.DarkGray : Brushes.Gray,
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                Blur = 9,
                OffsetX = 0,
                OffsetY = 3,
                Color = Color.FromArgb(55, 0, 0, 0)
            })
        };

        var horizontalRuler = BuildHorizontalRuler(pageWidth, page.WidthPoints);
        var verticalRuler = BuildVerticalRuler(pageHeight, page.HeightPoints);
        var frame = new Grid
        {
            RowDefinitions = new RowDefinitions("22,*"),
            ColumnDefinitions = new ColumnDefinitions("28,*")
        };
        Grid.SetColumn(horizontalRuler, 1);
        frame.Children.Add(horizontalRuler);
        Grid.SetRow(verticalRuler, 1);
        frame.Children.Add(verticalRuler);
        Grid.SetRow(pageSurface, 1);
        Grid.SetColumn(pageSurface, 1);
        frame.Children.Add(pageSurface);

        return new StackPanel
        {
            Margin = new Thickness(14),
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = page.IsBlank
                        ? $"Page {page.DisplayNumberText} — blank"
                        : $"Page {page.DisplayNumberText}{(page.FacingPages ? page.IsLeftPage ? " — left" : " — right" : string.Empty)}",
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                frame
            }
        };
    }

    private Control BuildFragment(PageLayoutFragment fragment)
    {
        var selected = _selectedBlock?.SourceLine == fragment.SourceLine;
        var text = new TextBlock
        {
            Text = FragmentLabel(fragment),
            TextWrapping = TextWrapping.Wrap,
            FontSize = Math.Max(7, 9 * PageScale),
            Foreground = fragment.Kind == PageLayoutFragmentKind.OversetIndicator ? Brushes.White : Brushes.Black,
            Margin = new Thickness(2),
            MaxLines = 5
        };
        var border = new Border
        {
            Child = text,
            Width = Math.Max(3, fragment.Bounds.WidthPoints * PageScale),
            Height = Math.Max(3, fragment.Bounds.HeightPoints * PageScale),
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = selected ? Brushes.DodgerBlue : BrushFor(fragment),
            Background = BackgroundFor(fragment),
            CornerRadius = new CornerRadius(1)
        };
        ToolTip.SetTip(border,
            $"{fragment.Kind} • source line {fragment.SourceLine}" +
            (fragment.FrameId is null ? string.Empty : $" • frame {fragment.FrameId}") +
            (fragment.AnchorId is null ? string.Empty : $" • anchor {fragment.AnchorId}"));
        border.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            SelectSourceLine(fragment.SourceLine);
        };
        border.DoubleTapped += (_, _) => GoToSource();
        Canvas.SetLeft(border, fragment.Bounds.XPoints * PageScale);
        Canvas.SetTop(border, fragment.Bounds.YPoints * PageScale);
        return border;
    }

    private static string FragmentLabel(PageLayoutFragment fragment)
    {
        if (fragment.Kind == PageLayoutFragmentKind.OversetIndicator) return "+";
        var text = fragment.Text.Replace('\n', ' ').Trim();
        if (text.Length > 180) text = text[..177] + "…";
        return text.Length == 0 ? fragment.Kind.ToString() : text;
    }

    private static IBrush BrushFor(PageLayoutFragment fragment)
        => fragment.Kind switch
        {
            PageLayoutFragmentKind.TextFrame => Brushes.SteelBlue,
            PageLayoutFragmentKind.FloatingObject or PageLayoutFragmentKind.MarginNote => Brushes.DarkOrange,
            PageLayoutFragmentKind.TableHeader or PageLayoutFragmentKind.TableRow => Brushes.SlateGray,
            PageLayoutFragmentKind.OversetIndicator => Brushes.DarkRed,
            PageLayoutFragmentKind.Footnote => Brushes.Gray,
            _ => Brushes.LightGray
        };

    private static IBrush BackgroundFor(PageLayoutFragment fragment)
        => fragment.Kind switch
        {
            PageLayoutFragmentKind.OversetIndicator => Brushes.DarkRed,
            PageLayoutFragmentKind.FloatingObject or PageLayoutFragmentKind.MarginNote =>
                new SolidColorBrush(Color.FromArgb(28, 255, 140, 0)),
            PageLayoutFragmentKind.TextFrame => new SolidColorBrush(Color.FromArgb(20, 30, 110, 190)),
            PageLayoutFragmentKind.TableHeader => new SolidColorBrush(Color.FromArgb(28, 90, 100, 120)),
            _ => Brushes.Transparent
        };

    private static void DrawGuides(Canvas canvas, PageLayoutPage page)
    {
        foreach (var guide in page.Guides)
        {
            if (guide.Orientation == PageLayoutGuideOrientation.Vertical)
            {
                var line = new Border
                {
                    Width = 1,
                    Height = page.HeightPoints * PageScale,
                    Background = new SolidColorBrush(Color.FromArgb(55, 70, 130, 220))
                };
                Canvas.SetLeft(line, guide.PositionPoints * PageScale);
                canvas.Children.Add(line);
            }
            else
            {
                var line = new Border
                {
                    Width = page.WidthPoints * PageScale,
                    Height = 1,
                    Background = new SolidColorBrush(Color.FromArgb(55, 70, 130, 220))
                };
                Canvas.SetTop(line, guide.PositionPoints * PageScale);
                canvas.Children.Add(line);
            }
        }
    }

    private static void DrawBaselineGrid(Canvas canvas, PageLayoutPage page)
    {
        if (page.BaselineGrid is not { Enabled: true } grid || grid.IncrementPoints <= 0) return;
        var start = page.ContentBounds.YPoints + Math.Max(0, grid.StartPoints);
        for (var y = start; y < page.ContentBounds.BottomPoints; y += grid.IncrementPoints)
        {
            var line = new Border
            {
                Width = page.ContentBounds.WidthPoints * PageScale,
                Height = 1,
                Background = new SolidColorBrush(Color.FromArgb(22, 70, 150, 210))
            };
            Canvas.SetLeft(line, page.ContentBounds.XPoints * PageScale);
            Canvas.SetTop(line, y * PageScale);
            canvas.Children.Add(line);
        }
    }

    private static Canvas BuildHorizontalRuler(double renderedWidth, double pointWidth)
    {
        var ruler = new Canvas { Width = renderedWidth, Height = 22 };
        for (double point = 0; point <= pointWidth; point += 36)
        {
            var x = point * PageScale;
            var tick = new Border { Width = 1, Height = point % 72 == 0 ? 9 : 5, Background = Brushes.Gray };
            Canvas.SetLeft(tick, x);
            Canvas.SetTop(tick, 13);
            ruler.Children.Add(tick);
            if (point % 72 == 0)
            {
                var label = new TextBlock { Text = (point / 72).ToString("0", CultureInfo.InvariantCulture), FontSize = 9 };
                Canvas.SetLeft(label, x + 2);
                ruler.Children.Add(label);
            }
        }
        return ruler;
    }

    private static Canvas BuildVerticalRuler(double renderedHeight, double pointHeight)
    {
        var ruler = new Canvas { Width = 28, Height = renderedHeight };
        for (double point = 0; point <= pointHeight; point += 36)
        {
            var y = point * PageScale;
            var tick = new Border { Width = point % 72 == 0 ? 9 : 5, Height = 1, Background = Brushes.Gray };
            Canvas.SetLeft(tick, 19);
            Canvas.SetTop(tick, y);
            ruler.Children.Add(tick);
            if (point % 72 == 0)
            {
                var label = new TextBlock { Text = (point / 72).ToString("0", CultureInfo.InvariantCulture), FontSize = 9 };
                Canvas.SetTop(label, y + 1);
                ruler.Children.Add(label);
            }
        }
        return ruler;
    }

    private void SelectSourceLine(int sourceLine)
    {
        if (_ast is null) return;
        _selectedBlock = _ast.Blocks
            .Where(block => block is not FootnoteDefinitionBlock and not BibliographyEntryBlock)
            .OrderBy(block => Math.Abs(block.SourceLine - sourceLine))
            .FirstOrDefault();
        BindInspector();
        RenderPages();
    }

    private void RebindSelection()
    {
        if (_ast is null || _selectedBlock is null)
        {
            BindInspector();
            return;
        }
        var oldLine = _selectedBlock.SourceLine;
        _selectedBlock = _ast.Blocks.FirstOrDefault(block => block.SourceLine == oldLine) ??
                         _ast.Blocks.OrderBy(block => Math.Abs(block.SourceLine - oldLine)).FirstOrDefault();
        BindInspector();
    }

    private void BindInspector()
    {
        var style = _viewModel.CurrentStyle;
        var block = _selectedBlock;
        var section = block?.Formatting?.Section ?? new SectionFormatting(PageStyleId: style.DefaultPageStyleId);
        var pageStyleId = section.PageStyleId ?? style.DefaultPageStyleId;
        _pageStyle.SelectedItem = pageStyleId;
        BindPageGeometry(pageStyleId);

        _columns.Text = Math.Max(1, section.Columns).ToString(CultureInfo.InvariantCulture);
        _columnGap.Text = section.ColumnGapPoints.ToString("0.##", CultureInfo.InvariantCulture);
        _sectionStart.SelectedItem = section.Start;
        _numberStyle.SelectedItem = section.PageNumberStyle;
        _numberStart.Text = section.PageNumberStart?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _facingPages.IsChecked = section.FacingPages;
        _baselineGrid.IsChecked = section.BaselineGrid?.Enabled == true;
        _baselineIncrement.Text = (section.BaselineGrid?.IncrementPoints ?? 12).ToString("0.##", CultureInfo.InvariantCulture);

        var frame = block?.Formatting?.TextFrame;
        _frameId.Text = frame?.Id ?? string.Empty;
        _nextFrameId.Text = frame?.NextFrameId ?? string.Empty;
        _frameColumns.Text = (frame?.Columns ?? 1).ToString(CultureInfo.InvariantCulture);

        var anchor = block?.Formatting?.AnchoredObject;
        _anchorId.Text = anchor?.Id ?? string.Empty;
        _placement.SelectedItem = anchor?.Placement ?? FloatPlacementMode.Inline;
        _wrap.SelectedItem = anchor?.Wrap ?? TextWrapMode.None;

        _selectionSummary.Text = block is null
            ? "No block selected. Click a laid-out block."
            : $"Source line {block.SourceLine} • {block.GetType().Name}";
    }

    private void PopulatePageStylePicker()
    {
        var ids = _viewModel.CurrentStyle.NamedStyles.PageStyles.Select(static style => style.Id).ToArray();
        _pageStyle.ItemsSource = ids;
        if (_pageStyle.SelectedItem is null)
            _pageStyle.SelectedItem = _viewModel.CurrentStyle.DefaultPageStyleId;
        _pageStyle.SelectionChanged -= PageStyleSelectionChanged;
        _pageStyle.SelectionChanged += PageStyleSelectionChanged;
    }

    private void PageStyleSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_pageStyle.SelectedItem is string id) BindPageGeometry(id);
    }

    private void BindPageGeometry(string id)
    {
        var style = _viewModel.CurrentStyle;
        var page = style.NamedStyles.ResolvePage(id);
        _pageWidth.Text = (page?.WidthInches ?? style.PageWidthInches).ToString("0.###", CultureInfo.InvariantCulture);
        _pageHeight.Text = (page?.HeightInches ?? style.PageHeightInches).ToString("0.###", CultureInfo.InvariantCulture);
        _marginTop.Text = (page?.MarginTopInches ?? style.MarginTopInches).ToString("0.###", CultureInfo.InvariantCulture);
        _marginBottom.Text = (page?.MarginBottomInches ?? style.MarginBottomInches).ToString("0.###", CultureInfo.InvariantCulture);
        _marginInner.Text = (page?.MarginInnerInches ?? style.MarginInnerInches).ToString("0.###", CultureInfo.InvariantCulture);
        _marginOuter.Text = (page?.MarginOuterInches ?? style.MarginOuterInches).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private async Task ApplyPageStyleAsync()
    {
        if (_pageStyle.SelectedItem is not string id) return;
        var style = _viewModel.CurrentStyle;
        var raw = style.NamedStyles.PageStyles.FirstOrDefault(page => string.Equals(page.Id, id, StringComparison.OrdinalIgnoreCase));
        if (raw is null) return;

        var updatedPage = raw with
        {
            WidthInches = ReadDouble(_pageWidth, 3, 20),
            HeightInches = ReadDouble(_pageHeight, 3, 24),
            MarginTopInches = ReadDouble(_marginTop, 0, 5),
            MarginBottomInches = ReadDouble(_marginBottom, 0, 5),
            MarginInnerInches = ReadDouble(_marginInner, 0, 5),
            MarginOuterInches = ReadDouble(_marginOuter, 0, 5)
        };
        var pages = style.NamedStyles.PageStyles
            .Select(page => string.Equals(page.Id, id, StringComparison.OrdinalIgnoreCase) ? updatedPage : page)
            .ToArray();
        var catalog = style.NamedStyles with { PageStyles = pages };
        await _viewModel.UpdateStyleAsync(style with { NamedStyles = catalog });
        Refresh();
    }

    private void ApplySection()
    {
        if (_selectedBlock is null) return;
        var current = _selectedBlock.Formatting?.Section;
        var grid = _baselineGrid.IsChecked == true
            ? new BaselineGridFormatting(true, ReadDouble(_baselineIncrement, 4, 72), current?.BaselineGrid?.StartPoints ?? 0)
            : current?.BaselineGrid is null ? null : current.BaselineGrid with { Enabled = false };
        var section = new SectionFormatting(
            StyleId: current?.StyleId,
            Columns: ReadInt(_columns, 1, 12),
            ColumnGapPoints: ReadDouble(_columnGap, 0, 144),
            Start: _sectionStart.SelectedItem is SectionStartMode start ? start : SectionStartMode.Continuous,
            PageStyleId: _pageStyle.SelectedItem as string ?? _viewModel.CurrentStyle.DefaultPageStyleId,
            PageNumberStyle: _numberStyle.SelectedItem is PageNumberStyle numberStyle ? numberStyle : PageNumberStyle.Arabic,
            PageNumberStart: ReadNullableInt(_numberStart, 1, 100000),
            BaselineGrid: grid,
            FacingPages: _facingPages.IsChecked == true,
            BleedTopPoints: current?.BleedTopPoints ?? 0,
            BleedBottomPoints: current?.BleedBottomPoints ?? 0,
            BleedInsidePoints: current?.BleedInsidePoints ?? 0,
            BleedOutsidePoints: current?.BleedOutsidePoints ?? 0,
            SlugPoints: current?.SlugPoints ?? 0,
            CropMarks: current?.CropMarks ?? false);
        RewriteSelected(currentFormatting => RichBlockFormattingEditor.ReplaceSection(currentFormatting, section));
    }

    private void ApplyFrame()
    {
        if (_selectedBlock is null) return;
        var id = _frameId.Text?.Trim() ?? string.Empty;
        if (id.Length == 0)
        {
            RewriteSelected(current => RichBlockFormattingEditor.ReplaceTextFrame(current, null));
            return;
        }

        var existing = _selectedBlock.Formatting?.TextFrame;
        var frame = new TextFrameFormatting(
            id,
            string.IsNullOrWhiteSpace(_nextFrameId.Text) ? null : _nextFrameId.Text.Trim(),
            ReadInt(_frameColumns, 1, 12),
            existing?.ColumnGapPoints ?? 12,
            existing?.InsetTopPoints ?? 0,
            existing?.InsetRightPoints ?? 0,
            existing?.InsetBottomPoints ?? 0,
            existing?.InsetLeftPoints ?? 0,
            existing?.Direction ?? TextDirectionMode.Auto,
            existing?.BaselineGrid);
        RewriteSelected(current => RichBlockFormattingEditor.ReplaceTextFrame(current, frame));
    }

    private void ApplyAnchor()
    {
        if (_selectedBlock is null) return;
        var id = _anchorId.Text?.Trim() ?? string.Empty;
        if (id.Length == 0)
        {
            RewriteSelected(current => RichBlockFormattingEditor.ReplaceAnchoredObject(current, null));
            return;
        }

        var existing = _selectedBlock.Formatting?.AnchoredObject;
        var anchor = new AnchoredObjectFormatting(
            id,
            _placement.SelectedItem is FloatPlacementMode placement ? placement : FloatPlacementMode.Inline,
            _wrap.SelectedItem is TextWrapMode wrap ? wrap : TextWrapMode.None,
            existing?.OffsetXPoints ?? 0,
            existing?.OffsetYPoints ?? 0,
            existing?.WrapTopPoints ?? 0,
            existing?.WrapRightPoints ?? 0,
            existing?.WrapBottomPoints ?? 0,
            existing?.WrapLeftPoints ?? 0,
            existing?.KeepWithAnchor ?? true);
        RewriteSelected(current => RichBlockFormattingEditor.ReplaceAnchoredObject(current, anchor));
    }

    private void RewriteSelected(Func<RichBlockFormatting?, RichBlockFormatting?> update)
    {
        if (_selectedBlock is null) return;
        try
        {
            var line = _selectedBlock.SourceLine;
            var rewritten = RichBlockFormattingEditor.Upsert(_viewModel.EditorText, line, update);
            _selectedBlock = null;
            _viewModel.UpdateEditorText(rewritten);
            Refresh();
        }
        catch (Exception ex)
        {
            _selectionSummary.Text = $"Could not apply layout metadata: {ex.Message}";
        }
    }

    private void GoToSource()
    {
        if (_selectedBlock is null) return;
        var editor = _owner.GetVisualDescendants().OfType<ManuscriptEditor>()
            .FirstOrDefault(candidate => candidate.DocumentIdentity is not null)
            ?? _owner.GetVisualDescendants().OfType<ManuscriptEditor>().FirstOrDefault();
        if (editor is null || editor.Document.LineCount == 0) return;

        var line = editor.Document.GetLineByNumber(Math.Clamp(_selectedBlock.SourceLine, 1, editor.Document.LineCount));
        editor.CaretOffset = line.Offset;
        editor.ScrollToLine(line.LineNumber);
        editor.Focus();
        _owner.Activate();
    }

    private void UpdateStatus()
    {
        if (_result is null) return;
        var fragments = _result.Pages.Sum(page => page.Columns.Sum(column => column.Fragments.Count));
        _status.Text = $"{_result.Pages.Count} pages • {fragments} flow fragments • {_result.FrameThreads.Count} frame threads";
        _warnings.Text = _result.Warnings.Count == 0
            ? "No page-layout warnings."
            : string.Join(Environment.NewLine, _result.Warnings.Take(12).Select(warning =>
                $"• {warning.Code}: {warning.Message}" + (warning.SourceLine is null ? string.Empty : $" (line {warning.SourceLine})"))) +
              (_result.Warnings.Count > 12 ? $"\n… {_result.Warnings.Count - 12} more" : string.Empty);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _viewModel.StateChanged -= OnWorkspaceChanged;
    }

    private static TextBlock Heading(string text)
        => new() { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 14, Margin = new Thickness(0, 8, 0, 0) };

    private static TextBox Field() => new() { MinWidth = 90 };

    private static ComboBox EnumPicker<T>() where T : struct, Enum
        => new() { ItemsSource = Enum.GetValues<T>() };

    private static Control Labelled(string label, Control control)
        => new StackPanel
        {
            Spacing = 3,
            Children = { new TextBlock { Text = label, Opacity = .72 }, control }
        };

    private static Control TwoFields(string leftLabel, Control left, string rightLabel, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        grid.Children.Add(Labelled(leftLabel, left));
        var rightHost = Labelled(rightLabel, right);
        Grid.SetColumn(rightHost, 1);
        grid.Children.Add(rightHost);
        return grid;
    }

    private static double ReadDouble(TextBox box, double minimum, double maximum)
    {
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            value = minimum;
        return Math.Clamp(value, minimum, maximum);
    }

    private static int ReadInt(TextBox box, int minimum, int maximum)
    {
        if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            value = minimum;
        return Math.Clamp(value, minimum, maximum);
    }

    private static int? ReadNullableInt(TextBox box, int minimum, int maximum)
    {
        if (string.IsNullOrWhiteSpace(box.Text)) return null;
        return ReadInt(box, minimum, maximum);
    }
}
