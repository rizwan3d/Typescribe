using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Typescribe.Application.Abstractions;
using Typescribe.Application.Services;
using Typescribe.Desktop.Editing;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

/// <summary>
/// Word-style print-layout editing for the canonical manuscript editor.
///
/// Unlike the old page-fragment editor, this feature never swaps in a per-block TextBox.
/// The one ManuscriptEditor remains the only live text control, so caret motion, selection,
/// IME/composition, undo/redo, find/replace and formatting commands all work across the
/// complete document. PagedLayoutEngine continuously projects that same document and paints
/// page boundaries into the editor; edits repaginate without changing document identity.
/// </summary>
internal sealed class ContinuousPagedEditingFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly IDocumentParser _parser;
    private readonly PagedLayoutEngine _engine = new();
    private readonly DispatcherTimer _layoutTimer;

    private LongFormEditorChrome? _host;
    private ManuscriptEditor? _editor;
    private ContinuousPageRenderer? _renderer;
    private PageBreakElementGenerator? _pageBreakGenerator;
    private TextBlock? _pageBadge;
    private ToggleButton? _pagesToggle;
    private IReadOnlyList<PageStart> _pageStarts = [];
    private PagedLayoutResult? _layout;
    private bool _installed;
    private bool _queued;
    private bool _disposed;
    private bool _printLayoutEnabled = true;

    private ContinuousPagedEditingFeature(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        IDocumentParser parser)
    {
        _window = window;
        _viewModel = viewModel;
        _parser = parser;
        _layoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
        _layoutTimer.Tick += (_, _) =>
        {
            _layoutTimer.Stop();
            RefreshLayout();
        };
    }

    public static void Apply(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        IDocumentParser parser)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(parser);

        var feature = new ContinuousPagedEditingFeature(window, viewModel, parser);
        window.Opened += feature.WindowReady;
        window.LayoutUpdated += feature.WindowReady;
        window.Closed += feature.WindowClosed;
        viewModel.StateChanged += feature.WorkspaceChanged;
        feature.QueueInstall();
    }

    private void WindowReady(object? sender, EventArgs e) => QueueInstall();

    private void QueueInstall()
    {
        if (_disposed || _installed || _queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            if (!_disposed) TryInstall();
        }, DispatcherPriority.Background);
    }

    private void TryInstall()
    {
        if (_disposed || _installed) return;

        var host = _window.GetVisualDescendants().OfType<LongFormEditorChrome>().FirstOrDefault();
        if (host is null) return;

        var editor = host.Children.OfType<ManuscriptEditor>().FirstOrDefault()
                     ?? host.GetVisualDescendants().OfType<ManuscriptEditor>().FirstOrDefault();
        if (editor is null) return;

        _host = host;
        _editor = editor;
        _renderer = new ContinuousPageRenderer();
        _pageBreakGenerator = new PageBreakElementGenerator();
        editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);
        editor.TextArea.TextView.ElementGenerators.Insert(0, _pageBreakGenerator);
        editor.Classes.Add("continuous-paged-editor");
        editor.IsVisible = true;

        InstallToolbarToggle(host);
        InstallPageBadge(host);

        editor.TextChanged += EditorTextChanged;
        editor.SizeChanged += EditorSizeChanged;
        editor.TextArea.Caret.PositionChanged += CaretChanged;
        editor.TextArea.TextView.ScrollOffsetChanged += TextViewScrollChanged;

        _installed = true;
        _window.LayoutUpdated -= WindowReady;
        RefreshLayout();
    }

    private void InstallToolbarToggle(LongFormEditorChrome host)
    {
        if (host.CommandBar.Child is not WrapPanel wrap) return;
        if (wrap.Children.OfType<Border>().Any(item => item.Classes.Contains("continuous-page-layout-group")))
            return;

        _pagesToggle = new ToggleButton
        {
            Content = "Print layout",
            IsChecked = true,
            MinHeight = 28,
            MinWidth = 76,
            Padding = new Thickness(8, 3),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        _pagesToggle.Classes.Add("editor-command-button");
        ToolTip.SetTip(_pagesToggle, "Use one continuous caret while showing PagedLayoutEngine page breaks");
        _pagesToggle.IsCheckedChanged += PagesToggleChanged;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "PAGES",
                    FontSize = 8.5,
                    FontWeight = FontWeight.SemiBold,
                    Opacity = .5,
                    Margin = new Thickness(1, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center
                },
                _pagesToggle
            }
        };

        var group = new Border
        {
            Child = row,
            Padding = new Thickness(3, 2),
            Margin = new Thickness(0, 0, 4, 2),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(0)
        };
        group.Classes.Add("editor-command-group");
        group.Classes.Add("continuous-page-layout-group");
        wrap.Children.Insert(0, group);
    }

    private void InstallPageBadge(LongFormEditorChrome host)
    {
        if (host.StatusBar.Child is not Grid statusGrid) return;
        if (statusGrid.Children.OfType<TextBlock>().Any(block => block.Classes.Contains("continuous-page-badge")))
            return;

        statusGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        _pageBadge = new TextBlock
        {
            Text = "Page —",
            FontSize = 10.5,
            Opacity = .72,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _pageBadge.Classes.Add("continuous-page-badge");
        Grid.SetColumn(_pageBadge, statusGrid.ColumnDefinitions.Count - 1);
        statusGrid.Children.Add(_pageBadge);
    }

    private void PagesToggleChanged(object? sender, EventArgs e)
    {
        _printLayoutEnabled = _pagesToggle?.IsChecked == true;
        if (_editor is not null)
        {
            // Print layout is intentionally page-width: the same live TextDocument is never
            // detached, replaced, or mirrored into another editor.
            if (_printLayoutEnabled)
            {
                _editor.WordWrap = true;
                _editor.MaxWidth = 820;
                _editor.HorizontalAlignment = HorizontalAlignment.Center;
            }
            else
            {
                _editor.MaxWidth = double.PositiveInfinity;
                _editor.HorizontalAlignment = HorizontalAlignment.Stretch;
            }

            _editor.InvalidateMeasure();
            _editor.TextArea.TextView.InvalidateMeasure();
        }

        ApplyRendererState();
        UpdatePageBadge();
    }

    private void WorkspaceChanged(object? sender, EventArgs e)
    {
        if (_disposed || !_installed) return;
        ScheduleLayout();
    }

    private void EditorTextChanged(object? sender, EventArgs e)
        => ScheduleLayout();

    private void EditorSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _pageBreakGenerator?.SetViewportWidth(Math.Max(240, e.NewSize.Width));
        _editor?.TextArea.TextView.Redraw();
    }

    private void CaretChanged(object? sender, EventArgs e)
        => UpdatePageBadge();

    private void TextViewScrollChanged(object? sender, EventArgs e)
        => _editor?.TextArea.TextView.InvalidateLayer(KnownLayer.Background);

    private void ScheduleLayout()
    {
        if (_disposed || !_installed) return;
        _layoutTimer.Stop();
        _layoutTimer.Start();
    }

    private void RefreshLayout()
    {
        var editor = _editor;
        if (_disposed || editor is null) return;

        if (!_viewModel.HasDocument)
        {
            _layout = null;
            _pageStarts = [];
            _renderer?.SetPages([]);
            _pageBreakGenerator?.SetPages([]);
            editor.TextArea.TextView.Redraw();
            UpdatePageBadge();
            return;
        }

        try
        {
            var source = editor.Text ?? string.Empty;
            var ast = _parser.Parse(source);
            var layout = _engine.Paginate(ast, _viewModel.CurrentStyle);
            var starts = BuildPageStarts(source, ast, layout);

            _layout = layout;
            _pageStarts = starts;
            _renderer?.SetPages(_printLayoutEnabled ? starts : []);
            _pageBreakGenerator?.SetViewportWidth(Math.Max(240, editor.Bounds.Width));
            _pageBreakGenerator?.SetPages(_printLayoutEnabled ? starts : []);
            ApplyRendererState();
            UpdatePageBadge();
        }
        catch
        {
            // Editing must never be blocked by a transient parse/layout failure. The single
            // ManuscriptEditor stays live; page furniture simply waits for the next valid pass.
            _renderer?.SetPages([]);
            _pageBreakGenerator?.SetPages([]);
            _pageStarts = [];
            editor.TextArea.TextView.Redraw();
            UpdatePageBadge();
        }
    }

    private static IReadOnlyList<PageStart> BuildPageStarts(
        string source,
        DocumentAst ast,
        PagedLayoutResult layout)
    {
        if (layout.Pages.Count == 0) return [];

        var starts = new List<PageStart>(layout.Pages.Count)
        {
            new(0, layout.Pages[0].DisplayNumberText, layout.Pages[0].PhysicalNumber, 0, layout.Pages[0].IsLeftPage)
        };
        var lastOffset = 0;

        for (var pageIndex = 1; pageIndex < layout.Pages.Count; pageIndex++)
        {
            var page = layout.Pages[pageIndex];
            var fragment = FirstAuthoredFragment(page);
            if (fragment is null) continue;

            int offset;
            try
            {
                offset = PagedLayoutSourceMapper.GetSourceOffsetForPlainText(
                    source,
                    ast,
                    fragment.SourceBlockIndex,
                    fragment.SourceTextStart);
            }
            catch
            {
                continue;
            }

            offset = Math.Clamp(offset, 0, source.Length);
            if (offset <= lastOffset) continue;

            starts.Add(new PageStart(
                pageIndex,
                page.DisplayNumberText,
                page.PhysicalNumber,
                offset,
                page.IsLeftPage));
            lastOffset = offset;
        }

        return starts;
    }

    private static PageLayoutFragment? FirstAuthoredFragment(PageLayoutPage page)
    {
        foreach (var column in page.Columns.OrderBy(static column => column.Index))
        {
            var fragment = column.Fragments.FirstOrDefault(static candidate =>
                candidate.SourceBlockIndex >= 0 &&
                candidate.Kind != PageLayoutFragmentKind.OversetIndicator &&
                !candidate.IsRepeatedHeader);
            if (fragment is not null) return fragment;
        }

        return page.FloatingObjects.FirstOrDefault(static candidate => candidate.SourceBlockIndex >= 0)
               ?? page.Footnotes.FirstOrDefault(static candidate => candidate.SourceBlockIndex >= 0);
    }

    private void ApplyRendererState()
    {
        if (_renderer is null) return;
        _renderer.Enabled = _printLayoutEnabled;
        _renderer.SetPages(_printLayoutEnabled ? _pageStarts : []);
        _pageBreakGenerator?.SetPages(_printLayoutEnabled ? _pageStarts : []);
        if (_editor is not null)
        {
            editor.TextArea.TextView.Redraw();
            _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }
    }

    private void UpdatePageBadge()
    {
        if (_pageBadge is null) return;
        if (!_viewModel.HasDocument || _layout is null || _layout.Pages.Count == 0)
        {
            _pageBadge.Text = "Page —";
            return;
        }

        if (!_printLayoutEnabled)
        {
            _pageBadge.Text = "Draft";
            return;
        }

        var caret = Math.Max(0, _editor?.CaretOffset ?? 0);
        var current = _pageStarts.Count > 0 ? _pageStarts[0] : null;
        foreach (var start in _pageStarts)
        {
            if (start.SourceOffset > caret) break;
            current = start;
        }

        var physical = current?.PhysicalNumber ?? 1;
        var display = current?.DisplayNumber ?? physical.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _pageBadge.Text = $"Page {display} / {_layout.Pages.Count}";
        ToolTip.SetTip(_pageBadge, $"Physical page {physical} of {_layout.Pages.Count}");
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _layoutTimer.Stop();
        _viewModel.StateChanged -= WorkspaceChanged;
        _window.Opened -= WindowReady;
        _window.LayoutUpdated -= WindowReady;
        _window.Closed -= WindowClosed;

        if (_pagesToggle is not null)
            _pagesToggle.IsCheckedChanged -= PagesToggleChanged;

        if (_editor is not null)
        {
            _editor.TextChanged -= EditorTextChanged;
            _editor.SizeChanged -= EditorSizeChanged;
            _editor.TextArea.Caret.PositionChanged -= CaretChanged;
            _editor.TextArea.TextView.ScrollOffsetChanged -= TextViewScrollChanged;
            if (_renderer is not null)
                _editor.TextArea.TextView.BackgroundRenderers.Remove(_renderer);
            if (_pageBreakGenerator is not null)
                _editor.TextArea.TextView.ElementGenerators.Remove(_pageBreakGenerator);
        }
    }

    private sealed record PageStart(
        int PageIndex,
        string DisplayNumber,
        int PhysicalNumber,
        int SourceOffset,
        bool IsLeftPage);

    private sealed class PageBreakElementGenerator : VisualLineElementGenerator
    {
        private const double PageGapHeight = 86;
        private static readonly IBrush PasteboardBrush = new SolidColorBrush(Color.Parse("#111315"));
        private static readonly IBrush EdgeBrush = new SolidColorBrush(Color.Parse("#3F3F46"));
        private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#969696"));
        private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#007ACC"));
        private IReadOnlyList<PageStart> _pages = [];
        private double _viewportWidth = 760;

        public void SetPages(IReadOnlyList<PageStart> pages)
            => _pages = pages.Count <= 1
                ? []
                : pages.Skip(1).OrderBy(static page => page.SourceOffset).ToArray();

        public void SetViewportWidth(double width)
            => _viewportWidth = Math.Max(240, width);

        public override int GetFirstInterestedOffset(int startOffset)
        {
            foreach (var page in _pages)
            {
                if (page.SourceOffset >= startOffset)
                    return page.SourceOffset;
            }
            return -1;
        }

        public override VisualLineElement ConstructElement(int offset)
        {
            var page = _pages.FirstOrDefault(candidate => candidate.SourceOffset == offset);
            if (page is null) return null!;

            var textViewWidth = CurrentContext?.TextView.Bounds.Width ?? 0;
            var width = Math.Max(240, textViewWidth > 0 ? textViewWidth : _viewportWidth);

            var label = new TextBlock
            {
                Text = $"Page {page.DisplayNumber}",
                Foreground = LabelBrush,
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            var accent = new Border
            {
                Width = 28,
                Height = 2,
                Background = AccentBrush,
                CornerRadius = new CornerRadius(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 20, 0, 0),
                IsHitTestVisible = false
            };

            var gap = new Grid
            {
                Width = width,
                Height = PageGapHeight,
                Background = PasteboardBrush,
                IsHitTestVisible = false,
                Children =
                {
                    new Border
                    {
                        Height = 1,
                        Background = EdgeBrush,
                        VerticalAlignment = VerticalAlignment.Top,
                        IsHitTestVisible = false
                    },
                    label,
                    accent,
                    new Border
                    {
                        Height = 1,
                        Background = EdgeBrush,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        IsHitTestVisible = false
                    }
                }
            };

            ToolTip.SetTip(gap, $"Physical page {page.PhysicalNumber}");
            return new InlineObjectElement(0, gap);
        }
    }

    private sealed class ContinuousPageRenderer : IBackgroundRenderer
    {
        private static readonly IBrush GapBrush = new SolidColorBrush(Color.Parse("#111315"));
        private static readonly IBrush EdgeBrush = new SolidColorBrush(Color.Parse("#3F3F46"));
        private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#007ACC"));
        private static readonly Pen EdgePen = new(EdgeBrush, 1);
        private IReadOnlyList<PageStart> _pages = [];

        public KnownLayer Layer => KnownLayer.Background;
        public bool Enabled { get; set; } = true;

        public void SetPages(IReadOnlyList<PageStart> pages)
            => _pages = pages;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!Enabled || _pages.Count <= 1 || textView.Document.TextLength == 0 || textView.VisualLines.Count == 0)
                return;

            var firstVisible = textView.VisualLines[0].FirstDocumentLine.Offset;
            var lastVisible = textView.VisualLines[^1].LastDocumentLine.EndOffset;

            foreach (var page in _pages.Skip(1))
            {
                var offset = Math.Clamp(page.SourceOffset, 0, Math.Max(0, textView.Document.TextLength - 1));
                if (offset < firstVisible || offset > lastVisible) continue;

                var segment = new SimpleSegment(offset, 1);
                var rect = BackgroundGeometryBuilder.GetRectsForSegment(textView, segment).FirstOrDefault();
                if (rect.Width <= 0 && rect.Height <= 0) continue;

                var y = Math.Max(0, rect.Top - 7);
                var width = Math.Max(1, textView.Bounds.Width);
                drawingContext.FillRectangle(GapBrush, new Rect(0, y, width, 14));
                drawingContext.DrawLine(EdgePen, new Point(0, y), new Point(width, y));
                drawingContext.DrawLine(EdgePen, new Point(0, y + 14), new Point(width, y + 14));

                // A small blue registration tick makes the physical page break easy to find
                // without interrupting typing or selection through the break.
                drawingContext.FillRectangle(AccentBrush, new Rect(0, y + 1, 3, 12));
            }
        }
    }
}
