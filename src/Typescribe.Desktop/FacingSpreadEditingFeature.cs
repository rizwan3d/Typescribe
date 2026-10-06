using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
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
    private DocumentAst? _ast;
    private string? _selectedFigureKey;
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

        _left = new SpreadPageView(
            mainEditor.Document,
            OnProjectionCaretChanged,
            OnProjectionSelectionChanged,
            OnProjectionFocused,
            OnFigureSelected,
            OnFigureMoved,
            OnFigureResized);
        _right = new SpreadPageView(
            mainEditor.Document,
            OnProjectionCaretChanged,
            OnProjectionSelectionChanged,
            OnProjectionFocused,
            OnFigureSelected,
            OnFigureMoved,
            OnFigureResized);
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
            _ast = null;
            _left.Configure(null, null, _viewModel.CurrentStyle, .8, false, _selectedFigureKey);
            _right.Configure(null, null, _viewModel.CurrentStyle, .8, false, _selectedFigureKey);
            _spreadLabel.Text = "Select a manuscript";
            UpdateNavigation();
            return;
        }

        try
        {
            var source = _mainEditor.Text ?? string.Empty;
            var ast = _parser.Parse(source);
            _ast = ast;
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

        _left.Configure(pair.Left, _ast, _viewModel.CurrentStyle, scale, ReferenceEquals(_activeView, _left), _selectedFigureKey);
        _right.Configure(pair.Right, _ast, _viewModel.CurrentStyle, scale, ReferenceEquals(_activeView, _right), _selectedFigureKey);

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

    private void OnFigureSelected(SpreadPageView view, FigureBlock figure, PageLayoutFragment fragment)
    {
        if (!_active) return;
        _activeView = view;
        _selectedFigureKey = FigureKey(figure);
        _left.SetSelectedFigure(_selectedFigureKey);
        _right.SetSelectedFigure(_selectedFigureKey);
        UpdateActivePageBorders();

        PageObjectSelectionHub.SelectFigure(new PageFigureSelection(
            figure.SourceLine,
            figure.Identifier,
            figure.Source,
            figure.Caption,
            figure.Layout,
            figure.Formatting?.AnchoredObject,
            fragment.Bounds.WidthPoints,
            fragment.Bounds.HeightPoints));
    }

    private void OnFigureMoved(
        SpreadPageView view,
        FigureBlock snapshot,
        PageLayoutFragment fragment,
        double deltaXPoints,
        double deltaYPoints)
    {
        if (!_active || (Math.Abs(deltaXPoints) < .1 && Math.Abs(deltaYPoints) < .1)) return;

        try
        {
            var source = _mainEditor.Text ?? string.Empty;
            var ast = _parser.Parse(source);
            var figure = FigureSourceEditor.FindFigure(ast, snapshot.SourceLine, snapshot.Identifier, snapshot.Source);
            if (figure is null) return;

            var current = figure.Formatting?.AnchoredObject;
            var anchored = new AnchoredObjectFormatting(
                current?.Id ?? figure.Identifier ?? $"figure-{figure.SourceLine}",
                current?.Placement ?? ToFloatPlacement(figure.Layout?.Placement),
                current?.Wrap ?? TextWrapMode.None,
                (current?.OffsetXPoints ?? 0) + deltaXPoints,
                (current?.OffsetYPoints ?? 0) + deltaYPoints,
                current?.WrapTopPoints ?? 0,
                current?.WrapRightPoints ?? 0,
                current?.WrapBottomPoints ?? 0,
                current?.WrapLeftPoints ?? 0,
                current?.KeepWithAnchor ?? true);

            var updated = RichBlockFormattingEditor.SetAnchoredObject(source, figure.SourceLine, anchored);
            _selectedFigureKey = FigureKey(figure);
            _viewModel.UpdateEditorText(updated);
            PageObjectSelectionHub.SelectFigure(new PageFigureSelection(
                figure.SourceLine,
                figure.Identifier,
                figure.Source,
                figure.Caption,
                figure.Layout,
                anchored,
                fragment.Bounds.WidthPoints,
                fragment.Bounds.HeightPoints));
        }
        catch
        {
            // The next layout pass restores the object from canonical source if a transient
            // pointer/update race occurs.
        }
    }

    private void OnFigureResized(
        SpreadPageView view,
        FigureBlock snapshot,
        PageLayoutFragment fragment,
        double columnWidthPoints,
        double widthPoints,
        double heightPoints)
    {
        if (!_active || columnWidthPoints <= 0) return;

        try
        {
            var source = _mainEditor.Text ?? string.Empty;
            var ast = _parser.Parse(source);
            var figure = FigureSourceEditor.FindFigure(ast, snapshot.SourceLine, snapshot.Identifier, snapshot.Source);
            if (figure is null) return;

            var widthPercent = Math.Clamp(widthPoints / columnWidthPoints * 100d, 10, 100);
            var captionHeight = string.IsNullOrWhiteSpace(figure.Caption)
                ? 0
                : _viewModel.CurrentStyle.CaptionFontSizePoints * 1.3;
            var imageHeightPoints = Math.Max(36, heightPoints - captionHeight);

            var layout = (figure.Layout ?? new FigureLayout()) with
            {
                WidthPercent = widthPercent,
                HeightInches = Math.Clamp(imageHeightPoints / 72d, .5, 24)
            };
            var updatedFigure = figure with { Layout = layout };
            var updated = FigureSourceEditor.ReplaceFigure(source, figure, updatedFigure);
            _selectedFigureKey = FigureKey(updatedFigure);
            _viewModel.UpdateEditorText(updated);

            PageObjectSelectionHub.SelectFigure(new PageFigureSelection(
                updatedFigure.SourceLine,
                updatedFigure.Identifier,
                updatedFigure.Source,
                updatedFigure.Caption,
                updatedFigure.Layout,
                updatedFigure.Formatting?.AnchoredObject,
                widthPoints,
                heightPoints));
        }
        catch
        {
            // Keep the last canonical figure if a transient layout update races the pointer.
        }
    }

    private static string FigureKey(FigureBlock figure)
        => !string.IsNullOrWhiteSpace(figure.Identifier)
            ? $"id:{figure.Identifier}"
            : $"source:{figure.Source}\u001f{figure.Caption}";

    private static FloatPlacementMode ToFloatPlacement(FigurePlacement? placement)
        => placement switch
        {
            FigurePlacement.Inline => FloatPlacementMode.Inline,
            FigurePlacement.Top => FloatPlacementMode.Top,
            FigurePlacement.Bottom => FloatPlacementMode.Bottom,
            FigurePlacement.Page => FloatPlacementMode.Page,
            _ => FloatPlacementMode.Here
        };

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
    private readonly Action<SpreadPageView, FigureBlock, PageLayoutFragment> _figureSelected;
    private readonly Action<SpreadPageView, FigureBlock, PageLayoutFragment, double, double> _figureMoved;
    private readonly Action<SpreadPageView, FigureBlock, PageLayoutFragment, double, double, double> _figureResized;
    private readonly List<FigureObjectOverlay> _figureObjects = [];
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
        Action<SpreadPageView> focused,
        Action<SpreadPageView, FigureBlock, PageLayoutFragment> figureSelected,
        Action<SpreadPageView, FigureBlock, PageLayoutFragment, double, double> figureMoved,
        Action<SpreadPageView, FigureBlock, PageLayoutFragment, double, double, double> figureResized)
    {
        _caretChanged = caretChanged;
        _selectionChanged = selectionChanged;
        _focused = focused;
        _figureSelected = figureSelected;
        _figureMoved = figureMoved;
        _figureResized = figureResized;

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
        DocumentAst? ast,
        BookStyle style,
        double scale,
        bool active,
        string? selectedFigureKey)
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
            ClearPageCanvas();
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

        RenderFigureObjects(page, ast, scale, selectedFigureKey);
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

                var endProbe = endOffset > startOffset ? endOffset - 1 : endOffset;
                var endLocation = document.GetLocation(endProbe);
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
        foreach (var figure in _figureObjects) figure.Dispose();
        _figureObjects.Clear();
    }

    public void SetSelectedFigure(string? key)
    {
        foreach (var figure in _figureObjects)
            figure.IsSelected = key is not null && string.Equals(figure.SelectionKey, key, StringComparison.Ordinal);
    }

    private void ClearPageCanvas()
    {
        foreach (var figure in _figureObjects) figure.Dispose();
        _figureObjects.Clear();
        _pageCanvas.Children.Clear();
    }

    private void RenderFigureObjects(
        PageLayoutPage page,
        DocumentAst? ast,
        double scale,
        string? selectedFigureKey)
    {
        if (ast is null) return;

        var fragments = page.Columns.SelectMany(static column => column.Fragments)
            .Concat(page.FloatingObjects)
            .Where(static fragment =>
                fragment.Kind is PageLayoutFragmentKind.Figure or
                    PageLayoutFragmentKind.FloatingObject or
                    PageLayoutFragmentKind.MarginNote)
            .ToArray();

        foreach (var fragment in fragments)
        {
            if (fragment.SourceBlockIndex < 0 || fragment.SourceBlockIndex >= ast.Blocks.Count ||
                ast.Blocks[fragment.SourceBlockIndex] is not FigureBlock figure)
                continue;

            var columnWidth = page.Columns
                .FirstOrDefault(column => column.Fragments.Any(candidate => candidate.Id == fragment.Id))
                ?.Bounds.WidthPoints ?? page.ContentBounds.WidthPoints;

            var overlay = new FigureObjectOverlay(
                _pageCanvas,
                figure,
                fragment,
                columnWidth,
                scale,
                () =>
                {
                    SetSelectedFigure(null);
                    _figureSelected(this, figure, fragment);
                },
                (dx, dy) => _figureMoved(this, figure, fragment, dx, dy),
                (width, height) => _figureResized(this, figure, fragment, columnWidth, width, height));

            overlay.IsSelected = selectedFigureKey is not null &&
                                 string.Equals(overlay.SelectionKey, selectedFigureKey, StringComparison.Ordinal);
            _figureObjects.Add(overlay);
            _pageCanvas.Children.Add(overlay);
            Canvas.SetLeft(overlay, fragment.Bounds.XPoints * scale);
            Canvas.SetTop(overlay, fragment.Bounds.YPoints * scale);
        }
    }

    private void EditorCaretChanged(object? sender, EventArgs e) => _caretChanged(this);
    private void EditorSelectionChanged(object? sender, EventArgs e) => _selectionChanged(this);
    private void EditorGotFocus(object? sender, GotFocusEventArgs e) => _focused(this);

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

internal sealed class FigureObjectOverlay : Grid, IDisposable
{
    private readonly Canvas _canvas;
    private readonly FigureBlock _figure;
    private readonly PageLayoutFragment _fragment;
    private readonly double _scale;
    private readonly Action _select;
    private readonly Action<double, double> _moveCommitted;
    private readonly Action<double, double> _resizeCommitted;
    private readonly Border _frame;
    private readonly Border _resizeHandle;
    private Bitmap? _bitmap;
    private bool _selected;
    private bool _dragging;
    private bool _resizing;
    private Point _startPointer;
    private double _startLeft;
    private double _startTop;
    private double _startWidth;
    private double _startHeight;

    public FigureObjectOverlay(
        Canvas canvas,
        FigureBlock figure,
        PageLayoutFragment fragment,
        double columnWidthPoints,
        double scale,
        Action select,
        Action<double, double> moveCommitted,
        Action<double, double> resizeCommitted)
    {
        _canvas = canvas;
        _figure = figure;
        _fragment = fragment;
        _scale = scale;
        _select = select;
        _moveCommitted = moveCommitted;
        _resizeCommitted = resizeCommitted;

        Width = Math.Max(24, fragment.Bounds.WidthPoints * scale);
        Height = Math.Max(24, fragment.Bounds.HeightPoints * scale);
        ClipToBounds = false;

        _frame = new Border
        {
            Background = Brushes.White,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(1),
            ClipToBounds = true,
            Child = BuildContent(figure)
        };
        Children.Add(_frame);

        _resizeHandle = new Border
        {
            Width = 10,
            Height = 10,
            Background = new SolidColorBrush(Color.Parse("#007ACC")),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -5, -5),
            IsVisible = false
        };
        Children.Add(_resizeHandle);

        PointerPressed += BodyPointerPressed;
        PointerMoved += BodyPointerMoved;
        PointerReleased += BodyPointerReleased;
        _resizeHandle.PointerPressed += ResizePointerPressed;
        _resizeHandle.PointerMoved += ResizePointerMoved;
        _resizeHandle.PointerReleased += ResizePointerReleased;

        ToolTip.SetTip(this, "Click to select • drag to move • drag blue handle to resize");
    }

    public string SelectionKey
        => !string.IsNullOrWhiteSpace(_figure.Identifier)
            ? $"id:{_figure.Identifier}"
            : $"source:{_figure.Source}\u001f{_figure.Caption}";

    public bool IsSelected
    {
        get => _selected;
        set
        {
            _selected = value;
            _frame.BorderBrush = new SolidColorBrush(Color.Parse(value ? "#007ACC" : "#D8D8D8"));
            _frame.BorderThickness = new Thickness(value ? 2 : 1);
            _resizeHandle.IsVisible = value;
        }
    }

    public void Dispose()
    {
        PointerPressed -= BodyPointerPressed;
        PointerMoved -= BodyPointerMoved;
        PointerReleased -= BodyPointerReleased;
        _resizeHandle.PointerPressed -= ResizePointerPressed;
        _resizeHandle.PointerMoved -= ResizePointerMoved;
        _resizeHandle.PointerReleased -= ResizePointerReleased;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private Control BuildContent(FigureBlock figure)
    {
        var grid = new Grid
        {
            Background = Brushes.White,
            RowDefinitions = string.IsNullOrWhiteSpace(figure.Caption)
                ? new RowDefinitions("*")
                : new RowDefinitions("*,Auto")
        };

        var image = TryLoadImage(figure);
        if (image is not null)
        {
            grid.Children.Add(image);
        }
        else
        {
            grid.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#F2F3F5")),
                Child = new TextBlock
                {
                    Text = figure.Source,
                    Foreground = new SolidColorBrush(Color.Parse("#60646C")),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(8),
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }

        if (!string.IsNullOrWhiteSpace(figure.Caption))
        {
            var caption = new TextBlock
            {
                Text = figure.Caption,
                Foreground = new SolidColorBrush(Color.Parse("#30343A")),
                FontSize = 9,
                FontStyle = FontStyle.Italic,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(5, 3, 5, 4)
            };
            Grid.SetRow(caption, 1);
            grid.Children.Add(caption);
        }

        return grid;
    }

    private Image? TryLoadImage(FigureBlock figure)
    {
        if (figure.SourceKind == FigureSourceKind.ExternalUri || string.IsNullOrWhiteSpace(figure.Source))
            return null;

        var project = TrackingProjectRepository.ActiveInstance?.CurrentProject;
        if (project is null) return null;

        var source = figure.Source.Trim();
        var path = Path.IsPathRooted(source)
            ? source
            : Path.Combine(project.RootPath, source.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return null;

        try
        {
            _bitmap = new Bitmap(path);
            return new Image
            {
                Source = _bitmap,
                Stretch = figure.Layout?.Fit switch
                {
                    FigureFitMode.Native => Stretch.None,
                    FigureFitMode.Cover => Stretch.UniformToFill,
                    FigureFitMode.Fill => Stretch.Fill,
                    _ => Stretch.Uniform
                }
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void BodyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_resizing || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _select();
        IsSelected = true;
        _dragging = true;
        _startPointer = e.GetPosition(_canvas);
        _startLeft = Canvas.GetLeft(this);
        _startTop = Canvas.GetTop(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void BodyPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || _resizing) return;
        var point = e.GetPosition(_canvas);
        var dx = point.X - _startPointer.X;
        var dy = point.Y - _startPointer.Y;
        Canvas.SetLeft(this, _startLeft + dx);
        Canvas.SetTop(this, _startTop + dy);
        e.Handled = true;
    }

    private void BodyPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging || _resizing) return;
        _dragging = false;
        var left = Canvas.GetLeft(this);
        var top = Canvas.GetTop(this);
        e.Pointer.Capture(null);
        e.Handled = true;
        _moveCommitted((left - _startLeft) / _scale, (top - _startTop) / _scale);
    }

    private void ResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_resizeHandle).Properties.IsLeftButtonPressed) return;
        _select();
        IsSelected = true;
        _dragging = false;
        _resizing = true;
        _startPointer = e.GetPosition(_canvas);
        _startWidth = Width;
        _startHeight = Height;
        e.Pointer.Capture(_resizeHandle);
        e.Handled = true;
    }

    private void ResizePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizing) return;
        var point = e.GetPosition(_canvas);
        var dx = point.X - _startPointer.X;
        var dy = point.Y - _startPointer.Y;
        Width = Math.Max(36, _startWidth + dx);
        Height = Math.Max(36, _startHeight + dy);
        e.Handled = true;
    }

    private void ResizePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        e.Pointer.Capture(null);
        e.Handled = true;
        _resizeCommitted(Width / _scale, Height / _scale);
    }
}
