using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Application.Abstractions;
using Typescribe.Application.Services;
using Typescribe.Desktop.Editing;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

/// <summary>
/// Adds line-and-column aware SyncTeX navigation on top of the continuous PDF surface.
/// The legacy ratio-only sync switch is hidden so only one synchronization owner is active.
/// </summary>
internal sealed class ExactPdfCursorSyncFeature
{
    private const string TinyTexVersion = "2026.09";
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly TrackingProjectRepository _repository;
    private readonly IDocumentParser _parser;
    private readonly ExactSourceMappedLatexRenderer _renderer = new();
    private readonly CheckBox _syncToggle = new()
    {
        Content = "Exact Sync",
        IsChecked = true,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _status = new()
    {
        Text = "Exact SyncTeX",
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0.7
    };
    private readonly HashSet<Border> _wiredPages = [];

    private CancellationTokenSource? _buildCts;
    private CancellationTokenSource? _caretCts;
    private ManuscriptEditor? _editor;
    private ScrollViewer? _mainScroll;
    private StackPanel? _pageStack;
    private long _builtVersion = -1;
    private long _requestedVersion = -1;
    private bool _installed;
    private bool _disposed;
    private bool _navigatingEditor;
    private ExactSyncIndex? _index;
    private SourceContext? _context;

    private ExactPdfCursorSyncFeature(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository,
        IDocumentParser parser)
    {
        _window = window;
        _viewModel = viewModel;
        _repository = repository;
        _parser = parser;
    }

    public static void Apply(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository,
        IDocumentParser parser)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(parser);

        var feature = new ExactPdfCursorSyncFeature(window, viewModel, repository, parser);
        window.Opened += feature.WindowOpened;
        window.LayoutUpdated += feature.WindowLayoutUpdated;
        window.Closed += feature.WindowClosed;
        viewModel.StateChanged += feature.ViewModelStateChanged;
        feature.TryInstall();
    }

    private void WindowOpened(object? sender, EventArgs e)
    {
        TryInstall();
        DiscoverEditor();
        WireRenderedPages();
        ScheduleBuild();
    }

    private void WindowLayoutUpdated(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!_installed) TryInstall();
        DiscoverEditor();
        WireRenderedPages();
    }

    private void ViewModelStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            TryInstall();
            DiscoverEditor();
            WireRenderedPages();
            if (_syncToggle.IsChecked == true && !_viewModel.IsLivePreviewBuilding)
                ScheduleBuild();
        }, DispatcherPriority.Background);
    }

    private void TryInstall()
    {
        if (_installed || _disposed) return;
        var pdfTab = FindPdfTab();
        if (pdfTab?.Content is not Control content) return;

        var controls = EnumerateControls(content).ToArray();
        var toolbar = controls.OfType<WrapPanel>().FirstOrDefault(panel =>
            panel.Children.OfType<CheckBox>().Any(box =>
                string.Equals(box.Content?.ToString(), "Sync", StringComparison.Ordinal)));
        if (toolbar is null) return;

        var legacy = toolbar.Children.OfType<CheckBox>().FirstOrDefault(box =>
            string.Equals(box.Content?.ToString(), "Sync", StringComparison.Ordinal));
        if (legacy is not null)
        {
            legacy.IsChecked = false;
            legacy.IsVisible = false;
        }

        _syncToggle.Margin = new Thickness(3, 0);
        _status.Margin = new Thickness(3, 0);
        _syncToggle.Click += ExactSyncClicked;
        toolbar.Children.Add(_syncToggle);
        toolbar.Children.Add(_status);

        _mainScroll = controls.OfType<ScrollViewer>().FirstOrDefault(scroll =>
            scroll.Content is StackPanel
            {
                Orientation: Orientation.Vertical,
                HorizontalAlignment: HorizontalAlignment.Center
            });
        _pageStack = _mainScroll?.Content as StackPanel;
        _installed = true;
    }

    private TabItem? FindPdfTab()
    {
        foreach (var tabs in _window.GetVisualDescendants().OfType<TabControl>())
        {
            var pdf = TabItems(tabs).FirstOrDefault(static item =>
                string.Equals(item.Header?.ToString(), "PDF", StringComparison.Ordinal));
            if (pdf is not null) return pdf;
        }

        if (_window.Content is not Control root) return null;
        foreach (var tabs in EnumerateControls(root).OfType<TabControl>())
        {
            var pdf = TabItems(tabs).FirstOrDefault(static item =>
                string.Equals(item.Header?.ToString(), "PDF", StringComparison.Ordinal));
            if (pdf is not null) return pdf;
        }
        return null;
    }

    private void ExactSyncClicked(object? sender, EventArgs e)
    {
        if (_syncToggle.IsChecked == true)
        {
            _status.Text = "Building exact map…";
            ScheduleBuild(force: true);
            ScheduleCaretSync();
        }
        else
        {
            _buildCts?.Cancel();
            _caretCts?.Cancel();
            _index = null;
            _context = null;
            _builtVersion = -1;
            _status.Text = "Exact Sync off";
        }
    }

    private void DiscoverEditor()
    {
        if (_editor is not null || _disposed) return;
        _editor = _window.GetVisualDescendants().OfType<ManuscriptEditor>().FirstOrDefault();
        if (_editor is null) return;
        _editor.TextArea.Caret.PositionChanged += EditorCaretChanged;
        ScheduleCaretSync();
    }

    private void EditorCaretChanged(object? sender, EventArgs e)
    {
        if (_navigatingEditor || _syncToggle.IsChecked != true) return;
        ScheduleCaretSync();
    }

    private void ScheduleCaretSync()
    {
        if (_editor is null || _index is null || _context is null || _syncToggle.IsChecked != true) return;
        _caretCts?.Cancel();
        _caretCts?.Dispose();
        _caretCts = new CancellationTokenSource();
        _ = SyncCaretAfterDelayAsync(_caretCts.Token);
    }

    private async Task SyncCaretAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(70, cancellationToken);
            if (_editor is null || _index is null || _context is null) return;
            var line = Math.Max(1, _editor.TextArea.Caret.Line);
            var column = Math.Max(1, _editor.TextArea.Caret.Column);
            var source = _context.ToCompiledPosition(
                _viewModel.SelectedRow?.Node.PersistentId,
                line,
                column);
            if (source is null) return;
            var point = _index.FindBySourcePosition(source.Value.Line, source.Value.Column, source.Value.LineLength);
            if (point is null) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ScrollPdfTo(point.PageIndex, point.PageYRatio);
                _status.Text = $"Exact SyncTeX · p {point.PageIndex + 1}";
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void WireRenderedPages()
    {
        if (_pageStack is null || _disposed) return;
        foreach (var border in RenderedPageBorders())
        {
            if (!_wiredPages.Add(border)) continue;
            border.PointerPressed += PagePointerPressed;
        }
    }

    private async void PagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_syncToggle.IsChecked != true || _index is null || _context is null || sender is not Border page) return;
        if (!e.GetCurrentPoint(page).Properties.IsLeftButtonPressed) return;
        var pageIndex = GetPageIndex(page);
        if (pageIndex < 0) return;

        var point = e.GetPosition(page);
        var xRatio = point.X / Math.Max(1, page.Bounds.Width);
        var yRatio = point.Y / Math.Max(1, page.Bounds.Height);
        var hit = _index.FindSourcePosition(pageIndex, xRatio, yRatio);
        if (hit is null) return;
        var target = _context.ToEditorPosition(hit.Value.SourceLine, hit.Value.StartColumn, hit.Value.PositionRatio);
        if (target is null) return;

        try
        {
            var row = _viewModel.BinderRows.FirstOrDefault(candidate =>
                string.Equals(candidate.Node.PersistentId, target.Value.PersistentId, StringComparison.Ordinal));
            if (row is null) return;
            if (!string.Equals(
                    _viewModel.SelectedRow?.Node.PersistentId,
                    target.Value.PersistentId,
                    StringComparison.Ordinal))
            {
                await _viewModel.SelectAsync(row);
                await Task.Delay(35);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                DiscoverEditor();
                if (_editor is null) return;
                SelectEditorTab();
                _navigatingEditor = true;
                try
                {
                    NavigateEditor(_editor, target.Value.EditorLine, target.Value.EditorColumn);
                    _status.Text = $"Exact SyncTeX · line {target.Value.EditorLine}, col {target.Value.EditorColumn}";
                }
                finally
                {
                    Dispatcher.UIThread.Post(() => _navigatingEditor = false, DispatcherPriority.Background);
                }
            }, DispatcherPriority.Background);
            e.Handled = true;
        }
        catch
        {
            _status.Text = "Exact Sync navigation failed";
        }
    }

    private void ScrollPdfTo(int pageIndex, double pageRatio)
    {
        if (_mainScroll is null || _pageStack is null) return;
        var height = CurrentPageHeight();
        if (height <= 0) return;
        var stride = height + 20;
        var y = Math.Max(0, pageIndex * stride + Math.Clamp(pageRatio, 0, 1) * height);
        _mainScroll.Offset = new Vector(_mainScroll.Offset.X, y);
    }

    private double CurrentPageHeight()
        => RenderedPageBorders().Select(static page => page.Height)
            .FirstOrDefault(static height => !double.IsNaN(height) && height > 1);

    private int GetPageIndex(Border page)
    {
        if (_pageStack is null) return -1;
        var pages = RenderedPageBorders().ToArray();
        var local = Array.IndexOf(pages, page);
        if (local < 0) return -1;
        var height = page.Height;
        if (double.IsNaN(height) || height <= 1) height = page.Bounds.Height;
        var stride = Math.Max(1, height + 20);
        var first = 0;
        if (_pageStack.Children.FirstOrDefault() is Border { Child: null } spacer && spacer.Height > 1)
            first = Math.Max(0, (int)Math.Round(spacer.Height / stride));
        return first + local;
    }

    private IEnumerable<Border> RenderedPageBorders()
    {
        if (_pageStack is null) yield break;
        foreach (var border in _pageStack.Children.OfType<Border>())
            if (border.Child is Image)
                yield return border;
    }

    private void SelectEditorTab()
    {
        foreach (var tabs in _window.GetVisualDescendants().OfType<TabControl>())
        {
            var editor = TabItems(tabs).FirstOrDefault(static item =>
                string.Equals(item.Header?.ToString(), "Editor", StringComparison.Ordinal));
            if (editor is null) continue;
            tabs.SelectedItem = editor;
            return;
        }
    }

    private static void NavigateEditor(ManuscriptEditor editor, int line, int column)
    {
        if (editor.Document.LineCount == 0) return;
        var lineNumber = Math.Clamp(line, 1, editor.Document.LineCount);
        var documentLine = editor.Document.GetLineByNumber(lineNumber);
        var columnOffset = Math.Clamp(column - 1, 0, documentLine.Length);
        var offset = documentLine.Offset + columnOffset;
        editor.CaretOffset = offset;
        editor.Select(offset, 0);
        editor.ScrollTo(lineNumber, columnOffset + 1);
        editor.Focus();
    }

    private void ScheduleBuild(bool force = false)
    {
        if (!_installed || _syncToggle.IsChecked != true || _disposed) return;
        if (_viewModel.IsLivePreviewBuilding) return;
        var version = _viewModel.LivePreviewVersion;
        if (version < 0 || string.IsNullOrWhiteSpace(_viewModel.LivePreviewPdfPath)) return;
        if (!force && version == _builtVersion) return;
        if (!force && version == _requestedVersion && _buildCts is not null) return;

        _requestedVersion = version;
        _buildCts?.Cancel();
        _buildCts?.Dispose();
        _buildCts = new CancellationTokenSource();
        _ = BuildExactMapAsync(version, _buildCts.Token);
    }

    private async Task BuildExactMapAsync(long version, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(180, cancellationToken);
            var project = _repository.CurrentProject;
            if (project is null) return;
            var executable = ResolveLuaLatex();
            if (executable is null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => _status.Text = "Exact Sync: LuaLaTeX unavailable");
                return;
            }

            var context = await BuildSourceContextAsync(cancellationToken);
            if (context is null) return;
            await Dispatcher.UIThread.InvokeAsync(() => _status.Text = "Building exact SyncTeX…");

            var document = _parser.Parse(context.Source);
            var latex = _renderer.Render(document, context.Source, context.Title, project.Style);
            var index = await CompileAndLoadIndexAsync(executable, latex, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (version != _viewModel.LivePreviewVersion || _disposed) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _context = context;
                _index = index;
                _builtVersion = version;
                _requestedVersion = -1;
                _status.Text = index.PointCount > 0 ? "Exact SyncTeX" : "Exact Sync: no points";
                WireRenderedPages();
                ScheduleCaretSync();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => _status.Text = "Exact Sync unavailable: " + ShortMessage(ex.Message));
        }
    }

    private async Task<SourceContext?> BuildSourceContextAsync(CancellationToken cancellationToken)
    {
        var project = _repository.CurrentProject;
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

    private static async Task<ExactSyncIndex> CompileAndLoadIndexAsync(
        string executable,
        string latex,
        CancellationToken cancellationToken)
    {
        var work = Path.Combine(Path.GetTempPath(), "typescribe", "exact-synctex", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var source = Path.Combine(work, "document.tex");
        var sync = Path.Combine(work, "document.synctex.gz");
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
                catch
                {
                }
            }, process);
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
                throw new InvalidOperationException(ShortMessage(error + Environment.NewLine + output));
            if (!File.Exists(sync))
                throw new InvalidOperationException("LuaLaTeX did not emit SyncTeX data.");
            return ExactSyncIndex.Load(source, sync);
        }
        finally
        {
            try
            {
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static string? ResolveLuaLatex()
    {
        var configured = Environment.GetEnvironmentVariable("TYPESCRIBE_LUALATEX");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);

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

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _buildCts?.Cancel();
        _caretCts?.Cancel();
        _buildCts?.Dispose();
        _caretCts?.Dispose();
        if (_editor is not null) _editor.TextArea.Caret.PositionChanged -= EditorCaretChanged;
        foreach (var page in _wiredPages) page.PointerPressed -= PagePointerPressed;
        _wiredPages.Clear();
        _syncToggle.Click -= ExactSyncClicked;
        _viewModel.StateChanged -= ViewModelStateChanged;
        _window.Opened -= WindowOpened;
        _window.LayoutUpdated -= WindowLayoutUpdated;
        _window.Closed -= WindowClosed;
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
        if (root is Decorator { Child: Control decorated })
        {
            foreach (var descendant in EnumerateControls(decorated))
                yield return descendant;
        }
        if (root is ContentControl { Content: Control content })
        {
            foreach (var descendant in EnumerateControls(content))
                yield return descendant;
        }
        if (root is TabControl tabs && tabs.ItemsSource is System.Collections.IEnumerable source)
        {
            foreach (var tab in source.Cast<object?>().OfType<TabItem>())
                if (tab.Content is Control tabContent)
                    foreach (var descendant in EnumerateControls(tabContent))
                        yield return descendant;
        }
    }

    private static IEnumerable<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is System.Collections.IEnumerable source)
            return source.Cast<object?>().OfType<TabItem>();
        return tabs.Items.OfType<TabItem>();
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0) return 1;
        var count = 1;
        foreach (var ch in text) if (ch == '\n') count++;
        return count;
    }

    private static string SliceLines(string text, int startLine, int endLine)
    {
        var normalized = NormalizeNewlines(text);
        var lines = normalized.Split('\n');
        if (lines.Length == 0) return string.Empty;
        var start = Math.Clamp(startLine - 1, 0, lines.Length - 1);
        var endExclusive = Math.Clamp(endLine, start + 1, lines.Length);
        return string.Join(Environment.NewLine, lines[start..endExclusive]);
    }

    private static string NormalizeNewlines(string text)
        => (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string ShortMessage(string text)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length <= 700) return text;
        return text[^700..];
    }

    private sealed record SourceSpan(string PersistentId, int CompiledStartLine, int CompiledEndLine, int EditorStartLine);
    private readonly record struct CompiledPosition(int Line, int Column, int LineLength);
    private readonly record struct EditorPosition(string PersistentId, int EditorLine, int EditorColumn);

    private sealed class SourceContext
    {
        private readonly string[] _lines;

        public SourceContext(string source, string title, IReadOnlyList<SourceSpan> spans)
        {
            Source = source;
            Title = title;
            Spans = spans;
            _lines = NormalizeNewlines(source).Split('\n');
        }

        public string Source { get; }
        public string Title { get; }
        public IReadOnlyList<SourceSpan> Spans { get; }

        public CompiledPosition? ToCompiledPosition(string? persistentId, int editorLine, int editorColumn)
        {
            if (persistentId is null) return null;
            var span = Spans.FirstOrDefault(item => string.Equals(item.PersistentId, persistentId, StringComparison.Ordinal));
            if (span is null) return null;
            var localLine = Math.Max(span.EditorStartLine, editorLine);
            var compiledLine = span.CompiledStartLine + localLine - span.EditorStartLine;
            compiledLine = Math.Clamp(compiledLine, span.CompiledStartLine, span.CompiledEndLine);
            var length = LineLength(compiledLine);
            return new CompiledPosition(compiledLine, Math.Clamp(editorColumn, 1, length + 1), length);
        }

        public EditorPosition? ToEditorPosition(int compiledLine, int startColumn, double ratio)
        {
            var span = Spans.FirstOrDefault(item => compiledLine >= item.CompiledStartLine && compiledLine <= item.CompiledEndLine);
            if (span is null) return null;
            var editorLine = span.EditorStartLine + compiledLine - span.CompiledStartLine;
            var length = LineLength(compiledLine);
            var start = Math.Clamp(startColumn, 1, length + 1);
            var available = Math.Max(0, length + 1 - start);
            var column = start + (int)Math.Round(Math.Clamp(ratio, 0, 1) * available);
            return new EditorPosition(span.PersistentId, editorLine, Math.Clamp(column, 1, length + 1));
        }

        private int LineLength(int line)
            => line >= 1 && line <= _lines.Length ? _lines[line - 1].Length : 0;
    }

    private sealed class ExactSourceMappedLatexRenderer
    {
        private const string MarkerPrefix = "% TYPESCRIBE-SOURCE:";
        private readonly LuaLatexSafeDocumentRenderer _inner = new();

        public string Render(DocumentAst document, string source, string title, BookStyle style)
        {
            var latex = _inner.RenderLatex(document, title, style);
            if (document.Blocks.Count == 0) return latex;
            var sourceLines = NormalizeNewlines(source).Split('\n');
            var cursor = 0;

            for (var blockIndex = 0; blockIndex < document.Blocks.Count; blockIndex++)
            {
                var block = document.Blocks[blockIndex];
                var needle = RenderBlockNeedle(block, style);
                if (string.IsNullOrWhiteSpace(needle)) continue;
                var index = latex.IndexOf(needle, cursor, StringComparison.Ordinal);
                if (index < 0) continue;

                var nextLine = blockIndex + 1 < document.Blocks.Count
                    ? document.Blocks[blockIndex + 1].SourceLine - 1
                    : sourceLines.Length;
                var decorated = InstrumentNeedle(
                    needle,
                    block,
                    sourceLines,
                    Math.Max(1, block.SourceLine),
                    Math.Max(block.SourceLine, nextLine));
                latex = latex.Remove(index, needle.Length).Insert(index, decorated);
                cursor = index + decorated.Length;
            }
            return latex;
        }

        private string RenderBlockNeedle(AstBlock block, BookStyle style)
        {
            var single = _inner.RenderLatex(
                new DocumentAst([block]),
                "Typescribe exact source map",
                style with { IncludeTableOfContents = false });
            var body = ExtractBody(single);
            if (body.Length == 0) return string.Empty;
            if (block is ListItemBlock)
            {
                var item = body.IndexOf("\\item ", StringComparison.Ordinal);
                if (item < 0) return string.Empty;
                var end = body.IndexOf('\n', item);
                return (end < 0 ? body[item..] : body[item..end]).TrimEnd('\r');
            }
            return body.Trim();
        }

        private static string InstrumentNeedle(
            string needle,
            AstBlock block,
            IReadOnlyList<string> sourceLines,
            int sourceStart,
            int sourceEnd)
        {
            var contentStart = ContentStart(needle, block);
            if (block is CodeBlock || block is TableBlock || block is FigureBlock)
                return InsertMarkers(needle, [(contentStart, sourceStart, 1)]);

            var plain = PlainText(block);
            if (string.IsNullOrWhiteSpace(plain))
                return InsertMarkers(needle, [(contentStart, sourceStart, 1)]);

            var raw = BuildRawCharacters(sourceLines, sourceStart, sourceEnd);
            var plainToRaw = AlignPlainToRaw(plain, raw);
            var anchors = new List<(int Index, int Line, int Column)>();
            var seenLines = new HashSet<int>();
            var search = contentStart;

            for (var plainIndex = 0; plainIndex < plain.Length; plainIndex++)
            {
                var ch = plain[plainIndex];
                if (char.IsWhiteSpace(ch)) continue;
                var token = RenderedToken(block, ch);
                var rendered = FindRenderedToken(needle, token, search, ch);
                if (rendered < 0) continue;
                search = rendered + token.Length;

                if (!plainToRaw.TryGetValue(plainIndex, out var position) || !seenLines.Add(position.Line))
                    continue;
                anchors.Add((rendered, position.Line, position.Column));
            }

            if (anchors.Count == 0)
                anchors.Add((contentStart, sourceStart, 1));
            return InsertMarkers(needle, anchors);
        }

        private static List<RawCharacter> BuildRawCharacters(
            IReadOnlyList<string> sourceLines,
            int sourceStart,
            int sourceEnd)
        {
            var output = new List<RawCharacter>();
            var start = Math.Clamp(sourceStart, 1, Math.Max(1, sourceLines.Count));
            var end = Math.Clamp(sourceEnd, start, Math.Max(start, sourceLines.Count));
            for (var line = start; line <= end && line <= sourceLines.Count; line++)
            {
                var text = sourceLines[line - 1];
                for (var column = 0; column < text.Length; column++)
                    output.Add(new RawCharacter(text[column], line, column + 1));
            }
            return output;
        }

        private static Dictionary<int, RawCharacter> AlignPlainToRaw(string plain, IReadOnlyList<RawCharacter> raw)
        {
            var result = new Dictionary<int, RawCharacter>();
            var cursor = 0;
            for (var index = 0; index < plain.Length; index++)
            {
                var ch = plain[index];
                if (char.IsWhiteSpace(ch)) continue;
                while (cursor < raw.Count && raw[cursor].Character != ch) cursor++;
                if (cursor >= raw.Count) break;
                result[index] = raw[cursor];
                cursor++;
            }
            return result;
        }

        private static string PlainText(AstBlock block)
            => block switch
            {
                HeadingBlock heading => heading.Inlines.ToPlainText(),
                ParagraphBlock paragraph => paragraph.Inlines.ToPlainText(),
                QuoteBlock quote => quote.Inlines.ToPlainText(),
                ListItemBlock item => item.Inlines.ToPlainText(),
                CodeBlock code => code.Text,
                DisplayMathBlock math => math.Text,
                FootnoteDefinitionBlock footnote => footnote.Inlines.ToPlainText(),
                FigureBlock figure => figure.Caption,
                TableBlock table => string.Join(" ", table.Header.Select(cell => cell.Inlines.ToPlainText())
                    .Concat(table.Rows.SelectMany(row => row.Select(cell => cell.Inlines.ToPlainText())))),
                _ => string.Empty
            };

        private static int ContentStart(string needle, AstBlock block)
        {
            if (block is HeadingBlock)
            {
                var brace = needle.IndexOf('{');
                return brace >= 0 ? brace + 1 : 0;
            }
            if (block is QuoteBlock)
            {
                var marker = needle.IndexOf("\\begin{quote}", StringComparison.Ordinal);
                if (marker >= 0)
                {
                    var newline = needle.IndexOf('\n', marker);
                    if (newline >= 0) return newline + 1;
                }
            }
            if (block is ListItemBlock)
            {
                var item = needle.IndexOf("\\item ", StringComparison.Ordinal);
                return item >= 0 ? item + 6 : 0;
            }
            if (block is DisplayMathBlock)
            {
                var marker = needle.IndexOf("\\[", StringComparison.Ordinal);
                if (marker >= 0)
                {
                    var newline = needle.IndexOf('\n', marker);
                    if (newline >= 0) return newline + 1;
                }
            }
            return 0;
        }

        private static string RenderedToken(AstBlock block, char ch)
        {
            if (block is DisplayMathBlock or CodeBlock) return ch.ToString();
            return ch switch
            {
                '\\' => "\\textbackslash{}",
                '{' => "\\{",
                '}' => "\\}",
                '$' => "\\$",
                '&' => "\\&",
                '#' => "\\#",
                '_' => "\\_",
                '%' => "\\%",
                '^' => "\\textasciicircum{}",
                '~' => "\\textasciitilde{}",
                _ => ch.ToString()
            };
        }

        private static int FindRenderedToken(string needle, string token, int start, char sourceCharacter)
        {
            var cursor = Math.Clamp(start, 0, needle.Length);
            while (cursor < needle.Length)
            {
                var candidate = needle.IndexOf(token, cursor, StringComparison.Ordinal);
                if (candidate < 0) return -1;
                if (token.Length == 1 && char.IsLetter(sourceCharacter) && IsInsideCommandName(needle, candidate))
                {
                    cursor = candidate + 1;
                    continue;
                }
                return candidate;
            }
            return -1;
        }

        private static bool IsInsideCommandName(string text, int index)
        {
            var cursor = index - 1;
            while (cursor >= 0 && char.IsLetter(text[cursor])) cursor--;
            return cursor >= 0 && text[cursor] == '\\';
        }

        private static string InsertMarkers(
            string needle,
            IEnumerable<(int Index, int Line, int Column)> anchors)
        {
            var output = needle;
            foreach (var anchor in anchors
                         .Distinct()
                         .OrderByDescending(static anchor => anchor.Index))
            {
                var index = Math.Clamp(anchor.Index, 0, output.Length);
                var marker = MarkerPrefix + anchor.Line.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
                             anchor.Column.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n";
                output = output.Insert(index, marker);
            }
            return output;
        }

        private static string ExtractBody(string latex)
        {
            var title = latex.IndexOf("\\maketitle", StringComparison.Ordinal);
            if (title < 0) return string.Empty;
            var start = latex.IndexOf('\n', title);
            if (start < 0) return string.Empty;
            start++;
            var end = latex.LastIndexOf("\\end{document}", StringComparison.Ordinal);
            if (end <= start) return string.Empty;
            return latex[start..end].Trim();
        }

        private readonly record struct RawCharacter(char Character, int Line, int Column);
    }

    private sealed class ExactSyncIndex
    {
        private const string MarkerPrefix = "% TYPESCRIBE-SOURCE:";
        private readonly SyncPoint[] _points;
        private readonly Dictionary<int, PageBounds> _pageBounds;

        private ExactSyncIndex(SyncPoint[] points)
        {
            _points = points;
            _pageBounds = points
                .GroupBy(static point => point.PageIndex)
                .ToDictionary(
                    static group => group.Key,
                    static group => new PageBounds(
                        group.Min(static point => point.X),
                        group.Max(static point => point.X),
                        group.Min(static point => point.Y),
                        group.Max(static point => point.Y)));
        }

        public int PointCount => _points.Length;

        public static ExactSyncIndex Load(string texPath, string syncPath)
        {
            var markers = LoadMarkers(texPath);
            if (markers.Length == 0) return new ExactSyncIndex([]);

            string[] syncLines;
            using (var file = File.OpenRead(syncPath))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                syncLines = NormalizeNewlines(reader.ReadToEnd()).Split('\n');

            var documentTag = FindDocumentTag(syncLines);
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
                if (page < 0 || !TryParseNode(line, out var tag, out var texLine, out var x, out var y) || tag != documentTag)
                    continue;
                var marker = MarkerForTexLine(markers, texLine);
                if (marker is null) continue;
                points.Add(new SyncPoint(marker.Value.SourceLine, marker.Value.SourceColumn, texLine, page, x, y, 0, 0));
            }

            var array = points
                .DistinctBy(static point => (point.SourceLine, point.SourceStartColumn, point.PageIndex, point.X, point.Y))
                .ToArray();
            var bounds = array.GroupBy(static point => point.PageIndex)
                .ToDictionary(
                    static group => group.Key,
                    static group => new PageBounds(
                        group.Min(static point => point.X),
                        group.Max(static point => point.X),
                        group.Min(static point => point.Y),
                        group.Max(static point => point.Y)));
            for (var index = 0; index < array.Length; index++)
            {
                var point = array[index];
                if (!bounds.TryGetValue(point.PageIndex, out var bound)) continue;
                var xRatio = bound.MaxX <= bound.MinX ? 0 : (point.X - bound.MinX) / (double)(bound.MaxX - bound.MinX);
                var yRatio = bound.MaxY <= bound.MinY ? 0 : (point.Y - bound.MinY) / (double)(bound.MaxY - bound.MinY);
                array[index] = point with
                {
                    PageXRatio = Math.Clamp(xRatio, 0, 1),
                    PageYRatio = Math.Clamp(yRatio, 0, 1)
                };
            }
            return new ExactSyncIndex(array);
        }

        public SyncPoint? FindBySourcePosition(int sourceLine, int sourceColumn, int lineLength)
        {
            if (_points.Length == 0) return null;
            var exact = _points.Where(point => point.SourceLine == sourceLine)
                .OrderBy(static point => point.PageIndex)
                .ThenBy(static point => point.Y)
                .ThenBy(static point => point.X)
                .ToArray();
            if (exact.Length == 0)
            {
                var nearestLine = _points
                    .OrderBy(point => Math.Abs(point.SourceLine - sourceLine))
                    .ThenBy(static point => point.SourceLine)
                    .First().SourceLine;
                exact = _points.Where(point => point.SourceLine == nearestLine)
                    .OrderBy(static point => point.PageIndex)
                    .ThenBy(static point => point.Y)
                    .ThenBy(static point => point.X)
                    .ToArray();
            }
            if (exact.Length == 0) return null;
            var start = Math.Clamp(exact[0].SourceStartColumn, 1, Math.Max(1, lineLength + 1));
            var available = Math.Max(1, lineLength + 1 - start);
            var ratio = Math.Clamp((sourceColumn - start) / (double)available, 0, 1);
            var index = Math.Clamp((int)Math.Round(ratio * (exact.Length - 1)), 0, exact.Length - 1);
            return exact[index];
        }

        public SourceHit? FindSourcePosition(int pageIndex, double xRatio, double yRatio)
        {
            var page = _points.Where(point => point.PageIndex == pageIndex).ToArray();
            if (page.Length == 0) return null;
            var targetX = Math.Clamp(xRatio, 0, 1);
            var targetY = Math.Clamp(yRatio, 0, 1);
            var closest = page.OrderBy(point =>
            {
                var dx = point.PageXRatio - targetX;
                var dy = point.PageYRatio - targetY;
                return (dx * dx) + (dy * dy * 2.2);
            }).First();

            var linePoints = _points.Where(point => point.SourceLine == closest.SourceLine)
                .OrderBy(static point => point.PageIndex)
                .ThenBy(static point => point.Y)
                .ThenBy(static point => point.X)
                .ToArray();
            var position = Array.IndexOf(linePoints, closest);
            var ratio = linePoints.Length <= 1 || position < 0
                ? 0
                : position / (double)(linePoints.Length - 1);
            return new SourceHit(closest.SourceLine, closest.SourceStartColumn, ratio);
        }

        private static SourceMarker[] LoadMarkers(string texPath)
        {
            var lines = File.ReadAllLines(texPath);
            var markers = new List<SourceMarker>();
            for (var index = 0; index < lines.Length; index++)
            {
                var markerIndex = lines[index].IndexOf(MarkerPrefix, StringComparison.Ordinal);
                if (markerIndex < 0) continue;
                var payload = lines[index][(markerIndex + MarkerPrefix.Length)..].Trim();
                var parts = payload.Split(':', 2);
                if (!int.TryParse(parts[0], out var sourceLine)) continue;
                var sourceColumn = 1;
                if (parts.Length > 1) int.TryParse(parts[1], out sourceColumn);
                markers.Add(new SourceMarker(index + 2, Math.Max(1, sourceLine), Math.Max(1, sourceColumn)));
            }
            return markers.OrderBy(static marker => marker.EffectiveTexLine).ToArray();
        }

        private static int FindDocumentTag(IEnumerable<string> syncLines)
        {
            foreach (var line in syncLines)
            {
                if (!line.StartsWith("Input:", StringComparison.Ordinal)) continue;
                var first = line.IndexOf(':');
                var second = line.IndexOf(':', first + 1);
                if (second <= first) continue;
                if (!int.TryParse(line[(first + 1)..second], out var tag)) continue;
                var path = line[(second + 1)..];
                if (path.EndsWith("document.tex", StringComparison.OrdinalIgnoreCase)) return tag;
            }
            return 1;
        }

        private static SourceMarker? MarkerForTexLine(IReadOnlyList<SourceMarker> markers, int texLine)
        {
            var low = 0;
            var high = markers.Count - 1;
            var result = -1;
            while (low <= high)
            {
                var mid = low + ((high - low) / 2);
                if (markers[mid].EffectiveTexLine <= texLine)
                {
                    result = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }
            return result >= 0 ? markers[result] : null;
        }

        private static bool TryParseNode(string line, out int tag, out int texLine, out long x, out long y)
        {
            tag = texLine = 0;
            x = y = 0;
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
            if (firstComma <= 0 || secondComma <= firstComma) return false;
            return long.TryParse(rest[..firstComma], out x) &&
                   long.TryParse(rest[(firstComma + 1)..secondComma], out y);
        }

        private readonly record struct SourceMarker(int EffectiveTexLine, int SourceLine, int SourceColumn);
        private readonly record struct PageBounds(long MinX, long MaxX, long MinY, long MaxY);
    }

    private readonly record struct SourceHit(int SourceLine, int StartColumn, double PositionRatio);
    private sealed record SyncPoint(
        int SourceLine,
        int SourceStartColumn,
        int TexLine,
        int PageIndex,
        long X,
        long Y,
        double PageXRatio,
        double PageYRatio);
}

/// <summary>
/// Applies WorkspaceViewModel navigation requests directly to the visible ManuscriptEditor.
/// This removes the proxy/timer delay from heading navigation in Project Explorer and every
/// other line-based navigation command.
/// </summary>
internal sealed class DirectEditorNavigationFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private ManuscriptEditor? _editor;
    private long _appliedVersion = -1;
    private bool _disposed;

    private DirectEditorNavigationFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        var feature = new DirectEditorNavigationFeature(window, viewModel);
        window.Opened += feature.WindowOpened;
        window.LayoutUpdated += feature.WindowLayoutUpdated;
        window.Closed += feature.WindowClosed;
        viewModel.StateChanged += feature.ViewModelStateChanged;
        feature.TryApplyNavigation();
    }

    private void WindowOpened(object? sender, EventArgs e) => TryApplyNavigation();
    private void WindowLayoutUpdated(object? sender, EventArgs e)
    {
        if (_editor is null) TryApplyNavigation();
    }

    private void ViewModelStateChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(TryApplyNavigation, DispatcherPriority.Background);

    private void TryApplyNavigation()
    {
        if (_disposed) return;
        _editor ??= _window.GetVisualDescendants().OfType<ManuscriptEditor>().FirstOrDefault();
        if (_editor is null || _appliedVersion == _viewModel.EditorNavigationVersion) return;
        _appliedVersion = _viewModel.EditorNavigationVersion;

        foreach (var tabs in _window.GetVisualDescendants().OfType<TabControl>())
        {
            var editorTab = TabItems(tabs).FirstOrDefault(static item =>
                string.Equals(item.Header?.ToString(), "Editor", StringComparison.Ordinal));
            if (editorTab is null) continue;
            tabs.SelectedItem = editorTab;
            break;
        }

        _editor.NavigateToLine(_viewModel.EditorNavigationLine);
    }

    private static IEnumerable<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is System.Collections.IEnumerable source)
            return source.Cast<object?>().OfType<TabItem>();
        return tabs.Items.OfType<TabItem>();
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _viewModel.StateChanged -= ViewModelStateChanged;
        _window.Opened -= WindowOpened;
        _window.LayoutUpdated -= WindowLayoutUpdated;
        _window.Closed -= WindowClosed;
    }
}
