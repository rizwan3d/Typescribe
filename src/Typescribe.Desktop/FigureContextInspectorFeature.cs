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

internal sealed class FigureContextInspectorFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly IDocumentParser _parser;
    private TabControl? _tabs;
    private TextBlock? _source;
    private TextBox? _caption;
    private TextBox? _width;
    private TextBox? _height;
    private ComboBox? _alignment;
    private ComboBox? _placement;
    private ComboBox? _wrap;
    private TextBox? _offsetX;
    private TextBox? _offsetY;
    private TextBlock? _status;
    private PageFigureSelection? _selection;
    private bool _installed;
    private bool _queued;
    private bool _disposed;

    private FigureContextInspectorFeature(
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
        var feature = new FigureContextInspectorFeature(window, viewModel, parser);
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

        var imageTab = TabItems(tabs)
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), "Image", StringComparison.OrdinalIgnoreCase));
        if (imageTab is null) return;

        _tabs = tabs;
        imageTab.Content = BuildInspector();
        PageObjectSelectionHub.FigureSelectionChanged += SelectionChanged;
        _installed = true;
        _window.LayoutUpdated -= WindowReady;

        if (PageObjectSelectionHub.SelectedFigure is { } selected)
            SelectionChanged(selected);
        else
            Refresh(null);
    }

    private Control BuildInspector()
    {
        _source = new TextBlock
        {
            Text = "No figure selected",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = .68
        };
        _caption = new TextBox { Watermark = "Caption" };
        _width = new TextBox { Watermark = "90" };
        _height = new TextBox { Watermark = "Auto" };
        _alignment = new ComboBox { ItemsSource = Enum.GetValues<FigureAlignment>() };
        _placement = new ComboBox { ItemsSource = Enum.GetValues<FigurePlacement>() };
        _wrap = new ComboBox { ItemsSource = Enum.GetValues<TextWrapMode>() };
        _offsetX = new TextBox { Watermark = "0" };
        _offsetY = new TextBox { Watermark = "0" };
        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10.5,
            Opacity = .64
        };

        var apply = new Button
        {
            Content = "Apply figure",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        apply.Classes.Add("primary");
        apply.Click += (_, _) => Apply();

        var reset = new Button
        {
            Content = "Reset position",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        reset.Click += (_, _) =>
        {
            if (_offsetX is not null) _offsetX.Text = "0";
            if (_offsetY is not null) _offsetY.Text = "0";
            if (_wrap is not null) _wrap.SelectedItem = TextWrapMode.None;
            Apply();
        };

        var size = TwoColumns(Field("Width %", _width), Field("Height (in)", _height));
        var position = TwoColumns(Field("X offset (pt)", _offsetX), Field("Y offset (pt)", _offsetY));

        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
        panel.Children.Add(Section("SELECTED FIGURE"));
        panel.Children.Add(_source);
        panel.Children.Add(Field("Caption", _caption));
        panel.Children.Add(Section("SIZE"));
        panel.Children.Add(size);
        panel.Children.Add(Field("Alignment", _alignment));
        panel.Children.Add(Section("FLOW"));
        panel.Children.Add(Field("Placement", _placement));
        panel.Children.Add(Field("Text wrap", _wrap));
        panel.Children.Add(position);
        panel.Children.Add(apply);
        panel.Children.Add(reset);
        panel.Children.Add(_status);

        return new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
    }

    private void SelectionChanged(PageFigureSelection? selection)
    {
        if (_disposed) return;
        _selection = selection;
        Refresh(selection);
        if (selection is not null && _tabs is not null)
            _tabs.SelectedIndex = 2;
    }

    private void Refresh(PageFigureSelection? selection)
    {
        if (_source is null || _caption is null || _width is null || _height is null ||
            _alignment is null || _placement is null || _wrap is null ||
            _offsetX is null || _offsetY is null || _status is null)
            return;

        if (selection is null)
        {
            _source.Text = "Click an image on a facing page to edit it.";
            _caption.Text = string.Empty;
            _width.Text = string.Empty;
            _height.Text = string.Empty;
            _alignment.SelectedItem = FigureAlignment.Center;
            _placement.SelectedItem = FigurePlacement.HereOrTop;
            _wrap.SelectedItem = TextWrapMode.None;
            _offsetX.Text = "0";
            _offsetY.Text = "0";
            _status.Text = "Drag an image to position it, or use the fields for exact values.";
            return;
        }

        _source.Text = selection.Source;
        _caption.Text = selection.Caption;
        _width.Text = selection.Layout?.WidthPercent?.ToString("0.###", CultureInfo.InvariantCulture)
                      ?? Math.Clamp(selection.WidthPoints / Math.Max(1, _viewModel.CurrentStyle.PageWidthInches * 72) * 100, 10, 100)
                          .ToString("0.###", CultureInfo.InvariantCulture);
        _height.Text = selection.Layout?.HeightInches?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;
        _alignment.SelectedItem = selection.Layout?.Alignment ?? FigureAlignment.Center;
        _placement.SelectedItem = selection.Layout?.Placement ?? FigurePlacement.HereOrTop;
        _wrap.SelectedItem = selection.Anchored?.Wrap ?? TextWrapMode.None;
        _offsetX.Text = (selection.Anchored?.OffsetXPoints ?? 0).ToString("0.###", CultureInfo.InvariantCulture);
        _offsetY.Text = (selection.Anchored?.OffsetYPoints ?? 0).ToString("0.###", CultureInfo.InvariantCulture);
        _status.Text = "Blue outline = selected. Drag the image or its lower-right handle for direct manipulation.";
    }

    private void Apply()
    {
        if (_selection is null || _caption is null || _width is null || _height is null ||
            _alignment is null || _placement is null || _wrap is null ||
            _offsetX is null || _offsetY is null || _status is null)
            return;

        try
        {
            var source = _viewModel.EditorText ?? string.Empty;
            var ast = _parser.Parse(source);
            var figure = FigureSourceEditor.FindFigure(
                ast,
                _selection.SourceLine,
                _selection.Identifier,
                _selection.Source);
            if (figure is null)
            {
                _status.Text = "The selected figure moved; click it again.";
                return;
            }

            var width = OptionalNumber(_width.Text);
            var height = OptionalNumber(_height.Text);
            var layout = (figure.Layout ?? new FigureLayout()) with
            {
                WidthPercent = width is null ? figure.Layout?.WidthPercent : Math.Clamp(width.Value, 10, 100),
                HeightInches = height is null ? null : Math.Clamp(height.Value, .25, 24),
                Alignment = _alignment.SelectedItem is FigureAlignment alignment ? alignment : figure.Layout?.Alignment,
                Placement = _placement.SelectedItem is FigurePlacement placement ? placement : figure.Layout?.Placement
            };

            var updatedFigure = figure with
            {
                Caption = _caption.Text?.Trim() ?? string.Empty,
                Layout = layout
            };

            var updatedSource = FigureSourceEditor.ReplaceFigure(source, figure, updatedFigure);
            var afterFigureAst = _parser.Parse(updatedSource);
            var afterFigure = FigureSourceEditor.FindFigure(
                afterFigureAst,
                updatedFigure.SourceLine,
                updatedFigure.Identifier,
                updatedFigure.Source) ?? afterFigureAst.Blocks.OfType<FigureBlock>().FirstOrDefault();
            if (afterFigure is null)
                throw new InvalidOperationException("Could not locate the updated figure.");

            var existing = afterFigure.Formatting?.AnchoredObject;
            var anchored = new AnchoredObjectFormatting(
                existing?.Id ?? afterFigure.Identifier ?? $"figure-{afterFigure.SourceLine}",
                existing?.Placement ?? ToFloatPlacement(layout.Placement),
                _wrap.SelectedItem is TextWrapMode wrap ? wrap : existing?.Wrap ?? TextWrapMode.None,
                Number(_offsetX.Text),
                Number(_offsetY.Text),
                existing?.WrapTopPoints ?? 0,
                existing?.WrapRightPoints ?? 0,
                existing?.WrapBottomPoints ?? 0,
                existing?.WrapLeftPoints ?? 0,
                existing?.KeepWithAnchor ?? true);

            updatedSource = RichBlockFormattingEditor.SetAnchoredObject(
                updatedSource,
                afterFigure.SourceLine,
                anchored);
            _viewModel.UpdateEditorText(updatedSource);

            var finalAst = _parser.Parse(updatedSource);
            var finalFigure = FigureSourceEditor.FindFigure(
                finalAst,
                afterFigure.SourceLine,
                afterFigure.Identifier,
                afterFigure.Source) ?? afterFigure;

            var next = new PageFigureSelection(
                finalFigure.SourceLine,
                finalFigure.Identifier,
                finalFigure.Source,
                finalFigure.Caption,
                finalFigure.Layout,
                finalFigure.Formatting?.AnchoredObject ?? anchored,
                _selection.WidthPoints,
                _selection.HeightPoints);
            PageObjectSelectionHub.SelectFigure(next);
            _status.Text = "Figure updated in the canonical manuscript.";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        PageObjectSelectionHub.FigureSelectionChanged -= SelectionChanged;
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

    private static double Number(string? text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
           double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            ? value
            : 0;

    private static double? OptionalNumber(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : Number(text);

    private static FloatPlacementMode ToFloatPlacement(FigurePlacement? placement)
        => placement switch
        {
            FigurePlacement.Inline => FloatPlacementMode.Inline,
            FigurePlacement.Top => FloatPlacementMode.Top,
            FigurePlacement.Bottom => FloatPlacementMode.Bottom,
            FigurePlacement.Page => FloatPlacementMode.Page,
            _ => FloatPlacementMode.Here
        };
}
