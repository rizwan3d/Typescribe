using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Typescribe.Application.Abstractions;
using Typescribe.Application.Services;
using Typescribe.Desktop.Editing;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

/// <summary>
/// Adds an editable two-page spread mode without introducing a second manuscript model.
/// The left/right page views share the exact TextDocument (and therefore the same UndoStack)
/// owned by the canonical ManuscriptEditor. Pagination only decides which source ranges are
/// visible in each physical page viewport.
/// </summary>
internal sealed class FacingSpreadEditingFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly IDocumentParser _parser;
    private bool _installed;
    private bool _queued;
    private bool _disposed;
    private ManuscriptEditor? _mainEditor;
    private FacingSpreadSurface? _surface;
    private ToggleButton? _toggle;

    private FacingSpreadEditingFeature(
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

        var feature = new FacingSpreadEditingFeature(window, viewModel, parser);
        window.Opened += feature.WindowReady;
        window.LayoutUpdated += feature.WindowReady;
        window.Closed += feature.WindowClosed;
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

        var editor = host.Children.OfType<ManuscriptEditor>().FirstOrDefault();
        if (editor is null) return;

        _mainEditor = editor;
        _surface = new FacingSpreadSurface(_viewModel, _parser, editor)
        {
            IsVisible = false
        };
        Grid.SetRow(_surface, 3);
        Grid.SetColumn(_surface, 0);
        host.Children.Add(_surface);

        InstallToggle(host);
        _installed = true;
        _window.LayoutUpdated -= WindowReady;

        // The new visual direction is page-first. Facing spreads start enabled, but the
        // author can return to the continuous single-page view at any time.
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _toggle is null) return;
            _toggle.IsChecked = true;
            SetFacingMode(true);
        }, DispatcherPriority.Background);
    }

    private void InstallToggle(LongFormEditorChrome host)
    {
        if (host.CommandBar.Child is not WrapPanel wrap) return;

        var existingGroup = wrap.Children
            .OfType<Border>()
            .FirstOrDefault(item => item.Classes.Contains("continuous-page-layout-group"));

        var row = existingGroup?.Child as StackPanel;
        if (row is null)
        {
            row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center
            };
            existingGroup = new Border
            {
                Child = row,
                Padding = new Thickness(3, 2),
                Margin = new Thickness(0, 0, 4, 2),
                CornerRadius = new CornerRadius(5)
            };
            existingGroup.Classes.Add("editor-command-group");
            existingGroup.Classes.Add("continuous-page-layout-group");
            wrap.Children.Insert(0, existingGroup);
        }

        if (row.Children.OfType<ToggleButton>().Any(button =>
                string.Equals(button.Content?.ToString(), "Facing", StringComparison.OrdinalIgnoreCase)))
            return;

        _toggle = new ToggleButton
        {
            Content = "Facing",
            MinWidth = 56,
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(8, 3),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        _toggle.Classes.Add("editor-command-button");
        ToolTip.SetTip(_toggle, "Edit left/right book pages side-by-side using the same manuscript document");
        _toggle.IsCheckedChanged += ToggleChanged;
        row.Children.Add(_toggle);
    }

    private void ToggleChanged(object? sender, EventArgs e)
        => SetFacingMode(_toggle?.IsChecked == true);

    private void SetFacingMode(bool enabled)
    {
        if (_surface is null || _mainEditor is null) return;

        if (enabled)
        {
            _surface.IsVisible = true;
            _mainEditor.IsVisible = false;
            _surface.Activate();
        }
        else
        {
            _surface.Deactivate();
            _surface.IsVisible = false;
            _mainEditor.IsVisible = true;
            _mainEditor.Focus();
        }
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        if (_toggle is not null)
            _toggle.IsCheckedChanged -= ToggleChanged;
        _surface?.Dispose();
        _window.Opened -= WindowReady;
        _window.LayoutUpdated -= WindowReady;
        _window.Closed -= WindowClosed;
    }
}

internal sealed class FacingSpreadSurface : Grid
{
    private readonly WorkspaceViewModel _viewModel;
    private readonly IDocumentParser _parser;
    private readonly ManuscriptEditor _mainEditor;
    private readonly PagedLayoutEngine _engine = new();
    private readonly DispatcherTimer _layoutTimer;

    private readonly Button _previous = new() { Content = "‹", Width = 32, Height = 28 };
    private readonly Button _next = new() { Content = "›", Width = 32, Height = 28 };
    private readonly TextBlock _spreadLabel = new()
    {
        Text = "Facing pages",
        VerticalAlignment = VerticalAlignment.Center,
        FontWeight = FontWeight.SemiBold
    };
    private readonly TextBlock _hint = new()
    {
        Text = "One document • one undo history • caret hands off across pages",
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = .58,
        FontSize = 10.5
    };
    private readonly Grid _spreadGrid = new()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,22,Auto"),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top
    };
    private readonly SpreadPageView _left;
    private readonly SpreadPageView _right;

    private IReadOnlyList<SpreadPageRange> _ranges = [];
    private IReadOnlyList<SpreadPair> _spreads = [];
    private int _spreadIndex;
    private bool _active;
    private bool _disposed;
    private bool _syncing;
    private bool _refreshQueued;
    private SpreadPageView? _activeView;

    public FacingSpreadSurface(
        WorkspaceViewModel viewModel,
        IDocumentParser parser,
        ManuscriptEditor mainEditor)
    {
        _viewModel = viewModel;
        _parser = parser;
        _mainEditor = mainEditor;

        Background = Brush("#111315");
        RowDefinitions = new RowDefinitions("Auto,*");

        _left = new SpreadPageView(mainEditor.Document, OnProjectionCaretChanged, OnProjectionSelectionChanged, OnProjectionFocused);
        _right = new SpreadPageView(mainEditor.Document, OnProjectionCaretChanged, OnProjectionSelectionChanged, OnProjectionFocused);
        _spreadGrid.Children.Add(_left);
        Grid.SetColumn(_right, 2);
        _spreadGrid.Children.Add(_right);

        var top = new Border
        {
            Background = Brush("#181818"),
            BorderBrush = Brush("#3F3F46"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 6),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"),
                ColumnSpacing = 7,
                Children =
                {
                    _previous,
                    _next,
                    _spreadLabel,
                    _hint
                }
            }
        };
        Grid.SetColumn(_hint, 4);

        var scroll = new ScrollViewer
        {
            Content = _spreadGrid,
            Background = Brush("#111315"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(26, 22, 26, 34)
        };

        Children.Add(top);
        Grid.SetRow(scroll, 1);
        Children.Add(scroll);

        _previous.Click += PreviousClicked;
        _next.Click += NextClicked;
        SizeChanged += SurfaceSizeChanged;

        _layoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(95) };
        _layoutTimer.Tick += LayoutTimerTick;

        _viewModel.StateChanged += WorkspaceChanged;
        _mainEditor.TextChanged += MainTextChanged;
        _mainEditor.TextArea.Caret.PositionChanged += MainCaretChanged;
        _mainEditor.TextArea.SelectionChanged += MainSelectionChanged;
    }

    public void Activate()
    {
        if (_disposed) return;
        _active = true;
        RefreshLayout(forceCaretSpread: true);
    }

    public void Deactivate()
    {
        _active = false;
        _layoutTimer.Stop();
        SyncMainFromActiveProjection();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _layoutTimer.Stop();

        _previous.Click -= PreviousClicked;
        _next.Click -= NextClicked;
        SizeChanged -= SurfaceSizeChanged;
        _viewModel.StateChanged -= WorkspaceChanged;
        _mainEditor.TextChanged -= MainTextChanged;
        _mainEditor.TextArea.Caret.PositionChanged -= MainCaretChanged;
        _mainEditor.TextArea.SelectionChanged -= MainSelectionChanged;

        _left.Dispose();
        _right.Dispose();
    }

    private void PreviousClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_spreads.Count == 0) return;
        ShowSpread(Math.Max(0, _spreadIndex - 1), moveCaret: true);
    }

    private void NextClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_spreads.Count == 0) return;
        ShowSpread(Math.Min(_spreads.Count - 1, _spreadIndex + 1), moveCaret: true);
    }

    private void SurfaceSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_active || _spreads.Count == 0) return;
        RenderCurrentSpread();
    }

    private void WorkspaceChanged(object? sender, EventArgs e)
    {
        if (!_active) return;
        ScheduleRefresh();
    }

    private void MainTextChanged(object? sender, EventArgs e)
    {
        if (!_active) return;
        ScheduleRefresh();
    }

    private void MainCaretChanged(object? sender, EventArgs e)
    {
        if (!_active || _syncing) return;
        NavigateToOffset(_mainEditor.CaretOffset, focusProjection: false);
    }

    private void MainSelectionChanged(object? sender, EventArgs e)
    {
        if (!_active || _syncing) return;
        NavigateToOffset(_mainEditor.CaretOffset, focusProjection: false);
    }

    private void ScheduleRefresh()
    {
        if (_disposed || !_active || _refreshQueued) return;
        _refreshQueued = true;
        _layoutTimer.Stop();
        _layoutTimer.Start();
    }

    private void LayoutTimerTick(object? sender, EventArgs e)
    {
        _layoutTimer.Stop();
        _refreshQueued = false;
        RefreshLayout(forceCaretSpread: true);
    }

    private void RefreshLayout(bool forceCaretSpread)
    {
        if (_disposed || !_active) return;

        if (!_viewModel.HasDocument)
        {
            _ranges = [];
            _spreads = [];
            _left.Configure(null, _viewModel.CurrentStyle, .8, false);
            _right.Configure(null, _viewModel.CurrentStyle, .8, false);
            _spreadLabel.Text = "Select a manuscript";
            UpdateNavigation();
            return;
        }

        try
        {
            var source = _mainEditor.Text ?? string.Empty;
            var ast = _parser.Parse(source);
            var layout = _engine.Paginate(ast, _viewModel.CurrentStyle);
            _ranges = BuildPageRanges(source, ast, layout);
            _spreads = BuildSpreads(_ranges);

            if (_spreads.Count == 0)
            {
                _spreadLabel.Text = "No pages";
                UpdateNavigation();
                return;
            }

            if (forceCaretSpread)
            {
                var page = FindPageForOffset(_mainEditor.CaretOffset);
                _spreadIndex = page is null ? Math.Clamp(_spreadIndex, 0, _spreads.Count - 1) : SpreadIndexForPage(page.PageIndex);
            }
            else
            {
                _spreadIndex = Math.Clamp(_spreadIndex, 0, _spreads.Count - 1);
            }

            RenderCurrentSpread();
        }
        catch (Exception ex)
        {
            _spreadLabel.Text = $"Spread layout unavailable: {ex.Message}";
        }
    }

    private void RenderCurrentSpread()
    {
        if (_spreads.Count == 0) return;
        _spreadIndex = Math.Clamp(_spreadIndex, 0, _spreads.Count - 1);
        var pair = _spreads[_spreadIndex];
        var scale = CalculateScale(pair);

        _left.Configure(pair.Left, _viewModel.CurrentStyle, scale, ReferenceEquals(_activeView, _left));
        _right.Configure(pair.Right, _viewModel.CurrentStyle, scale, ReferenceEquals(_activeView, _right));

        var leftLabel = pair.Left?.Page.DisplayNumberText;
        var rightLabel = pair.Right?.Page.DisplayNumberText;
        _spreadLabel.Text = leftLabel is not null && rightLabel is not null
            ? $"Pages {leftLabel}–{rightLabel}"
            : $"Page {leftLabel ?? rightLabel ?? "—"}";

        UpdateNavigation();

        // Preserve the global caret after a repagination. The page view that contains it gets
        // focus only when the user was already editing the spread; navigation alone does not
        // steal focus from menus/inspectors.
        var current = FindPageForOffset(_mainEditor.CaretOffset);
        if (current is not null)
        {
            var target = pair.Left?.PageIndex == current.PageIndex ? _left :
                         pair.Right?.PageIndex == current.PageIndex ? _right : null;
            if (target is not null)
            {
                target.PositionViewport();
                if (_activeView is not null)
                    SyncProjectionFromMain(target, focus: false);
            }
        }
    }

    private double CalculateScale(SpreadPair pair)
    {
        var pages = new[] { pair.Left, pair.Right }.Where(static page => page is not null).Select(static page => page!).ToArray();
        if (pages.Length == 0) return .8;

        var totalPoints = pages.Sum(page => page.Page.WidthPoints) + (pages.Length > 1 ? 22 : 0);
        var availableWidth = Math.Max(520, Bounds.Width - 90);
        var widthScale = availableWidth / Math.Max(1, totalPoints);
        var maxHeight = pages.Max(page => page.Page.HeightPoints);
        var availableHeight = Math.Max(480, Bounds.Height - 105);
        var heightScale = availableHeight / Math.Max(1, maxHeight);

        return Math.Clamp(Math.Min(widthScale, heightScale), .52, 1.02);
    }

    private void ShowSpread(int index, bool moveCaret)
    {
        if (_spreads.Count == 0) return;
        _spreadIndex = Math.Clamp(index, 0, _spreads.Count - 1);
        RenderCurrentSpread();

        if (!moveCaret) return;
        var pair = _spreads[_spreadIndex];
        var target = pair.Left is { Editable: true } ? pair.Left : pair.Right is { Editable: true } ? pair.Right : null;
        if (target is null) return;

        _syncing = true;
        try
        {
            _mainEditor.CaretOffset = Math.Clamp(target.StartOffset, 0, _mainEditor.Document.TextLength);
            _mainEditor.Select(_mainEditor.CaretOffset, 0);
        }
        finally
        {
            _syncing = false;
        }

        var view = pair.Left?.PageIndex == target.PageIndex ? _left : _right;
        _activeView = view;
        SyncProjectionFromMain(view, focus: true);
        RenderCurrentSpread();
    }

    private void NavigateToOffset(int offset, bool focusProjection)
    {
        var page = FindPageForOffset(offset);
        if (page is null || _spreads.Count == 0) return;

        var targetSpread = SpreadIndexForPage(page.PageIndex);
        if (targetSpread != _spreadIndex)
        {
            _spreadIndex = targetSpread;
            RenderCurrentSpread();
        }

        var pair = _spreads[_spreadIndex];
        var target = pair.Left?.PageIndex == page.PageIndex ? _left :
                     pair.Right?.PageIndex == page.PageIndex ? _right : null;
        if (target is null) return;

        if (focusProjection)
            _activeView = target;
        target.PositionViewport();
        SyncProjectionFromMain(target, focusProjection);
        UpdateActivePageBorders();
    }

    private void OnProjectionFocused(SpreadPageView view)
    {
        if (!_active || _syncing) return;
        _activeView = view;
        SyncMainFromProjection(view);
        UpdateActivePageBorders();
    }

    private void OnProjectionCaretChanged(SpreadPageView view)
    {
        if (!_active || _syncing || !view.Editor.IsKeyboardFocusWithin) return;

        var caret = view.Editor.CaretOffset;
        var ownRange = view.Range;
        if (ownRange is not null && (caret < ownRange.StartOffset || caret >= ownRange.EndOffset) &&
            FindPageForOffset(caret) is { } destination)
        {
            SyncMainFromProjection(view);
            NavigateToOffset(destination.StartOffset <= caret ? caret : destination.StartOffset, focusProjection: true);
            return;
        }

        _activeView = view;
        SyncMainFromProjection(view);
        UpdateActivePageBorders();
    }

    private void OnProjectionSelectionChanged(SpreadPageView view)
    {
        if (!_active || _syncing || !view.Editor.IsKeyboardFocusWithin) return;
        _activeView = view;
        SyncMainFromProjection(view);
    }

    private void SyncMainFromProjection(SpreadPageView view)
    {
        _syncing = true;
        try
        {
            var editor = view.Editor;
            var start = Math.Clamp(editor.SelectionStart, 0, _mainEditor.Document.TextLength);
            var length = Math.Clamp(editor.SelectionLength, 0, _mainEditor.Document.TextLength - start);
            if (length > 0)
                _mainEditor.Select(start, length);
            else
            {
                _mainEditor.CaretOffset = Math.Clamp(editor.CaretOffset, 0, _mainEditor.Document.TextLength);
                _mainEditor.Select(_mainEditor.CaretOffset, 0);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncMainFromActiveProjection()
    {
        if (_activeView is not null)
            SyncMainFromProjection(_activeView);
    }

    private void SyncProjectionFromMain(SpreadPageView view, bool focus)
    {
        if (view.Range is null || !view.Range.Editable) return;

        _syncing = true;
        try
        {
            var caret = Math.Clamp(_mainEditor.CaretOffset, view.Range.StartOffset, Math.Max(view.Range.StartOffset, view.Range.EndOffset));
            var selectionStart = _mainEditor.SelectionStart;
            var selectionLength = _mainEditor.SelectionLength;

            if (selectionLength > 0)
            {
                // Preserve the full canonical selection in the active page view so typing,
                // cut/delete and formatting still operate on the whole selection even when
                // it spans a page boundary.
                view.Editor.Select(selectionStart, selectionLength);
            }
            else
            {
                view.Editor.CaretOffset = caret;
                view.Editor.Select(caret, 0);
            }

            view.PositionViewport();
            if (focus)
                view.Editor.Focus();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void UpdateActivePageBorders()
    {
        _left.SetActive(ReferenceEquals(_activeView, _left));
        _right.SetActive(ReferenceEquals(_activeView, _right));
    }

    private void UpdateNavigation()
    {
        _previous.IsEnabled = _spreadIndex > 0;
        _next.IsEnabled = _spreadIndex + 1 < _spreads.Count;
        ToolTip.SetTip(_previous, _previous.IsEnabled ? "Previous spread" : "First spread");
        ToolTip.SetTip(_next, _next.IsEnabled ? "Next spread" : "Last spread");
    }

    private SpreadPageRange? FindPageForOffset(int offset)
    {
        if (_ranges.Count == 0) return null;
        offset = Math.Clamp(offset, 0, _mainEditor.Document.TextLength);

        SpreadPageRange? preceding = null;
        foreach (var range in _ranges)
        {
            if (!range.Editable) continue;
            if (offset >= range.StartOffset && (offset < range.EndOffset ||
                                                range.EndOffset == _mainEditor.Document.TextLength && offset == range.EndOffset))
                return range;
            if (range.StartOffset <= offset)
                preceding = range;
        }

        return preceding ?? _ranges.FirstOrDefault(static page => page.Editable);
    }

    private int SpreadIndexForPage(int pageIndex)
    {
        for (var index = 0; index < _spreads.Count; index++)
        {
            var spread = _spreads[index];
            if (spread.Left?.PageIndex == pageIndex || spread.Right?.PageIndex == pageIndex)
                return index;
        }
        return Math.Clamp(_spreadIndex, 0, Math.Max(0, _spreads.Count - 1));
    }

    private static IReadOnlyList<SpreadPageRange> BuildPageRanges(
        string source,
        DocumentAst ast,
        PagedLayoutResult layout)
    {
        if (layout.Pages.Count == 0) return [];

        var starts = new int?[layout.Pages.Count];
        for (var pageIndex = 0; pageIndex < layout.Pages.Count; pageIndex++)
        {
            var page = layout.Pages[pageIndex];
            var fragment = FirstAuthoredFragment(page);
            if (fragment is null) continue;

            try
            {
                starts[pageIndex] = Math.Clamp(
                    PagedLayoutSourceMapper.GetSourceOffsetForPlainText(
                        source,
                        ast,
                        fragment.SourceBlockIndex,
                        fragment.SourceTextStart),
                    0,
                    source.Length);
            }
            catch
            {
                starts[pageIndex] = null;
            }
        }

        var result = new List<SpreadPageRange>(layout.Pages.Count);
        for (var pageIndex = 0; pageIndex < layout.Pages.Count; pageIndex++)
        {
            var page = layout.Pages[pageIndex];
            var start = starts[pageIndex];
            if (start is null || page.IsBlank)
            {
                result.Add(new SpreadPageRange(pageIndex, page, 0, 0, Editable: false));
                continue;
            }

            var end = source.Length;
            for (var next = pageIndex + 1; next < starts.Length; next++)
            {
                if (starts[next] is int candidate && candidate > start.Value)
                {
                    end = candidate;
                    break;
                }
            }

            result.Add(new SpreadPageRange(
                pageIndex,
                page,
                start.Value,
                Math.Max(start.Value, end),
                Editable: true));
        }

        return result;
    }

    private static IReadOnlyList<SpreadPair> BuildSpreads(IReadOnlyList<SpreadPageRange> pages)
    {
        var output = new List<SpreadPair>();
        for (var index = 0; index < pages.Count;)
        {
            var page = pages[index];
            if (!page.Page.FacingPages)
            {
                output.Add(new SpreadPair(null, page));
                index++;
                continue;
            }

            if (page.Page.IsLeftPage)
            {
                SpreadPageRange? right = null;
                if (index + 1 < pages.Count &&
                    pages[index + 1].Page.FacingPages &&
                    !pages[index + 1].Page.IsLeftPage)
                {
                    right = pages[index + 1];
                }
                output.Add(new SpreadPair(page, right));
                index += right is null ? 1 : 2;
                continue;
            }

            output.Add(new SpreadPair(null, page));
            index++;
        }

        return output;
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

    private sealed record SpreadPair(SpreadPageRange? Left, SpreadPageRange? Right);

    internal sealed record SpreadPageRange(
        int PageIndex,
        PageLayoutPage Page,
        int StartOffset,
        int EndOffset,
        bool Editable);

    private static SolidColorBrush Brush(string value) => new(Color.Parse(value));
}

internal sealed class SpreadPageView : Grid
{
    private readonly Action<SpreadPageView> _caretChanged;
    private readonly Action<SpreadPageView> _selectionChanged;
    private readonly Action<SpreadPageView> _focused;
    private readonly Border _pageBorder = new()
    {
        Background = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.Parse("#555A62")),
        BorderThickness = new Thickness(1),
        BoxShadow = new BoxShadows(new BoxShadow
        {
            Blur = 14,
            OffsetY = 5,
            Color = Color.FromArgb(78, 0, 0, 0)
        })
    };
    private readonly Canvas _pageCanvas = new() { ClipToBounds = true };
    private readonly TextBlock _label = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        FontSize = 10.5,
        Opacity = .7,
        Margin = new Thickness(0, 0, 0, 6)
    };
    private readonly TextBlock _blank = new()
    {
        Text = "Blank page",
        Foreground = new SolidColorBrush(Color.Parse("#8A8F98")),
        FontStyle = FontStyle.Italic,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    private double _scale = .8;
    private bool _disposed;

    public SpreadPageView(
        AvaloniaEdit.Document.TextDocument sharedDocument,
        Action<SpreadPageView> caretChanged,
        Action<SpreadPageView> selectionChanged,
        Action<SpreadPageView> focused)
    {
        _caretChanged = caretChanged;
        _selectionChanged = selectionChanged;
        _focused = focused;

        RowDefinitions = new RowDefinitions("Auto,Auto");
        Children.Add(_label);
        Grid.SetRow(_pageBorder, 1);
        Children.Add(_pageBorder);
        _pageBorder.Child = _pageCanvas;

        Editor = new ManuscriptEditor(sharedDocument, projectionView: true)
        {
            ShowLineNumbers = false,
            WordWrap = true,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.Parse("#202124")),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden
        };
        Editor.Classes.Add("spread-page-editor");
        Editor.TextArea.Caret.PositionChanged += EditorCaretChanged;
        Editor.TextArea.SelectionChanged += EditorSelectionChanged;
        Editor.GotFocus += EditorGotFocus;
    }

    public ManuscriptEditor Editor { get; }
    public FacingSpreadSurface.SpreadPageRange? Range { get; private set; }

    public void Configure(
        FacingSpreadSurface.SpreadPageRange? range,
        BookStyle style,
        double scale,
        bool active)
    {
        if (_disposed) return;
        Range = range;
        _scale = scale;
        SetActive(active);

        if (range is null)
        {
            IsVisible = true;
            _label.Text = string.Empty;
            _pageBorder.Width = style.PageWidthInches * 72 * scale;
            _pageBorder.Height = style.PageHeightInches * 72 * scale;
            _pageCanvas.Width = _pageBorder.Width;
            _pageCanvas.Height = _pageBorder.Height;
            _pageCanvas.Children.Clear();
            _blank.Text = "No page";
            _pageCanvas.Children.Add(_blank);
            Canvas.SetLeft(_blank, Math.Max(0, (_pageCanvas.Width - 55) / 2));
            Canvas.SetTop(_blank, Math.Max(0, (_pageCanvas.Height - 20) / 2));
            return;
        }

        var page = range.Page;
        var pageWidth = page.WidthPoints * scale;
        var pageHeight = page.HeightPoints * scale;
        _label.Text = page.IsBlank
            ? $"Page {page.DisplayNumberText} • blank"
            : $"Page {page.DisplayNumberText}{(page.IsLeftPage ? " • left" : " • right")}";
        _pageBorder.Width = pageWidth;
        _pageBorder.Height = pageHeight;
        _pageCanvas.Width = pageWidth;
        _pageCanvas.Height = pageHeight;
        _pageCanvas.Children.Clear();

        DrawPageFurniture(page, style, scale);

        if (!range.Editable || page.IsBlank)
        {
            _blank.Text = "Blank page";
            _pageCanvas.Children.Add(_blank);
            Canvas.SetLeft(_blank, Math.Max(0, (pageWidth - 70) / 2));
            Canvas.SetTop(_blank, Math.Max(0, (pageHeight - 20) / 2));
            return;
        }

        var content = page.ContentBounds;
        var contentWidth = Math.Max(80, content.WidthPoints * scale);
        var contentHeight = Math.Max(80, content.HeightPoints * scale);

        Editor.FontFamily = new FontFamily(style.BodyFontFamily);
        Editor.FontSize = Math.Max(8, style.BodyFontSizePoints * scale);
        Editor.Foreground = SafeBrush(style.BodyColorHex, "#202124");
        Editor.Width = contentWidth;
        Editor.Height = contentHeight;
        Editor.MaxWidth = contentWidth;
        Editor.MinWidth = contentWidth;
        Editor.MaxHeight = contentHeight;
        Editor.HorizontalAlignment = HorizontalAlignment.Left;
        Editor.VerticalAlignment = VerticalAlignment.Top;

        _pageCanvas.Children.Add(Editor);
        Canvas.SetLeft(Editor, content.XPoints * scale);
        Canvas.SetTop(Editor, content.YPoints * scale);

        PositionViewport();
    }

    public void SetActive(bool active)
    {
        _pageBorder.BorderBrush = new SolidColorBrush(Color.Parse(active ? "#007ACC" : "#555A62"));
        _pageBorder.BorderThickness = new Thickness(active ? 2 : 1);
    }

    public void PositionViewport()
    {
        if (_disposed || Range is not { Editable: true } range) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || Range is not { Editable: true } current || current.PageIndex != range.PageIndex)
                return;

            var document = Editor.Document;
            if (document.TextLength == 0) return;

            try
            {
                var startOffset = Math.Clamp(range.StartOffset, 0, document.TextLength);
                var endOffset = Math.Clamp(range.EndOffset, startOffset, document.TextLength);
                var textView = Editor.TextArea.TextView;

                var startLocation = document.GetLocation(startOffset);
                var startPoint = textView.GetVisualPosition(
                    new TextViewPosition(startLocation.Line, startLocation.Column),
                    VisualYPosition.LineTop);

                var endLocation = document.GetLocation(endOffset);
                var endPoint = textView.GetVisualPosition(
                    new TextViewPosition(endLocation.Line, endLocation.Column),
                    VisualYPosition.LineBottom);

                var availableHeight = range.Page.ContentBounds.HeightPoints * _scale;
                var textHeight = Math.Max(
                    textView.DefaultLineHeight * 1.4,
                    endPoint.Y - startPoint.Y + textView.DefaultLineHeight);
                Editor.Height = Math.Min(availableHeight, textHeight);
                Editor.MaxHeight = availableHeight;

                ((IScrollable)Editor.TextArea).Offset = new Vector(0, Math.Max(0, startPoint.Y));
                textView.InvalidateMeasure();
                textView.Redraw();
            }
            catch
            {
                // A transient visual-line rebuild can race a keystroke. The next layout pass
                // repositions the shared view; editing remains attached to the same document.
            }
        }, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Editor.TextArea.Caret.PositionChanged -= EditorCaretChanged;
        Editor.TextArea.SelectionChanged -= EditorSelectionChanged;
        Editor.GotFocus -= EditorGotFocus;
    }

    private void EditorCaretChanged(object? sender, EventArgs e) => _caretChanged(this);
    private void EditorSelectionChanged(object? sender, EventArgs e) => _selectionChanged(this);
    private void EditorGotFocus(object? sender, Avalonia.Input.GotFocusEventArgs e) => _focused(this);

    private void DrawPageFurniture(PageLayoutPage page, BookStyle style, double scale)
    {
        var content = page.ContentBounds;
        var guideBrush = new SolidColorBrush(Color.FromArgb(45, 45, 126, 214));

        var leftGuide = new Border { Width = 1, Height = content.HeightPoints * scale, Background = guideBrush, IsHitTestVisible = false };
        Canvas.SetLeft(leftGuide, content.XPoints * scale);
        Canvas.SetTop(leftGuide, content.YPoints * scale);
        _pageCanvas.Children.Add(leftGuide);

        var rightGuide = new Border { Width = 1, Height = content.HeightPoints * scale, Background = guideBrush, IsHitTestVisible = false };
        Canvas.SetLeft(rightGuide, content.RightPoints * scale);
        Canvas.SetTop(rightGuide, content.YPoints * scale);
        _pageCanvas.Children.Add(rightGuide);

        var topGuide = new Border { Width = content.WidthPoints * scale, Height = 1, Background = guideBrush, IsHitTestVisible = false };
        Canvas.SetLeft(topGuide, content.XPoints * scale);
        Canvas.SetTop(topGuide, content.YPoints * scale);
        _pageCanvas.Children.Add(topGuide);

        var bottomGuide = new Border { Width = content.WidthPoints * scale, Height = 1, Background = guideBrush, IsHitTestVisible = false };
        Canvas.SetLeft(bottomGuide, content.XPoints * scale);
        Canvas.SetTop(bottomGuide, content.BottomPoints * scale);
        _pageCanvas.Children.Add(bottomGuide);

        if (style.ShowHeadersAndFooters && !page.IsBlank)
        {
            var header = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(style.HeaderCenter) ? string.Empty : style.HeaderCenter,
                Foreground = new SolidColorBrush(Color.Parse("#60646C")),
                FontSize = Math.Max(6, style.HeaderFooterFontSizePoints * scale),
                Width = content.WidthPoints * scale,
                TextAlignment = TextAlignment.Center,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(header, content.XPoints * scale);
            Canvas.SetTop(header, Math.Max(5, content.YPoints * scale * .35));
            _pageCanvas.Children.Add(header);
        }

        if (style.ShowPageNumbers && !page.IsBlank)
        {
            var number = new TextBlock
            {
                Text = page.DisplayNumberText,
                Foreground = new SolidColorBrush(Color.Parse("#60646C")),
                FontSize = Math.Max(6, style.HeaderFooterFontSizePoints * scale),
                Width = 48,
                TextAlignment = TextAlignment.Center,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(number, page.WidthPoints * scale / 2 - 24);
            Canvas.SetTop(number, page.HeightPoints * scale - Math.Max(19, 25 * scale));
            _pageCanvas.Children.Add(number);
        }
    }

    private static IBrush SafeBrush(string? value, string fallback)
    {
        try
        {
            return new SolidColorBrush(Color.Parse(string.IsNullOrWhiteSpace(value) ? fallback : value));
        }
        catch
        {
            return new SolidColorBrush(Color.Parse(fallback));
        }
    }
}
