using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Application.Abstractions;
using Typescribe.Application.Services;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

internal sealed class TextFrameContextInspectorFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly IDocumentParser _parser;

    private TabControl? _tabs;
    private TabItem? _layoutTab;
    private Control? _defaultLayoutContent;
    private Control? _frameInspector;
    private PageTextFrameSelection? _selection;

    private TextBlock? _frameId;
    private TextBox? _nextFrame;
    private TextBox? _columns;
    private TextBox? _columnGap;
    private TextBox? _x;
    private TextBox? _y;
    private TextBox? _width;
    private TextBox? _height;
    private TextBox? _pageOffset;
    private TextBox? _insetTop;
    private TextBox? _insetRight;
    private TextBox? _insetBottom;
    private TextBox? _insetLeft;
    private TextBlock? _overset;
    private TextBlock? _status;

    private bool _installed;
    private bool _queued;
    private bool _disposed;

    private TextFrameContextInspectorFeature(
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
        var feature = new TextFrameContextInspectorFeature(window, viewModel, parser);
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
        var tabs = _window.GetVisualDescendants().OfType<TabControl>()
            .FirstOrDefault(candidate =>
                candidate.Classes.Contains("unified-inspector-tabs") ||
                Headers(candidate).SequenceEqual(new[] { "Text", "Layout", "Image", "Section" }));
        if (tabs is null) return;

        var layout = TabItems(tabs)
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), "Layout", StringComparison.OrdinalIgnoreCase));
        if (layout is null) return;

        _tabs = tabs;
        _layoutTab = layout;
        _defaultLayoutContent = layout.Content as Control;
        _frameInspector = BuildFrameInspector();

        PageObjectSelectionHub.TextFrameSelectionChanged += SelectionChanged;
        _installed = true;
        _window.LayoutUpdated -= WindowReady;

        SelectionChanged(PageObjectSelectionHub.SelectedTextFrame);
    }

    private Control BuildFrameInspector()
    {
        _frameId = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        _nextFrame = new TextBox { Watermark = "Next frame ID (optional)" };
        _columns = new TextBox { Watermark = "1" };
        _columnGap = new TextBox { Watermark = "12" };
        _x = new TextBox { Watermark = "Auto" };
        _y = new TextBox { Watermark = "Auto" };
        _width = new TextBox { Watermark = "Auto" };
        _height = new TextBox { Watermark = "Auto" };
        _pageOffset = new TextBox { Watermark = "0" };
        _insetTop = new TextBox { Watermark = "0" };
        _insetRight = new TextBox { Watermark = "0" };
        _insetBottom = new TextBox { Watermark = "0" };
        _insetLeft = new TextBox { Watermark = "0" };
        _overset = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10.5,
            FontWeight = FontWeight.SemiBold
        };
        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10.5,
            Opacity = .68
        };

        var apply = new Button
        {
            Content = "Apply text frame",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        apply.Classes.Add("primary");
        apply.Click += (_, _) => ApplyFrame(resetGeometry: false);

        var autoFlow = new Button
        {
            Content = "Return to auto-flow",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        autoFlow.Click += (_, _) => ApplyFrame(resetGeometry: true);

        var pageLayout = new Button
        {
            Content = "Back to page layout",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        pageLayout.Click += (_, _) => PageObjectSelectionHub.SelectTextFrame(null);

        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 8 };
        panel.Children.Add(Section("TEXT FRAME"));
        panel.Children.Add(_frameId);
        panel.Children.Add(_overset);
        panel.Children.Add(Field("Next frame", _nextFrame));
        panel.Children.Add(TwoColumns(Field("Columns", _columns), Field("Column gap (pt)", _columnGap)));

        panel.Children.Add(Section("GEOMETRY"));
        panel.Children.Add(TwoColumns(Field("X (pt)", _x), Field("Y (pt)", _y)));
        panel.Children.Add(TwoColumns(Field("Width (pt)", _width), Field("Height (pt)", _height)));
        panel.Children.Add(Field("Page offset", _pageOffset));

        panel.Children.Add(Section("INSETS"));
        panel.Children.Add(TwoColumns(Field("Top", _insetTop), Field("Right", _insetRight)));
        panel.Children.Add(TwoColumns(Field("Bottom", _insetBottom), Field("Left", _insetLeft)));

        panel.Children.Add(apply);
        panel.Children.Add(autoFlow);
        panel.Children.Add(pageLayout);
        panel.Children.Add(_status);

        return new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
    }

    private void SelectionChanged(PageTextFrameSelection? selection)
    {
        if (_disposed || _layoutTab is null) return;

        _selection = selection;
        if (selection is null)
        {
            if (_defaultLayoutContent is not null)
                _layoutTab.Content = _defaultLayoutContent;
            return;
        }

        if (_frameInspector is not null)
            _layoutTab.Content = _frameInspector;
        if (_tabs is not null)
            _tabs.SelectedIndex = 1;
        Refresh(selection);
    }

    private void Refresh(PageTextFrameSelection selection)
    {
        if (_frameId is null || _nextFrame is null || _columns is null || _columnGap is null ||
            _x is null || _y is null || _width is null || _height is null || _pageOffset is null ||
            _insetTop is null || _insetRight is null || _insetBottom is null || _insetLeft is null ||
            _overset is null || _status is null)
            return;

        _frameId.Text = selection.FrameId;
        _nextFrame.Text = selection.NextFrameId ?? string.Empty;
        _columns.Text = selection.Columns.ToString(CultureInfo.InvariantCulture);
        _columnGap.Text = selection.ColumnGapPoints.ToString("0.###", CultureInfo.InvariantCulture);
        _x.Text = Optional(selection.XPoints);
        _y.Text = Optional(selection.YPoints);
        _width.Text = Optional(selection.WidthPoints);
        _height.Text = Optional(selection.HeightPoints);
        _pageOffset.Text = selection.PageOffset.ToString(CultureInfo.InvariantCulture);
        _insetTop.Text = selection.InsetTopPoints.ToString("0.###", CultureInfo.InvariantCulture);
        _insetRight.Text = selection.InsetRightPoints.ToString("0.###", CultureInfo.InvariantCulture);
        _insetBottom.Text = selection.InsetBottomPoints.ToString("0.###", CultureInfo.InvariantCulture);
        _insetLeft.Text = selection.InsetLeftPoints.ToString("0.###", CultureInfo.InvariantCulture);

        _overset.Text = selection.Overset
            ? "⚠ Overset text — enlarge this frame or link its output to another frame."
            : "Frame content fits.";
        _overset.Foreground = new SolidColorBrush(Color.Parse(selection.Overset ? "#F14C4C" : "#89D185"));
        _status.Text = "Drag the frame label to move it. Drag the lower-right blue handle to resize.";
    }

    private void ApplyFrame(bool resetGeometry)
    {
        if (_selection is null || _status is null || _nextFrame is null ||
            _columns is null || _columnGap is null || _x is null || _y is null ||
            _width is null || _height is null || _pageOffset is null ||
            _insetTop is null || _insetRight is null || _insetBottom is null || _insetLeft is null)
            return;

        try
        {
            var source = _viewModel.EditorText ?? string.Empty;
            var ast = _parser.Parse(source);
            var block = TextFrameSourceEditor.FindFrameBlock(ast, _selection.FrameId, _selection.SourceLine);
            var current = block?.Formatting?.TextFrame;
            if (block is null || current is null)
            {
                _status.Text = "The selected frame moved; select it again.";
                return;
            }

            TextFrameFormatting updatedFrame;
            if (resetGeometry)
            {
                updatedFrame = current with
                {
                    XPoints = null,
                    YPoints = null,
                    WidthPoints = null,
                    HeightPoints = null,
                    PageOffset = 0
                };
            }
            else
            {
                updatedFrame = current with
                {
                    NextFrameId = Clean(_nextFrame.Text),
                    Columns = Math.Clamp(Integer(_columns.Text, current.Columns), 1, 12),
                    ColumnGapPoints = Math.Clamp(Number(_columnGap.Text, current.ColumnGapPoints), 0, 144),
                    XPoints = OptionalNumber(_x.Text),
                    YPoints = OptionalNumber(_y.Text),
                    WidthPoints = ClampOptional(OptionalNumber(_width.Text), 36, 4000),
                    HeightPoints = ClampOptional(OptionalNumber(_height.Text), 36, 4000),
                    PageOffset = Math.Clamp(Integer(_pageOffset.Text, current.PageOffset), 0, 999),
                    InsetTopPoints = Math.Clamp(Number(_insetTop.Text, current.InsetTopPoints), 0, 720),
                    InsetRightPoints = Math.Clamp(Number(_insetRight.Text, current.InsetRightPoints), 0, 720),
                    InsetBottomPoints = Math.Clamp(Number(_insetBottom.Text, current.InsetBottomPoints), 0, 720),
                    InsetLeftPoints = Math.Clamp(Number(_insetLeft.Text, current.InsetLeftPoints), 0, 720)
                };
            }

            var updated = TextFrameSourceEditor.UpdateFrame(
                source,
                ast,
                current.Id,
                _ => updatedFrame,
                block.SourceLine);
            _viewModel.UpdateEditorText(updated);

            var next = new PageTextFrameSelection(
                block.SourceLine,
                updatedFrame.Id,
                updatedFrame.NextFrameId,
                updatedFrame.Columns,
                updatedFrame.ColumnGapPoints,
                updatedFrame.InsetTopPoints,
                updatedFrame.InsetRightPoints,
                updatedFrame.InsetBottomPoints,
                updatedFrame.InsetLeftPoints,
                updatedFrame.XPoints,
                updatedFrame.YPoints,
                updatedFrame.WidthPoints,
                updatedFrame.HeightPoints,
                updatedFrame.PageOffset,
                Overset: false);
            PageObjectSelectionHub.SelectTextFrame(next);
            _status.Text = resetGeometry
                ? "Frame returned to automatic document flow."
                : "Text frame updated in the canonical manuscript.";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        PageObjectSelectionHub.TextFrameSelectionChanged -= SelectionChanged;
        _window.Opened -= WindowReady;
        _window.LayoutUpdated -= WindowReady;
        _window.Closed -= WindowClosed;
    }

    private static IReadOnlyList<TabItem> TabItems(TabControl tabs)
        => tabs.ItemsSource is IEnumerable source
            ? source.Cast<object?>().OfType<TabItem>().ToArray()
            : tabs.Items.OfType<TabItem>().ToArray();

    private static IEnumerable<string> Headers(TabControl tabs)
        => TabItems(tabs).Select(item => item.Header?.ToString() ?? string.Empty);

    private static Control Field(string label, Control control)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 3 };
        grid.Children.Add(new TextBlock { Text = label, FontSize = 10.5, Opacity = .7 });
        Grid.SetRow(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private static Control TwoColumns(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 7 };
        grid.Children.Add(left);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    private static TextBlock Section(string text)
        => new()
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Opacity = .6,
            Margin = new Thickness(0, 6, 0, 0)
        };

    private static string Optional(double? value)
        => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;

    private static double? OptionalNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant)) return invariant;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var local) ? local : null;
    }

    private static double Number(string? text, double fallback)
        => OptionalNumber(text) ?? fallback;

    private static int Integer(string? text, int fallback)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
           int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value)
            ? value
            : fallback;

    private static double? ClampOptional(double? value, double minimum, double maximum)
        => value is null ? null : Math.Clamp(value.Value, minimum, maximum);

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
