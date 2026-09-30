using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PDFtoImage;
using SkiaSharp;
using Typescribe.Application.Services;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;
using Typescribe.Infrastructure.Services;

namespace Typescribe.Desktop;

/// <summary>
/// Continuous, virtualized PDF preview with thumbnails and optional SyncTeX editor/PDF navigation.
/// Only a small page window and nearby thumbnails are rasterized at any time.
/// </summary>
internal sealed class ContinuousPdfPreviewFeature
{
    private const string TinyTexVersion = "2026.09";
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly FileSystemProjectRepository _repository = new();
    private readonly SourceMappedDocumentRenderer _sourceRenderer = new();
    private readonly AdvancedDocumentParser _parser = new();

    private readonly ComboBox _scopeBox = new()
    {
        ItemsSource = new[] { "Document", "Heading", "Whole Book" },
        SelectedIndex = 0,
        MinWidth = 112,
        Height = 28
    };
    private readonly ComboBox _headingBox = new() { MinWidth = 180, Height = 28 };
    private readonly Button _previous = new() { Content = "‹", Width = 32, Height = 28 };
    private readonly Button _next = new() { Content = "›", Width = 32, Height = 28 };
    private readonly Button _zoomOut = new() { Content = "−", Width = 32, Height = 28 };
    private readonly Button _zoomIn = new() { Content = "+", Width = 32, Height = 28 };
    private readonly CheckBox _syncBox = new() { Content = "Sync", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0) };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.68 };
    private readonly StackPanel _pageStack = new() { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _thumbStack = new() { Spacing = 5, Margin = new Thickness(5) };
    private readonly ScrollViewer _mainScroll;
    private readonly ScrollViewer _thumbScroll;

    private readonly Dictionary<int, Bitmap> _pageBitmaps = [];
    private readonly Dictionary<int, Bitmap> _thumbBitmaps = [];
    private readonly List<Button> _thumbButtons = [];
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _thumbCts;
    private CancellationTokenSource? _syncBuildCts;
    private CancellationTokenSource? _caretSyncCts;
    private TabItem? _pdfTab;
    private TabControl? _centerTabs;
    private Avalonia.Controls.TextBox? _editor;
    private string? _pdfPath;
    private long _version = -1;
    private int _pageCount;
    private int _currentPage;
    private int _windowStart = -1;
    private int _windowEnd = -1;
    private double _aspect = 1.45;
    private double _zoom = 1.0;
    private bool _buildingSurface;
    private bool _syncingScope;
    private bool _syncingEditor;
    private bool _installed;
    private bool _disposed;
    private SyncTexIndex? _syncIndex;
    private SourceContext? _scopeContext;

    private ContinuousPdfPreviewFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
        _mainScroll = new ScrollViewer
        {
            Content = _pageStack,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        _thumbScroll = new ScrollViewer
        {
            Content = _thumbStack,
            Width = 118,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        var feature = new ContinuousPdfPreviewFeature(window, viewModel);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        viewModel.StateChanged += feature.OnStateChanged;
        feature.TryInstall();
    }

    private void OnOpened(object? sender, EventArgs e) => TryInstall();
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_installed) TryInstall();
    }

    private void TryInstall()
    {
        if (_installed || _disposed) return;
        foreach (var tabs in _window.GetVisualDescendants().OfType<TabControl>())
        {
            var items = TabItems(tabs).ToArray();
            var pdf = items.FirstOrDefault(static item => HeaderEquals(item, "PDF"));
            if (pdf is null) continue;
            _pdfTab = pdf;
            _pdfTab.Content = BuildSurface();
            _installed = true;
            _editor = typeof(StudioWorkspaceWindow)
                .GetField("_editor", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(_window) as Avalonia.Controls.TextBox;
            _centerTabs = _window.GetVisualDescendants()
                .OfType<TabControl>()
                .FirstOrDefault(control => TabItems(control).Any(static item => HeaderEquals(item, "Editor")));
            if (_editor is not null) _editor.PropertyChanged += EditorPropertyChanged;
            SyncScopeControls();
            _ = ReloadPdfIfNeededAsync(force: true);
            return;
        }
    }

    private Control BuildSurface()
    {
        var toolbar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(7, 5),
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var control in new Control[]
                 {
                     _scopeBox, _headingBox, _previous, _pageLabel, _next,
                     _zoomOut, _zoomIn, _syncBox, _status
                 })
        {
            control.Margin = new Thickness(3, 0);
            toolbar.Children.Add(control);
        }

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("118,*") };
        body.Children.Add(_thumbScroll);
        Grid.SetColumn(_mainScroll, 1);
        body.Children.Add(_mainScroll);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(toolbar);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        _previous.Click += async (_, _) => await ScrollToPageAsync(_currentPage - 1, 0);
        _next.Click += async (_, _) => await ScrollToPageAsync(_currentPage + 1, 0);
        _zoomOut.Click += async (_, _) => await ChangeZoomAsync(-0.1);
        _zoomIn.Click += async (_, _) => await ChangeZoomAsync(0.1);
        _scopeBox.SelectionChanged += ScopeSelectionChanged;
        _headingBox.SelectionChanged += HeadingSelectionChanged;
        _syncBox.Click += (_, _) =>
        {
            if (_syncBox.IsChecked == true) ScheduleSyncBuild();
            else
            {
                _syncBuildCts?.Cancel();
                _syncIndex = null;
                _scopeContext = null;
                _status.Text = "Sync off";
            }
        };
        _mainScroll.ScrollChanged += MainScrollChanged;
        _mainScroll.SizeChanged += async (_, _) =>
        {
            if (_pageCount > 0) await RefreshPageWindowAsync(force: true, preserveCurrent: true);
        };
        return root;
    }

    private void ScopeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingScope) return;
        var scope = _scopeBox.SelectedItem?.ToString() ?? "Document";
        if (string.Equals(scope, "Whole Book", StringComparison.Ordinal))
        {
            _viewModel.SetPreviewWholeBook(true);
        }
        else if (string.Equals(scope, "Heading", StringComparison.Ordinal))
        {
            _viewModel.SetPreviewWholeBook(false);
            if (_headingBox.SelectedItem is OutlineItemViewModel heading)
                _viewModel.SelectOutline(heading);
            else if (_viewModel.OutlineItems.FirstOrDefault() is { } first)
                _viewModel.SelectOutline(first);
        }
        else
        {
            _viewModel.SetPreviewWholeBook(false);
            _viewModel.SelectOutline(null);
        }
    }

    private void HeadingSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingScope || !string.Equals(_scopeBox.SelectedItem?.ToString(), "Heading", StringComparison.Ordinal)) return;
        if (_headingBox.SelectedItem is OutlineItemViewModel heading)
            _viewModel.SelectOutline(heading);
    }

    private void SyncScopeControls()
    {
        if (!_installed) return;
        _syncingScope = true;
        try
        {
            var headings = _viewModel.OutlineItems.ToArray();
            _headingBox.ItemsSource = headings;
            if (_viewModel.SelectedOutline is { } selected)
            {
                _headingBox.SelectedItem = headings.FirstOrDefault(item =>
                    item.SourceLine == selected.SourceLine && item.Level == selected.Level &&
                    string.Equals(item.Title, selected.Title, StringComparison.Ordinal));
            }
            else if (headings.Length > 0 && _headingBox.SelectedIndex < 0)
            {
                _headingBox.SelectedIndex = 0;
            }

            _scopeBox.SelectedItem = _viewModel.PreviewWholeBook
                ? "Whole Book"
                : _viewModel.SelectedOutline is not null ? "Heading" : "Document";
            _headingBox.IsEnabled = !_viewModel.PreviewWholeBook && _viewModel.HasDocument;
        }
        finally { _syncingScope = false; }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !_installed) return;
            SyncScopeControls();
            _status.Text = _viewModel.IsLivePreviewBuilding
                ? "Compiling…"
                : _viewModel.LivePreviewError is not null
                    ? "Preview error"
                    : _syncBox.IsChecked == true && _syncIndex is not null ? "Live · synced" : "Live";
            _ = ReloadPdfIfNeededAsync();
        }, DispatcherPriority.Background);
    }

    private async Task ReloadPdfIfNeededAsync(bool force = false)
    {
        var path = _viewModel.LivePreviewPdfPath;
        var version = _viewModel.LivePreviewVersion;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        if (!force && version == _version && string.Equals(path, _pdfPath, StringComparison.Ordinal)) return;

        _pdfPath = path;
        _version = version;
        _currentPage = 0;
        _windowStart = _windowEnd = -1;
        _syncIndex = null;
        _scopeContext = null;
        CancelRendering();
        DisposeBitmaps();

        _renderCts = new CancellationTokenSource();
        try
        {
            var probe = await RasterizeAsync(path, 0, 640, _renderCts.Token);
            _pageCount = probe.PageCount;
            _aspect = probe.Bitmap.Size.Height / Math.Max(1, probe.Bitmap.Size.Width);
            probe.Bitmap.Dispose();
            BuildThumbnailButtons();
            await RefreshPageWindowAsync(force: true, preserveCurrent: false);
            await RefreshThumbnailsAsync();
            if (_syncBox.IsChecked == true) ScheduleSyncBuild();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _status.Text = "Could not render: " + ex.Message;
        }
    }

    private void MainScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_buildingSurface || _pageCount <= 0) return;
        var stride = PageStride();
        if (stride <= 1) return;
        var focusY = _mainScroll.Offset.Y + Math.Max(0, _mainScroll.Bounds.Height * 0.28);
        var page = Math.Clamp((int)Math.Floor(focusY / stride), 0, _pageCount - 1);
        if (page == _currentPage) return;
        _currentPage = page;
        UpdatePageUi();
        if (page <= _windowStart + 1 || page >= _windowEnd - 1)
            _ = RefreshPageWindowAsync(force: false, preserveCurrent: true);
        _ = RefreshThumbnailsAsync();
    }

    private async Task ChangeZoomAsync(double delta)
    {
        if (_pageCount <= 0) return;
        _zoom = Math.Clamp(_zoom + delta, 0.45, 2.5);
        await RefreshPageWindowAsync(force: true, preserveCurrent: true);
        await ScrollToPageAsync(_currentPage, 0);
    }

    private async Task ScrollToPageAsync(int page, double ratio)
    {
        if (_pageCount <= 0) return;
        page = Math.Clamp(page, 0, _pageCount - 1);
        _currentPage = page;
        await RefreshPageWindowAsync(force: page < _windowStart || page > _windowEnd, preserveCurrent: false);
        var y = page * PageStride() + Math.Clamp(ratio, 0, 1) * PageHeight();
        _mainScroll.Offset = new Vector(_mainScroll.Offset.X, Math.Max(0, y));
        UpdatePageUi();
        await RefreshThumbnailsAsync();
    }

    private async Task RefreshPageWindowAsync(bool force, bool preserveCurrent)
    {
        if (_pdfPath is null || _pageCount <= 0 || _disposed) return;
        var start = Math.Max(0, _currentPage - 2);
        var end = Math.Min(_pageCount - 1, _currentPage + 2);
        if (!force && start == _windowStart && end == _windowEnd) return;

        var oldOffset = _mainScroll.Offset;
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = new CancellationTokenSource();
        var token = _renderCts.Token;
        var width = Math.Clamp((int)Math.Round(PageWidth() * 1.35), 620, 1800);

        try
        {
            var tasks = Enumerable.Range(start, end - start + 1)
                .Select(page => RasterizeAsync(_pdfPath, page, width, token))
                .ToArray();
            var rendered = await Task.WhenAll(tasks);
            token.ThrowIfCancellationRequested();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _buildingSurface = true;
                try
                {
                    foreach (var bitmap in _pageBitmaps.Values) bitmap.Dispose();
                    _pageBitmaps.Clear();
                    foreach (var page in rendered) _pageBitmaps[page.PageIndex] = page.Bitmap;
                    _windowStart = start;
                    _windowEnd = end;
                    RebuildVirtualSurface();
                    if (preserveCurrent) _mainScroll.Offset = oldOffset;
                    UpdatePageUi();
                }
                finally { _buildingSurface = false; }
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void RebuildVirtualSurface()
    {
        _pageStack.Children.Clear();
        var stride = PageStride();
        var pageWidth = PageWidth();
        var pageHeight = PageHeight();
        if (_windowStart > 0)
            _pageStack.Children.Add(new Border { Height = _windowStart * stride, Width = pageWidth });

        for (var page = _windowStart; page <= _windowEnd; page++)
        {
            if (!_pageBitmaps.TryGetValue(page, out var bitmap)) continue;
            var image = new Image
            {
                Source = bitmap,
                Width = pageWidth,
                Height = pageHeight,
                Stretch = Stretch.Uniform
            };
            var pageNumber = page;
            var border = new Border
            {
                Width = pageWidth,
                Height = pageHeight,
                Margin = new Thickness(10, 0, 10, 20),
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 100, 100, 100)),
                BorderThickness = new Thickness(1),
                Child = image
            };
            border.PointerPressed += async (_, e) =>
            {
                if (_syncBox.IsChecked != true || _syncIndex is null) return;
                var point = e.GetPosition(border);
                var ratio = point.Y / Math.Max(1, border.Bounds.Height);
                await SyncPdfToEditorAsync(pageNumber, ratio);
            };
            _pageStack.Children.Add(border);
        }

        var remaining = _pageCount - _windowEnd - 1;
        if (remaining > 0)
            _pageStack.Children.Add(new Border { Height = remaining * stride, Width = pageWidth });
    }

    private void BuildThumbnailButtons()
    {
        foreach (var bitmap in _thumbBitmaps.Values) bitmap.Dispose();
        _thumbBitmaps.Clear();
        _thumbButtons.Clear();
        _thumbStack.Children.Clear();
        for (var page = 0; page < _pageCount; page++)
        {
            var pageIndex = page;
            var button = new Button
            {
                Content = new TextBlock { Text = (page + 1).ToString(), HorizontalAlignment = HorizontalAlignment.Center },
                Width = 100,
                MinHeight = 42,
                Padding = new Thickness(4),
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            button.Click += async (_, _) => await ScrollToPageAsync(pageIndex, 0);
            _thumbButtons.Add(button);
            _thumbStack.Children.Add(button);
        }
    }

    private async Task RefreshThumbnailsAsync()
    {
        if (_pdfPath is null || _pageCount <= 0 || _thumbButtons.Count != _pageCount) return;
        _thumbCts?.Cancel();
        _thumbCts?.Dispose();
        _thumbCts = new CancellationTokenSource();
        var token = _thumbCts.Token;
        var start = Math.Max(0, _currentPage - 4);
        var end = Math.Min(_pageCount - 1, _currentPage + 4);

        try
        {
            var needed = Enumerable.Range(start, end - start + 1).Where(page => !_thumbBitmaps.ContainsKey(page)).ToArray();
            var rendered = await Task.WhenAll(needed.Select(page => RasterizeAsync(_pdfPath, page, 88, token)));
            token.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var stale in _thumbBitmaps.Keys.Where(page => page < start || page > end).ToArray())
                {
                    _thumbBitmaps[stale].Dispose();
                    _thumbBitmaps.Remove(stale);
                    SetThumbnailContent(stale, null);
                }
                foreach (var page in rendered)
                {
                    _thumbBitmaps[page.PageIndex] = page.Bitmap;
                    SetThumbnailContent(page.PageIndex, page.Bitmap);
                }
                UpdatePageUi();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void SetThumbnailContent(int page, Bitmap? bitmap)
    {
        if (page < 0 || page >= _thumbButtons.Count) return;
        _thumbButtons[page].Content = bitmap is null
            ? new TextBlock { Text = (page + 1).ToString(), HorizontalAlignment = HorizontalAlignment.Center }
            : new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new Image { Source = bitmap, Width = 86, Stretch = Stretch.Uniform },
                    new TextBlock { Text = (page + 1).ToString(), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 10 }
                }
            };
    }

    private void UpdatePageUi()
    {
        if (_pageCount <= 0)
        {
            _pageLabel.Text = "0 / 0";
            _previous.IsEnabled = _next.IsEnabled = false;
            return;
        }
        _pageLabel.Text = $"{_currentPage + 1} / {_pageCount} · {_zoom:P0}";
        _previous.IsEnabled = _currentPage > 0;
        _next.IsEnabled = _currentPage + 1 < _pageCount;
        if (_currentPage >= 0 && _currentPage < _thumbButtons.Count)
            _thumbButtons[_currentPage].BringIntoView();
    }

    private double PageWidth()
    {
        var viewport = Math.Max(380, _mainScroll.Bounds.Width - 40);
        return Math.Max(280, Math.Min(760, viewport) * _zoom);
    }

    private double PageHeight() => PageWidth() * Math.Max(0.5, _aspect);
    private double PageStride() => PageHeight() + 20;

    private void EditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_syncingEditor || _syncBox.IsChecked != true || _syncIndex is null || _scopeContext is null) return;
        if (e.Property != Avalonia.Controls.TextBox.CaretIndexProperty) return;
        _caretSyncCts?.Cancel();
        _caretSyncCts?.Dispose();
        _caretSyncCts = new CancellationTokenSource();
        _ = SyncCaretAfterDelayAsync(_caretSyncCts.Token);
    }

    private async Task SyncCaretAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(160, cancellationToken);
            var editorLine = CurrentEditorLine();
            var sourceLine = _scopeContext?.ToCompiledLine(_viewModel.SelectedRow?.Node.PersistentId, editorLine);
            if (sourceLine is null && !_viewModel.PreviewWholeBook && _viewModel.SelectedOutline is not null)
            {
                var heading = _viewModel.OutlineItems.FirstOrDefault(item => editorLine >= item.SourceLine && editorLine <= item.EndLine);
                if (heading is not null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => _viewModel.SelectOutline(heading));
                    return;
                }
            }
            if (sourceLine is null || _syncIndex is null) return;
            var point = _syncIndex.FindBySourceLine(sourceLine.Value);
            if (point is null) return;
            Dispatcher.UIThread.Post(() => _ = ScrollToPageAsync(point.PageIndex, point.PageRatio), DispatcherPriority.Background);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task SyncPdfToEditorAsync(int pageIndex, double ratio)
    {
        if (_syncIndex is null || _scopeContext is null) return;
        var sourceLine = _syncIndex.FindSourceLine(pageIndex, ratio);
        if (sourceLine is null) return;
        var target = _scopeContext.ToEditorLine(sourceLine.Value);
        if (target is null) return;

        var row = _viewModel.BinderRows.FirstOrDefault(candidate =>
            string.Equals(candidate.Node.PersistentId, target.Value.PersistentId, StringComparison.Ordinal));
        if (row is null) return;
        if (!ReferenceEquals(row.Node, _viewModel.SelectedRow?.Node))
            await _viewModel.SelectAsync(row);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _syncingEditor = true;
            try
            {
                if (_centerTabs is not null) _centerTabs.SelectedIndex = 0;
                NavigateEditorToLine(target.Value.EditorLine);
            }
            finally
            {
                Dispatcher.UIThread.Post(() => _syncingEditor = false, DispatcherPriority.Background);
            }
        });
    }

    private int CurrentEditorLine()
    {
        if (_editor is null) return 1;
        var text = _editor.Text ?? string.Empty;
        var caret = Math.Clamp(_editor.CaretIndex, 0, text.Length);
        var line = 1;
        for (var index = 0; index < caret; index++) if (text[index] == '\n') line++;
        return line;
    }

    private void NavigateEditorToLine(int line)
    {
        if (_editor is null) return;
        var text = _editor.Text ?? string.Empty;
        var targetLine = Math.Max(1, line);
        var index = 0;
        for (var current = 1; current < targetLine && index < text.Length; current++)
        {
            var next = text.IndexOf('\n', index);
            if (next < 0) { index = text.Length; break; }
            index = next + 1;
        }
        _editor.CaretIndex = index;
        _editor.SelectionStart = index;
        _editor.SelectionEnd = index;
        _editor.Focus();
    }

    private void ScheduleSyncBuild()
    {
        if (_pdfPath is null || _version < 0 || _syncBox.IsChecked != true) return;
        _syncBuildCts?.Cancel();
        _syncBuildCts?.Dispose();
        _syncBuildCts = new CancellationTokenSource();
        var version = _version;
        _ = BuildSyncMapAsync(version, _syncBuildCts.Token);
    }

    private async Task BuildSyncMapAsync(long version, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(180, cancellationToken);
            var context = await BuildSourceContextAsync(cancellationToken);
            if (context is null) return;
            var project = CurrentProject();
            if (project is null) return;
            var executable = ResolveLuaLatex();
            if (executable is null) return;

            await Dispatcher.UIThread.InvokeAsync(() => _status.Text = "Building SyncTeX…");
            var latex = _sourceRenderer.RenderLatex(_parser.Parse(context.Source), context.Title, project.Style);
            var buildDirectory = Path.Combine(project.RootPath, "build");
            Directory.CreateDirectory(buildDirectory);
            var texPath = Path.Combine(buildDirectory, "live-preview-sync.tex");
            var syncPath = Path.Combine(buildDirectory, "live-preview-sync.synctex.gz");
            await CompileSyncTexAsync(executable, latex, texPath, syncPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (version != _version) return;

            var index = await Task.Run(() => SyncTexIndex.Load(texPath, syncPath), cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _version || _disposed) return;
                _scopeContext = context;
                _syncIndex = index;
                _status.Text = index.PointCount > 0 ? "Live · synced" : "Live · no sync points";
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => _status.Text = "Sync unavailable: " + ex.Message);
        }
    }

    private async Task<SourceContext?> BuildSourceContextAsync(CancellationToken cancellationToken)
    {
        var project = CurrentProject();
        var selected = _viewModel.SelectedRow?.Node;
        if (project is null) return null;

        if (_viewModel.PreviewWholeBook || selected?.IsDocument != true)
        {
            var builder = new StringBuilder();
            var spans = new List<SourceSpan>();
            var nextStart = 1;
            var first = true;
            await foreach (var (node, content) in _repository.EnumerateDocumentsAsync(project, cancellationToken))
            {
                var effective = ReferenceEquals(node, selected) ? _viewModel.EditorText : content;
                if (!first) builder.AppendLine().AppendLine();
                var trimmed = effective.TrimEnd();
                var count = CountLines(trimmed);
                var start = first ? 1 : nextStart;
                spans.Add(new SourceSpan(node.PersistentId, start, start + count - 1, 1));
                builder.Append(trimmed).AppendLine();
                nextStart = start + count + 2;
                first = false;
            }
            return new SourceContext(builder.ToString(), project.Title, spans);
        }

        if (_viewModel.SelectedOutline is { } heading)
        {
            var source = SliceLines(_viewModel.EditorText, heading.SourceLine, heading.EndLine);
            var count = CountLines(source);
            return new SourceContext(
                source,
                selected.Title,
                [new SourceSpan(selected.PersistentId, 1, count, heading.SourceLine)]);
        }

        return new SourceContext(
            _viewModel.EditorText,
            selected.Title,
            [new SourceSpan(selected.PersistentId, 1, CountLines(_viewModel.EditorText), 1)]);
    }

    private BookProject? CurrentProject()
        => typeof(WorkspaceViewModel)
            .GetField("_project", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_viewModel) as BookProject;

    private static async Task CompileSyncTexAsync(
        string executable,
        string latex,
        string destinationTex,
        string destinationSync,
        CancellationToken cancellationToken)
    {
        var work = Path.Combine(Path.GetTempPath(), "typescribe", "synctex", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var source = Path.Combine(work, "document.tex");
        await File.WriteAllTextAsync(source, latex, new UTF8Encoding(false), cancellationToken);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = work,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-interaction=nonstopmode");
            start.ArgumentList.Add("-halt-on-error");
            start.ArgumentList.Add("-file-line-error");
            start.ArgumentList.Add("-no-shell-escape");
            start.ArgumentList.Add("-synctex=1");
            start.ArgumentList.Add($"-output-directory={work}");
            start.ArgumentList.Add(source);

            var engineDir = Path.GetDirectoryName(executable);
            if (!string.IsNullOrWhiteSpace(engineDir))
            {
                var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                start.Environment["PATH"] = engineDir + Path.PathSeparator + path;
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start LuaLaTeX.");
            using var registration = cancellationToken.Register(static state =>
            {
                try
                {
                    var process = (Process)state!;
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch { }
            }, process);
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
                throw new InvalidOperationException(Tail(error + Environment.NewLine + output, 900));

            var producedSync = Path.Combine(work, "document.synctex.gz");
            if (!File.Exists(producedSync)) throw new InvalidOperationException("LuaLaTeX did not emit SyncTeX data.");
            File.Copy(source, destinationTex, overwrite: true);
            File.Copy(producedSync, destinationSync, overwrite: true);
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
        }
    }

    private static string? ResolveLuaLatex()
    {
        var configured = Environment.GetEnvironmentVariable("TYPESCRIBE_LUALATEX");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) local = Path.GetTempPath();
        var platform = OperatingSystem.IsWindows()
            ? "windows-x64"
            : OperatingSystem.IsMacOS()
                ? $"macos-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}"
                : $"linux-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
        var root = Path.Combine(local, "Typescribe", "engines", $"tinytex-full-{TinyTexVersion}", platform);
        var marker = Path.Combine(root, ".typescribe-lualatex-path");
        if (File.Exists(marker))
        {
            var relative = File.ReadAllText(marker).Trim();
            if (relative.Length > 0)
            {
                var candidate = Path.GetFullPath(Path.Combine(root, relative));
                if (File.Exists(candidate)) return candidate;
            }
        }

        var executable = OperatingSystem.IsWindows() ? "lualatex.exe" : "lualatex";
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue)) return null;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static Task<RasterPage> RasterizeAsync(string pdfPath, int page, int width, CancellationToken cancellationToken)
        => Task.Run(() => Rasterize(pdfPath, page, width, cancellationToken), cancellationToken);

    private static RasterPage Rasterize(string pdfPath, int requestedPage, int width, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.SequentialScan);
        var pageCount = Conversion.GetPageCount(stream, leaveOpen: true);
        if (pageCount <= 0) throw new InvalidOperationException("PDF contains no pages.");
        var page = Math.Clamp(requestedPage, 0, pageCount - 1);
        stream.Position = 0;
        using var rendered = Conversion.ToImage(stream, page, leaveOpen: true, options: new RenderOptions(Width: width, WithAspectRatio: true));
        using var image = SKImage.FromBitmap(rendered);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 92) ?? throw new InvalidOperationException("Could not encode PDF page.");
        using var memory = new MemoryStream(encoded.ToArray(), writable: false);
        return new RasterPage(new Bitmap(memory), page, pageCount);
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0) return 1;
        var count = 1;
        foreach (var character in text) if (character == '\n') count++;
        return count;
    }

    private static string SliceLines(string text, int startLine, int endLine)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var start = Math.Clamp(startLine - 1, 0, Math.Max(0, lines.Length - 1));
        var endExclusive = Math.Clamp(endLine, start + 1, lines.Length);
        return string.Join(Environment.NewLine, lines[start..endExclusive]);
    }

    private static string Tail(string text, int max)
        => text.Length <= max ? text : text[^max..];

    private static bool HeaderEquals(TabItem item, string header)
        => string.Equals(item.Header?.ToString(), header, StringComparison.Ordinal);

    private static IEnumerable<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is System.Collections.IEnumerable source)
            return source.Cast<object?>().OfType<TabItem>();
        return tabs.Items.OfType<TabItem>();
    }

    private void CancelRendering()
    {
        _renderCts?.Cancel();
        _thumbCts?.Cancel();
        _syncBuildCts?.Cancel();
        _caretSyncCts?.Cancel();
    }

    private void DisposeBitmaps()
    {
        foreach (var bitmap in _pageBitmaps.Values) bitmap.Dispose();
        foreach (var bitmap in _thumbBitmaps.Values) bitmap.Dispose();
        _pageBitmaps.Clear();
        _thumbBitmaps.Clear();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        CancelRendering();
        _renderCts?.Dispose();
        _thumbCts?.Dispose();
        _syncBuildCts?.Dispose();
        _caretSyncCts?.Dispose();
        DisposeBitmaps();
        if (_editor is not null) _editor.PropertyChanged -= EditorPropertyChanged;
        _viewModel.StateChanged -= OnStateChanged;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
    }

    private sealed record RasterPage(Bitmap Bitmap, int PageIndex, int PageCount);

    private sealed record SourceSpan(string PersistentId, int CompiledStartLine, int CompiledEndLine, int EditorStartLine);

    private sealed record SourceContext(string Source, string Title, IReadOnlyList<SourceSpan> Spans)
    {
        public int? ToCompiledLine(string? persistentId, int editorLine)
        {
            if (persistentId is null) return null;
            var span = Spans.FirstOrDefault(item => string.Equals(item.PersistentId, persistentId, StringComparison.Ordinal));
            if (span is null) return null;
            var local = Math.Max(span.EditorStartLine, editorLine);
            var compiled = span.CompiledStartLine + local - span.EditorStartLine;
            return Math.Clamp(compiled, span.CompiledStartLine, span.CompiledEndLine);
        }

        public (string PersistentId, int EditorLine)? ToEditorLine(int compiledLine)
        {
            var span = Spans.FirstOrDefault(item => compiledLine >= item.CompiledStartLine && compiledLine <= item.CompiledEndLine);
            if (span is null) return null;
            return (span.PersistentId, span.EditorStartLine + compiledLine - span.CompiledStartLine);
        }
    }

    private sealed class SyncTexIndex
    {
        private const string Marker = "% TYPESCRIBE-SOURCE:";
        private readonly SyncPoint[] _points;
        private readonly Dictionary<int, (long MinY, long MaxY)> _pageBounds;

        private SyncTexIndex(SyncPoint[] points)
        {
            _points = points;
            _pageBounds = points
                .GroupBy(static point => point.PageIndex)
                .ToDictionary(
                    static group => group.Key,
                    static group => (group.Min(static point => point.Y), group.Max(static point => point.Y)));
        }

        public int PointCount => _points.Length;

        public static SyncTexIndex Load(string texPath, string syncPath)
        {
            var markers = new List<(int TexLine, int SourceLine)>();
            var texLines = File.ReadAllLines(texPath);
            for (var index = 0; index < texLines.Length; index++)
            {
                var line = texLines[index].Trim();
                if (!line.StartsWith(Marker, StringComparison.Ordinal)) continue;
                if (int.TryParse(line[Marker.Length..], out var sourceLine))
                    markers.Add((index + 1, Math.Max(1, sourceLine)));
            }
            if (markers.Count == 0) return new SyncTexIndex([]);

            string[] syncLines;
            using (var file = File.OpenRead(syncPath))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                syncLines = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

            var documentTag = 1;
            foreach (var line in syncLines)
            {
                if (!line.StartsWith("Input:", StringComparison.Ordinal)) continue;
                var first = line.IndexOf(':');
                var second = line.IndexOf(':', first + 1);
                if (second <= first) continue;
                if (!int.TryParse(line[(first + 1)..second], out var tag)) continue;
                var path = line[(second + 1)..];
                if (path.EndsWith("document.tex", StringComparison.OrdinalIgnoreCase))
                {
                    documentTag = tag;
                    break;
                }
            }

            var points = new List<SyncPoint>();
            var page = -1;
            foreach (var raw in syncLines)
            {
                var line = raw.Trim();
                if (line.StartsWith('{') && int.TryParse(line[1..], out var pageNumber))
                {
                    page = Math.Max(0, pageNumber - 1);
                    continue;
                }
                if (page < 0 || !TryParseNode(line, out var tag, out var texLine, out var y) || tag != documentTag) continue;
                var sourceLine = SourceForTexLine(markers, texLine);
                if (sourceLine is null) continue;
                points.Add(new SyncPoint(sourceLine.Value, texLine, page, y, 0));
            }

            var array = points.ToArray();
            var bounds = array.GroupBy(static point => point.PageIndex)
                .ToDictionary(static group => group.Key, static group => (Min: group.Min(static point => point.Y), Max: group.Max(static point => point.Y)));
            for (var index = 0; index < array.Length; index++)
            {
                var point = array[index];
                if (!bounds.TryGetValue(point.PageIndex, out var bound) || bound.Max <= bound.Min)
                    array[index] = point with { PageRatio = 0 };
                else
                    array[index] = point with { PageRatio = Math.Clamp((point.Y - bound.Min) / (double)(bound.Max - bound.Min), 0, 1) };
            }
            return new SyncTexIndex(array);
        }

        public SyncPoint? FindBySourceLine(int sourceLine)
        {
            if (_points.Length == 0) return null;
            var before = _points.Where(point => point.SourceLine <= sourceLine)
                .OrderByDescending(static point => point.SourceLine)
                .ThenBy(static point => point.TexLine)
                .FirstOrDefault();
            if (before is not null) return before;
            return _points.OrderBy(static point => point.SourceLine).First();
        }

        public int? FindSourceLine(int pageIndex, double pageRatio)
        {
            var page = _points.Where(point => point.PageIndex == pageIndex).ToArray();
            if (page.Length == 0) return null;
            if (!_pageBounds.TryGetValue(pageIndex, out var bounds) || bounds.MaxY <= bounds.MinY)
                return page[0].SourceLine;
            var target = bounds.MinY + Math.Clamp(pageRatio, 0, 1) * (bounds.MaxY - bounds.MinY);
            return page.OrderBy(point => Math.Abs(point.Y - target)).First().SourceLine;
        }

        private static int? SourceForTexLine(IReadOnlyList<(int TexLine, int SourceLine)> markers, int texLine)
        {
            var low = 0;
            var high = markers.Count - 1;
            var result = -1;
            while (low <= high)
            {
                var mid = low + ((high - low) / 2);
                if (markers[mid].TexLine <= texLine)
                {
                    result = mid;
                    low = mid + 1;
                }
                else high = mid - 1;
            }
            return result >= 0 ? markers[result].SourceLine : null;
        }

        private static bool TryParseNode(string line, out int tag, out int texLine, out long y)
        {
            tag = texLine = 0;
            y = 0;
            if (line.Length < 5) return false;
            var comma = line.IndexOf(',');
            var colon = comma < 0 ? -1 : line.IndexOf(':', comma + 1);
            if (comma <= 0 || colon <= comma) return false;

            var tagStart = 0;
            while (tagStart < comma && !char.IsDigit(line[tagStart]) && line[tagStart] != '-') tagStart++;
            if (tagStart >= comma || !int.TryParse(line[tagStart..comma], out tag)) return false;
            if (!int.TryParse(line[(comma + 1)..colon], out texLine)) return false;
            var rest = line[(colon + 1)..];
            var firstComma = rest.IndexOf(',');
            var secondComma = firstComma < 0 ? -1 : rest.IndexOf(',', firstComma + 1);
            if (firstComma < 0 || secondComma < 0) return false;
            return long.TryParse(rest[(firstComma + 1)..secondComma], out y);
        }
    }

    private sealed record SyncPoint(int SourceLine, int TexLine, int PageIndex, long Y, double PageRatio);
}
