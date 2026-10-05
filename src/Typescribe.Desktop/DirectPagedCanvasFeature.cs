using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Application.Abstractions;
using Typescribe.Application.Services;
using Typescribe.Desktop.Editing;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

/// <summary>
/// Makes PagedLayoutEngine the primary visual projection for manuscript editing.
/// Each page is rebuilt from canonical Markdown. Clicking a laid-out fragment opens an
/// in-place Markdown editor for that exact AST source block; committing replaces only that
/// source span and immediately repaginates. The existing ManuscriptEditor remains mounted
/// as the full-source editing surface and as the compatibility target for legacy commands.
/// </summary>
internal sealed class DirectPagedCanvasFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly IDocumentParser _parser;
    private bool _installed;
    private bool _queued;
    private bool _disposed;

    private DirectPagedCanvasFeature(
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

        var feature = new DirectPagedCanvasFeature(window, viewModel, parser);
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
        if (host is null || host.Children.OfType<EditablePagedCanvas>().Any()) return;

        var editor = host.Children.OfType<ManuscriptEditor>().FirstOrDefault();
        if (editor is null) return;

        var canvas = new EditablePagedCanvas(_window, _viewModel, _parser, editor);
        Grid.SetRow(canvas, 3);
        Grid.SetColumn(canvas, 0);
        host.Children.Add(canvas);

        AddCanvasControls(host, canvas);
        canvas.ShowPages();

        _installed = true;
        _window.LayoutUpdated -= WindowReady;
    }

    private static void AddCanvasControls(LongFormEditorChrome host, EditablePagedCanvas canvas)
    {
        if (host.CommandBar.Child is not WrapPanel wrap ||
            wrap.Children.OfType<Border>().Any(border => border.Classes.Contains("paged-canvas-group")))
            return;

        var pages = CanvasToggle("Pages", "Edit directly on paginated pages");
        var source = CanvasToggle("Source", "Edit the full Markdown source");
        var spread = CanvasToggle("Spread", "Show facing-page spreads");
        pages.IsChecked = true;
        spread.IsChecked = true;

        pages.Click += (_, _) =>
        {
            pages.IsChecked = true;
            source.IsChecked = false;
            canvas.ShowPages();
        };
        source.Click += (_, _) =>
        {
            source.IsChecked = true;
            pages.IsChecked = false;
            canvas.ShowSource();
        };
        spread.Click += (_, _) => canvas.SetSpreadMode(spread.IsChecked == true);

        var zoomOut = SmallButton("−", "Zoom page canvas out");
        var zoomIn = SmallButton("+", "Zoom page canvas in");
        var fit = SmallButton("Fit", "Fit page spread");
        zoomOut.Click += (_, _) => canvas.ChangeZoom(-.06);
        zoomIn.Click += (_, _) => canvas.ChangeZoom(.06);
        fit.Click += (_, _) => canvas.FitZoom();

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "CANVAS",
                    FontSize = 8.5,
                    FontWeight = FontWeight.SemiBold,
                    Opacity = .5,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(1, 0, 4, 0)
                },
                pages,
                source,
                spread,
                zoomOut,
                zoomIn,
                fit
            }
        };

        var group = new Border
        {
            Child = row,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(3, 2),
            Margin = new Thickness(0, 0, 4, 2)
        };
        group.Classes.Add("editor-command-group");
        group.Classes.Add("paged-canvas-group");
        wrap.Children.Insert(0, group);
    }

    private static ToggleButton CanvasToggle(string text, string tip)
    {
        var button = new ToggleButton
        {
            Content = text,
            MinWidth = 48,
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(7, 2),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Classes.Add("editor-command-button");
        ToolTip.SetTip(button, tip);
        return button;
    }

    private static Button SmallButton(string text, string tip)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = text.Length > 1 ? 38 : 28,
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(6, 2)
        };
        button.Classes.Add("editor-command-button");
        ToolTip.SetTip(button, tip);
        return button;
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= WindowReady;
        _window.LayoutUpdated -= WindowReady;
        _window.Closed -= WindowClosed;
    }
}

internal sealed class EditablePagedCanvas : Grid
{
    private const double DefaultScale = .76;
    private const double MinimumScale = .48;
    private const double MaximumScale = 1.18;

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly IDocumentParser _parser;
    private readonly ManuscriptEditor _sourceEditor;
    private readonly PagedLayoutEngine _engine = new();
    private readonly StackPanel _pageRows = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
        Spacing = 18
    };
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _status = new()
    {
        FontSize = 10.5,
        Opacity = .66,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly List<Bitmap> _bitmaps = [];

    private DocumentAst? _ast;
    private PagedLayoutResult? _layout;
    private string _renderedSource = string.Empty;
    private string? _renderedDocumentId;
    private bool _spreadMode = true;
    private bool _pagesVisible = true;
    private bool _refreshQueued;
    private bool _refreshDeferred;
    private bool _committing;
    private bool _finishingEdit;
    private bool _disposed;
    private double _scale = DefaultScale;

    private TextBox? _activeEditor;
    private Border? _activeFragment;
    private Canvas? _activeCanvas;
    private int _activeBlockIndex = -1;
    private string _activeOriginal = string.Empty;

    public EditablePagedCanvas(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        IDocumentParser parser,
        ManuscriptEditor sourceEditor)
    {
        _window = window;
        _viewModel = viewModel;
        _parser = parser;
        _sourceEditor = sourceEditor;

        Background = Brush("#E9EBEE");
        ClipToBounds = true;
        RowDefinitions = new RowDefinitions("*,Auto");

        _scroll = new ScrollViewer
        {
            Content = _pageRows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(26, 18, 26, 32)
        };
        Children.Add(_scroll);

        var footer = new Border
        {
            Background = Brush("#F8F9FB"),
            BorderBrush = Brush("#D5D9E0"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(10, 4),
            Child = _status
        };
        Grid.SetRow(footer, 1);
        Children.Add(footer);

        _viewModel.StateChanged += WorkspaceStateChanged;
        DetachedFromVisualTree += (_, _) => DisposeCanvas();
        Refresh();
    }

    public void ShowPages()
    {
        if (_disposed) return;
        CommitActiveEdit();
        _pagesVisible = true;
        IsVisible = true;
        _sourceEditor.IsVisible = false;
        Refresh();
    }

    public void ShowSource()
    {
        if (_disposed) return;
        CommitActiveEdit();
        _pagesVisible = false;
        IsVisible = false;
        _sourceEditor.IsVisible = true;
        _sourceEditor.Focus();
    }

    public void SetSpreadMode(bool enabled)
    {
        if (_spreadMode == enabled) return;
        CommitActiveEdit();
        _spreadMode = enabled;
        RenderPages();
    }

    public void ChangeZoom(double delta)
    {
        CommitActiveEdit();
        _scale = Math.Clamp(_scale + delta, MinimumScale, MaximumScale);
        RenderPages();
    }

    public void FitZoom()
    {
        CommitActiveEdit();
        var viewport = _scroll.Bounds.Width;
        if (viewport <= 0 || _layout?.Pages.Count == 0)
        {
            _scale = DefaultScale;
        }
        else
        {
            var sample = _layout.Pages[0];
            var pageCount = _spreadMode && sample.FacingPages ? 2 : 1;
            var available = Math.Max(300, viewport - 90);
            _scale = Math.Clamp(
                available / (sample.WidthPoints * pageCount + (pageCount - 1) * 24),
                MinimumScale,
                MaximumScale);
        }
        RenderPages();
    }

    private void WorkspaceStateChanged(object? sender, EventArgs e)
    {
        if (_disposed || !_pagesVisible) return;

        var documentId = _viewModel.SelectedRow?.Node.PersistentId;
        var sourceChanged = !string.Equals(_viewModel.EditorText, _renderedSource, StringComparison.Ordinal);
        var documentChanged = !string.Equals(documentId, _renderedDocumentId, StringComparison.Ordinal);

        if (_activeEditor is not null)
        {
            if (!_committing && (documentChanged || sourceChanged))
            {
                CancelActiveEdit();
                QueueRefresh();
            }
            else
            {
                _refreshDeferred = true;
            }
            return;
        }

        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (_disposed || _refreshQueued || !_pagesVisible) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            if (!_disposed && _pagesVisible) Refresh();
        }, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        if (_disposed || !_pagesVisible) return;
        DisposeBitmaps();

        if (!_viewModel.HasDocument)
        {
            _ast = null;
            _layout = null;
            _renderedSource = string.Empty;
            _renderedDocumentId = null;
            _pageRows.Children.Clear();
            _status.Text = "Select a manuscript document to edit in pages.";
            return;
        }

        try
        {
            _renderedSource = _viewModel.EditorText ?? string.Empty;
            _renderedDocumentId = _viewModel.SelectedRow?.Node.PersistentId;
            _ast = _parser.Parse(_renderedSource);
            _layout = _engine.Paginate(_ast, _viewModel.CurrentStyle);
            RenderPages();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            _pageRows.Children.Clear();
            _status.Text = $"Paged editing unavailable: {ex.Message}";
        }
    }

    private void RenderPages()
    {
        if (_disposed || !_pagesVisible) return;
        DisposeBitmaps();
        _pageRows.Children.Clear();

        if (_layout is null || _layout.Pages.Count == 0) return;

        if (!_spreadMode)
        {
            foreach (var page in _layout.Pages)
                _pageRows.Children.Add(BuildSpreadRow(null, page));
            return;
        }

        for (var index = 0; index < _layout.Pages.Count;)
        {
            var page = _layout.Pages[index];
            if (!page.FacingPages)
            {
                _pageRows.Children.Add(BuildSpreadRow(null, page));
                index++;
                continue;
            }

            if (page.IsLeftPage)
            {
                var right = index + 1 < _layout.Pages.Count &&
                            _layout.Pages[index + 1].FacingPages &&
                            !_layout.Pages[index + 1].IsLeftPage
                    ? _layout.Pages[index + 1]
                    : null;
                _pageRows.Children.Add(BuildSpreadRow(page, right));
                index += right is null ? 1 : 2;
            }
            else
            {
                _pageRows.Children.Add(BuildSpreadRow(null, page));
                index++;
            }
        }
    }

    private Control BuildSpreadRow(PageLayoutPage? left, PageLayoutPage? right)
    {
        var page = left ?? right!;
        var renderedWidth = page.WidthPoints * _scale;
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Spacing = 18
        };

        if (left is null && right?.FacingPages == true)
            row.Children.Add(PagePlaceholder(renderedWidth, right.HeightPoints * _scale));

        if (left is not null)
            row.Children.Add(BuildPage(left));
        if (right is not null)
            row.Children.Add(BuildPage(right));

        return row;
    }

    private static Border PagePlaceholder(double width, double height)
        => new()
        {
            Width = width,
            Height = height,
            Background = Brushes.Transparent
        };

    private Control BuildPage(PageLayoutPage page)
    {
        var width = page.WidthPoints * _scale;
        var height = page.HeightPoints * _scale;
        var canvas = new Canvas
        {
            Width = width,
            Height = height,
            Background = Brushes.White,
            ClipToBounds = true
        };

        DrawPageFurniture(canvas, page);

        foreach (var column in page.Columns)
        {
            foreach (var fragment in column.Fragments)
                canvas.Children.Add(BuildFragment(canvas, fragment));
        }
        foreach (var fragment in page.FloatingObjects)
            canvas.Children.Add(BuildFragment(canvas, fragment));
        foreach (var fragment in page.Footnotes)
            canvas.Children.Add(BuildFragment(canvas, fragment));

        var surface = new Border
        {
            Child = canvas,
            Width = width,
            Height = height,
            Background = Brushes.White,
            BorderBrush = Brush("#CDD2D9"),
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                Blur = 13,
                OffsetY = 4,
                Color = Color.FromArgb(48, 28, 36, 48)
            })
        };

        ToolTip.SetTip(surface,
            $"Page {page.DisplayNumberText}" +
            (page.FacingPages ? page.IsLeftPage ? " • left page" : " • right page" : string.Empty));

        return surface;
    }

    private Control BuildFragment(Canvas canvas, PageLayoutFragment fragment)
    {
        var width = Math.Max(4, fragment.Bounds.WidthPoints * _scale);
        var height = Math.Max(4, fragment.Bounds.HeightPoints * _scale);

        var content = BuildFragmentContent(fragment);
        var border = new Border
        {
            Child = content,
            Width = width,
            Height = height,
            Background = FragmentBackground(fragment),
            BorderBrush = FragmentBorder(fragment),
            BorderThickness = fragment.Kind is PageLayoutFragmentKind.TextFrame or
                               PageLayoutFragmentKind.FloatingObject or
                               PageLayoutFragmentKind.MarginNote or
                               PageLayoutFragmentKind.OversetIndicator
                ? new Thickness(1)
                : new Thickness(0),
            CornerRadius = new CornerRadius(1),
            ClipToBounds = true,
            Cursor = new Cursor(StandardCursorType.Ibeam)
        };

        ToolTip.SetTip(border,
            $"{fragment.Kind} • line {fragment.SourceLine} • click to edit" +
            (fragment.IsContinuation ? " • continued" : string.Empty));

        border.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            BeginEdit(canvas, border, fragment);
        };

        Canvas.SetLeft(border, fragment.Bounds.XPoints * _scale);
        Canvas.SetTop(border, fragment.Bounds.YPoints * _scale);
        return border;
    }

    private Control BuildFragmentContent(PageLayoutFragment fragment)
    {
        if (fragment.Kind == PageLayoutFragmentKind.OversetIndicator)
        {
            return new TextBlock
            {
                Text = "+",
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        if (_ast is not null &&
            fragment.SourceBlockIndex >= 0 &&
            fragment.SourceBlockIndex < _ast.Blocks.Count &&
            _ast.Blocks[fragment.SourceBlockIndex] is FigureBlock figure)
        {
            var visual = TryBuildFigure(figure);
            if (visual is not null) return visual;
        }

        var block = _ast is not null &&
                    fragment.SourceBlockIndex >= 0 &&
                    fragment.SourceBlockIndex < _ast.Blocks.Count
            ? _ast.Blocks[fragment.SourceBlockIndex]
            : null;

        var text = new TextBlock
        {
            Text = fragment.Text,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = Math.Max(1, (int)Math.Floor(Math.Max(10, fragment.Bounds.HeightPoints) / 11)),
            Foreground = Brush("#252A32"),
            Margin = new Thickness(1.5, 0),
            FontSize = Math.Max(7.5, 11.2 * _scale)
        };

        if (block is HeadingBlock heading)
        {
            text.FontWeight = FontWeight.SemiBold;
            text.FontSize = Math.Max(9, (17 - Math.Min(heading.Level, 5) * 1.5) * _scale);
        }
        else if (fragment.Kind == PageLayoutFragmentKind.Quote)
        {
            text.FontStyle = FontStyle.Italic;
            text.Opacity = .82;
        }
        else if (fragment.Kind == PageLayoutFragmentKind.Footnote)
        {
            text.FontSize = Math.Max(6.5, 8.5 * _scale);
            text.Opacity = .78;
        }

        return text;
    }

    private Control? TryBuildFigure(FigureBlock figure)
    {
        var project = TrackingProjectRepository.ActiveInstance?.CurrentProject;
        if (project is null || string.IsNullOrWhiteSpace(figure.Source)) return null;

        var source = figure.Source.Trim();
        if (source.Contains("://", StringComparison.Ordinal)) return null;

        var candidate = Path.IsPathRooted(source)
            ? source
            : Path.Combine(project.RootPath, source.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(candidate)) return null;

        try
        {
            var bitmap = new Bitmap(candidate);
            _bitmaps.Add(bitmap);

            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(2)
            };

            if (string.IsNullOrWhiteSpace(figure.Caption))
                return image;

            return new Grid
            {
                RowDefinitions = new RowDefinitions("*,Auto"),
                Children =
                {
                    image,
                    Caption(figure.Caption)
                }
            };

            static TextBlock Caption(string value)
            {
                var caption = new TextBlock
                {
                    Text = value,
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle = FontStyle.Italic,
                    FontSize = 8.5,
                    Margin = new Thickness(4, 2, 4, 3),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                Grid.SetRow(caption, 1);
                return caption;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void BeginEdit(Canvas canvas, Border fragmentBorder, PageLayoutFragment fragment)
    {
        if (_ast is null || fragment.SourceBlockIndex < 0 || fragment.SourceBlockIndex >= _ast.Blocks.Count)
            return;

        if (_activeEditor is not null)
            CommitActiveEdit();

        PagedLayoutSourceMapper.EditableSpan span;
        try
        {
            span = PagedLayoutSourceMapper.GetEditableSpan(
                _renderedSource,
                _ast,
                fragment.SourceBlockIndex);
        }
        catch (Exception ex)
        {
            _status.Text = $"Cannot edit this fragment: {ex.Message}";
            return;
        }

        var left = fragment.Bounds.XPoints * _scale;
        var top = fragment.Bounds.YPoints * _scale;
        var width = Math.Max(90, fragment.Bounds.WidthPoints * _scale);
        var desiredHeight = Math.Max(58, fragment.Bounds.HeightPoints * _scale);
        var height = Math.Min(
            Math.Max(desiredHeight, Math.Min(190, 34 + CountLines(span.Text) * 18)),
            Math.Max(58, canvas.Height - top - 4));

        var editor = new TextBox
        {
            Text = span.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily(_viewModel.CurrentStyle.BodyFontFamily),
            FontSize = Math.Max(10, 12 * _scale),
            Background = Brushes.White,
            Foreground = Brush("#171B21"),
            BorderBrush = Brush("#2F80ED"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 4),
            Width = width,
            Height = height,
            MinHeight = 58,
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        editor.KeyDown += ActiveEditorKeyDown;
        editor.LostFocus += ActiveEditorLostFocus;

        _activeEditor = editor;
        _activeFragment = fragmentBorder;
        _activeCanvas = canvas;
        _activeBlockIndex = fragment.SourceBlockIndex;
        _activeOriginal = span.Text;
        fragmentBorder.IsVisible = false;

        Canvas.SetLeft(editor, left);
        Canvas.SetTop(editor, top);
        canvas.Children.Add(editor);

        _status.Text =
            $"Editing source line {span.StartLine}" +
            (span.EndLine > span.StartLine ? $"–{span.EndLine}" : string.Empty) +
            " • Ctrl/Cmd+Enter reflows pages • Esc cancels";

        Dispatcher.UIThread.Post(() =>
        {
            editor.Focus();
            editor.CaretIndex = Math.Clamp(editor.Text?.Length ?? 0, 0, editor.Text?.Length ?? 0);
        }, DispatcherPriority.Input);
    }

    private void ActiveEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelActiveEdit();
            return;
        }

        var primary = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                      e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (primary && e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitActiveEdit();
        }
    }

    private void ActiveEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_finishingEdit) return;
        CommitActiveEdit();
    }

    private void CommitActiveEdit()
    {
        if (_activeEditor is null || _ast is null || _activeBlockIndex < 0 || _finishingEdit)
            return;

        _finishingEdit = true;
        try
        {
            var replacement = _activeEditor.Text ?? string.Empty;
            var changed = !string.Equals(replacement, _activeOriginal, StringComparison.Ordinal);
            RemoveActiveEditor();

            if (changed)
            {
                var updated = PagedLayoutSourceMapper.ReplaceBlock(
                    _renderedSource,
                    _ast,
                    _activeBlockIndex,
                    replacement);

                _committing = true;
                try
                {
                    _viewModel.UpdateEditorText(updated);
                    _renderedSource = updated;
                }
                finally
                {
                    _committing = false;
                }
            }

            Refresh();
            if (_refreshDeferred)
            {
                _refreshDeferred = false;
                QueueRefresh();
            }
        }
        finally
        {
            _finishingEdit = false;
        }
    }

    private void CancelActiveEdit()
    {
        if (_activeEditor is null || _finishingEdit) return;

        _finishingEdit = true;
        try
        {
            RemoveActiveEditor();
            _status.Text = "Edit cancelled.";
            if (_refreshDeferred)
            {
                _refreshDeferred = false;
                QueueRefresh();
            }
        }
        finally
        {
            _finishingEdit = false;
        }
    }

    private void RemoveActiveEditor()
    {
        var editor = _activeEditor;
        if (editor is not null)
        {
            editor.KeyDown -= ActiveEditorKeyDown;
            editor.LostFocus -= ActiveEditorLostFocus;
            _activeCanvas?.Children.Remove(editor);
        }

        if (_activeFragment is not null)
            _activeFragment.IsVisible = true;

        _activeEditor = null;
        _activeFragment = null;
        _activeCanvas = null;
        _activeBlockIndex = -1;
        _activeOriginal = string.Empty;
    }

    private void DrawPageFurniture(Canvas canvas, PageLayoutPage page)
    {
        foreach (var guide in page.Guides)
        {
            var line = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(28, 62, 125, 210))
            };
            if (guide.Orientation == PageLayoutGuideOrientation.Vertical)
            {
                line.Width = 1;
                line.Height = page.HeightPoints * _scale;
                Canvas.SetLeft(line, guide.PositionPoints * _scale);
            }
            else
            {
                line.Width = page.WidthPoints * _scale;
                line.Height = 1;
                Canvas.SetTop(line, guide.PositionPoints * _scale);
            }
            canvas.Children.Add(line);
        }

        if (page.BaselineGrid is { Enabled: true } grid && grid.IncrementPoints > 0)
        {
            var start = page.ContentBounds.YPoints + Math.Max(0, grid.StartPoints);
            for (var y = start; y < page.ContentBounds.BottomPoints; y += grid.IncrementPoints)
            {
                var line = new Border
                {
                    Width = page.ContentBounds.WidthPoints * _scale,
                    Height = 1,
                    Background = new SolidColorBrush(Color.FromArgb(13, 46, 128, 200))
                };
                Canvas.SetLeft(line, page.ContentBounds.XPoints * _scale);
                Canvas.SetTop(line, y * _scale);
                canvas.Children.Add(line);
            }
        }

        if (_viewModel.CurrentStyle.ShowHeadersAndFooters && !page.IsBlank)
        {
            var header = new TextBlock
            {
                Text = _viewModel.SelectedTitle,
                FontSize = Math.Max(6.5, 8 * _scale),
                Opacity = .5,
                Width = page.ContentBounds.WidthPoints * _scale,
                TextAlignment = page.IsLeftPage ? TextAlignment.Left : TextAlignment.Right
            };
            Canvas.SetLeft(header, page.ContentBounds.XPoints * _scale);
            Canvas.SetTop(header, Math.Max(4, page.ContentBounds.YPoints * _scale * .32));
            canvas.Children.Add(header);
        }

        if (_viewModel.CurrentStyle.ShowPageNumbers && !page.IsBlank)
        {
            var number = new TextBlock
            {
                Text = page.DisplayNumberText,
                FontSize = Math.Max(7, 9 * _scale),
                Opacity = .62,
                Width = 56,
                TextAlignment = TextAlignment.Center
            };
            Canvas.SetLeft(number, page.WidthPoints * _scale / 2 - 28);
            Canvas.SetTop(number, page.HeightPoints * _scale - Math.Max(18, 24 * _scale));
            canvas.Children.Add(number);
        }
    }

    private void UpdateStatus()
    {
        if (_layout is null) return;
        var fragments = _layout.Pages.Sum(page =>
            page.Columns.Sum(column => column.Fragments.Count) +
            page.FloatingObjects.Count +
            page.Footnotes.Count);
        var warning = _layout.Warnings.Count == 0
            ? "No layout warnings"
            : $"{_layout.Warnings.Count} layout warning{(_layout.Warnings.Count == 1 ? string.Empty : "s")}";
        _status.Text =
            $"{_layout.Pages.Count} page{(_layout.Pages.Count == 1 ? string.Empty : "s")} • " +
            $"{fragments} fragments • {warning} • click text to edit";
    }

    private void DisposeBitmaps()
    {
        foreach (var bitmap in _bitmaps)
            bitmap.Dispose();
        _bitmaps.Clear();
    }

    private void DisposeCanvas()
    {
        if (_disposed) return;
        _disposed = true;
        _viewModel.StateChanged -= WorkspaceStateChanged;
        DisposeBitmaps();
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0) return 1;
        var count = 1;
        foreach (var ch in text)
            if (ch == '\n') count++;
        return count;
    }

    private static IBrush FragmentBorder(PageLayoutFragment fragment)
        => fragment.Kind switch
        {
            PageLayoutFragmentKind.TextFrame => Brush("#7AA7D9"),
            PageLayoutFragmentKind.FloatingObject or PageLayoutFragmentKind.MarginNote => Brush("#D99A4E"),
            PageLayoutFragmentKind.TableHeader or PageLayoutFragmentKind.TableRow => Brush("#B9C0CA"),
            PageLayoutFragmentKind.OversetIndicator => Brush("#B42318"),
            _ => Brushes.Transparent
        };

    private static IBrush FragmentBackground(PageLayoutFragment fragment)
        => fragment.Kind switch
        {
            PageLayoutFragmentKind.OversetIndicator => Brush("#B42318"),
            PageLayoutFragmentKind.TextFrame => new SolidColorBrush(Color.FromArgb(16, 47, 128, 237)),
            PageLayoutFragmentKind.FloatingObject or PageLayoutFragmentKind.MarginNote =>
                new SolidColorBrush(Color.FromArgb(16, 217, 154, 78)),
            PageLayoutFragmentKind.TableHeader =>
                new SolidColorBrush(Color.FromArgb(20, 105, 115, 134)),
            _ => Brushes.Transparent
        };

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}
