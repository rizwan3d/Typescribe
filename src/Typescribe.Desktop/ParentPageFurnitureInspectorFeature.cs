using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

internal sealed class ParentPageFurnitureInspectorFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;

    private TabControl? _tabs;
    private TabItem? _layoutTab;
    private Control? _defaultLayoutContent;
    private Control? _inspector;
    private PageFurnitureSelection? _selection;

    private TextBlock? _selectionLabel;
    private CheckBox? _showFurniture;
    private CheckBox? _showPageNumbers;
    private TextBox? _fontSize;
    private TextBox? _headerLeft;
    private TextBox? _headerCenter;
    private TextBox? _headerRight;
    private TextBox? _footerLeft;
    private TextBox? _footerCenter;
    private TextBox? _footerRight;

    private TextBlock? _pageStyleLabel;
    private TextBox? _pageWidth;
    private TextBox? _pageHeight;
    private TextBox? _marginTop;
    private TextBox? _marginBottom;
    private TextBox? _marginInner;
    private TextBox? _marginOuter;
    private CheckBox? _facingPages;
    private CheckBox? _cropMarks;
    private TextBlock? _status;

    private bool _installed;
    private bool _queued;
    private bool _disposed;

    private ParentPageFurnitureInspectorFeature(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        var feature = new ParentPageFurnitureInspectorFeature(window, viewModel);
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
        _inspector = BuildInspector();

        PageObjectSelectionHub.FurnitureSelectionChanged += SelectionChanged;
        _installed = true;
        _window.LayoutUpdated -= WindowReady;

        SelectionChanged(PageObjectSelectionHub.SelectedFurniture);
    }

    private Control BuildInspector()
    {
        _selectionLabel = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold
        };
        _showFurniture = new CheckBox { Content = "Show headers and footers" };
        _showPageNumbers = new CheckBox { Content = "Show page numbers" };
        _fontSize = Box("9");
        _headerLeft = Box("Header left");
        _headerCenter = Box("Header center");
        _headerRight = Box("Header right");
        _footerLeft = Box("Footer left");
        _footerCenter = Box("Footer center");
        _footerRight = Box("Footer right");

        _pageStyleLabel = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold
        };
        _pageWidth = Box("6");
        _pageHeight = Box("9");
        _marginTop = Box("0.8");
        _marginBottom = Box("0.8");
        _marginInner = Box("0.85");
        _marginOuter = Box("0.7");
        _facingPages = new CheckBox { Content = "Facing pages" };
        _cropMarks = new CheckBox { Content = "Crop marks" };
        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10.5,
            Opacity = .68
        };

        var applyFurniture = new Button
        {
            Content = "Apply running furniture",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        applyFurniture.Classes.Add("primary");
        applyFurniture.Click += async (_, _) => await ApplyFurnitureAsync();

        var applyParent = new Button
        {
            Content = "Apply parent page",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        applyParent.Click += async (_, _) => await ApplyParentPageAsync();

        var back = new Button
        {
            Content = "Back to page layout",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        back.Click += (_, _) => PageObjectSelectionHub.SelectFurniture(null);

        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 8 };
        panel.Children.Add(Section("SELECTED PAGE OBJECT"));
        panel.Children.Add(_selectionLabel);

        panel.Children.Add(Section("RUNNING FURNITURE"));
        panel.Children.Add(_showFurniture);
        panel.Children.Add(_showPageNumbers);
        panel.Children.Add(Field("Header/footer size (pt)", _fontSize));
        panel.Children.Add(Field("Header left", _headerLeft));
        panel.Children.Add(Field("Header center", _headerCenter));
        panel.Children.Add(Field("Header right", _headerRight));
        panel.Children.Add(Field("Footer left", _footerLeft));
        panel.Children.Add(Field("Footer center", _footerCenter));
        panel.Children.Add(Field("Footer right", _footerRight));
        panel.Children.Add(applyFurniture);

        panel.Children.Add(new Separator { Margin = new Thickness(0, 7) });
        panel.Children.Add(Section("PARENT PAGE"));
        panel.Children.Add(_pageStyleLabel);
        panel.Children.Add(TwoColumns(Field("Width (in)", _pageWidth), Field("Height (in)", _pageHeight)));
        panel.Children.Add(TwoColumns(Field("Top margin", _marginTop), Field("Bottom margin", _marginBottom)));
        panel.Children.Add(TwoColumns(Field("Inner margin", _marginInner), Field("Outer margin", _marginOuter)));
        panel.Children.Add(_facingPages);
        panel.Children.Add(_cropMarks);
        panel.Children.Add(applyParent);
        panel.Children.Add(back);
        panel.Children.Add(_status);

        return new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
    }

    private void SelectionChanged(PageFurnitureSelection? selection)
    {
        if (_disposed || _layoutTab is null) return;

        _selection = selection;
        if (selection is null)
        {
            if (_defaultLayoutContent is not null)
                _layoutTab.Content = _defaultLayoutContent;
            return;
        }

        if (_inspector is not null)
            _layoutTab.Content = _inspector;
        if (_tabs is not null)
            _tabs.SelectedIndex = 1;

        Refresh(selection);
    }

    private void Refresh(PageFurnitureSelection selection)
    {
        if (_selectionLabel is null || _showFurniture is null || _showPageNumbers is null ||
            _fontSize is null || _headerLeft is null || _headerCenter is null || _headerRight is null ||
            _footerLeft is null || _footerCenter is null || _footerRight is null ||
            _pageStyleLabel is null || _pageWidth is null || _pageHeight is null ||
            _marginTop is null || _marginBottom is null || _marginInner is null || _marginOuter is null ||
            _facingPages is null || _cropMarks is null || _status is null)
            return;

        var style = _viewModel.CurrentStyle;
        _selectionLabel.Text =
            $"Page {selection.DisplayPageNumberText} · {DisplayName(selection.Kind)}" +
            (selection.IsLeftPage ? " · left" : " · right");

        _showFurniture.IsChecked = style.ShowHeadersAndFooters;
        _showPageNumbers.IsChecked = style.ShowPageNumbers;
        _fontSize.Text = Number(style.HeaderFooterFontSizePoints);
        _headerLeft.Text = style.HeaderLeft;
        _headerCenter.Text = style.HeaderCenter;
        _headerRight.Text = style.HeaderRight;
        _footerLeft.Text = style.FooterLeft;
        _footerCenter.Text = style.FooterCenter;
        _footerRight.Text = style.FooterRight;

        var pageStyleId = selection.PageStyleId ?? style.DefaultPageStyleId;
        var resolved = style.NamedStyles.ResolvePage(pageStyleId);
        _pageStyleLabel.Text = $"Parent: {pageStyleId}";

        _pageWidth.Text = Number(resolved?.WidthInches ?? style.PageWidthInches);
        _pageHeight.Text = Number(resolved?.HeightInches ?? style.PageHeightInches);
        _marginTop.Text = Number(resolved?.MarginTopInches ?? style.MarginTopInches);
        _marginBottom.Text = Number(resolved?.MarginBottomInches ?? style.MarginBottomInches);
        _marginInner.Text = Number(resolved?.MarginInnerInches ?? style.MarginInnerInches);
        _marginOuter.Text = Number(resolved?.MarginOuterInches ?? style.MarginOuterInches);
        _facingPages.IsChecked = resolved?.FacingPages ?? false;
        _cropMarks.IsChecked = resolved?.CropMarks ?? false;

        _status.Text = selection.Kind switch
        {
            PageFurnitureKind.ParentPage =>
                "This page inherits its trim, margins, facing behavior and print marks from the selected parent page.",
            PageFurnitureKind.PageNumber =>
                "The number is generated from section numbering. Edit footer center to replace it with fixed footer text.",
            _ =>
                "You can type directly in the page margin or edit the same running element here."
        };
    }

    private async Task ApplyFurnitureAsync()
    {
        if (_selection is null || _showFurniture is null || _showPageNumbers is null ||
            _fontSize is null || _headerLeft is null || _headerCenter is null || _headerRight is null ||
            _footerLeft is null || _footerCenter is null || _footerRight is null || _status is null)
            return;

        try
        {
            var size = Parse(_fontSize.Text, _viewModel.CurrentStyle.HeaderFooterFontSizePoints);
            var updated = (_viewModel.CurrentStyle with
            {
                ShowHeadersAndFooters = _showFurniture.IsChecked == true,
                ShowPageNumbers = _showPageNumbers.IsChecked == true,
                HeaderFooterFontSizePoints = Math.Clamp(size, 5, 24),
                HeaderLeft = _headerLeft.Text ?? string.Empty,
                HeaderCenter = _headerCenter.Text ?? string.Empty,
                HeaderRight = _headerRight.Text ?? string.Empty,
                FooterLeft = _footerLeft.Text ?? string.Empty,
                FooterCenter = _footerCenter.Text ?? string.Empty,
                FooterRight = _footerRight.Text ?? string.Empty
            }).Validate();

            await _viewModel.UpdateStyleAsync(updated);
            _status.Text = "Running furniture updated in the same book style used by PDF publishing.";
            Refresh(_selection);
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private async Task ApplyParentPageAsync()
    {
        if (_selection is null || _pageWidth is null || _pageHeight is null ||
            _marginTop is null || _marginBottom is null || _marginInner is null || _marginOuter is null ||
            _facingPages is null || _cropMarks is null || _status is null)
            return;

        try
        {
            var style = _viewModel.CurrentStyle;
            var pageStyleId = _selection.PageStyleId ?? style.DefaultPageStyleId;
            var raw = style.NamedStyles.PageStyles.FirstOrDefault(page =>
                string.Equals(page.Id, pageStyleId, StringComparison.OrdinalIgnoreCase))
                ?? new PageStyleDefinition(pageStyleId, pageStyleId, null);

            var width = Math.Clamp(Parse(_pageWidth.Text, raw.WidthInches ?? style.PageWidthInches), 3, 20);
            var height = Math.Clamp(Parse(_pageHeight.Text, raw.HeightInches ?? style.PageHeightInches), 3, 24);
            var top = Math.Clamp(Parse(_marginTop.Text, raw.MarginTopInches ?? style.MarginTopInches), 0, 5);
            var bottom = Math.Clamp(Parse(_marginBottom.Text, raw.MarginBottomInches ?? style.MarginBottomInches), 0, 5);
            var inner = Math.Clamp(Parse(_marginInner.Text, raw.MarginInnerInches ?? style.MarginInnerInches), 0, 5);
            var outer = Math.Clamp(Parse(_marginOuter.Text, raw.MarginOuterInches ?? style.MarginOuterInches), 0, 5);

            var updatedPage = raw with
            {
                WidthInches = width,
                HeightInches = height,
                MarginTopInches = top,
                MarginBottomInches = bottom,
                MarginInnerInches = inner,
                MarginOuterInches = outer,
                FacingPages = _facingPages.IsChecked == true,
                CropMarks = _cropMarks.IsChecked == true
            };

            var found = false;
            var pages = style.NamedStyles.PageStyles.Select(page =>
            {
                if (!string.Equals(page.Id, pageStyleId, StringComparison.OrdinalIgnoreCase))
                    return page;
                found = true;
                return updatedPage;
            }).ToList();
            if (!found) pages.Add(updatedPage);

            var catalog = style.NamedStyles with { PageStyles = pages };
            var updatedStyle = style with { NamedStyles = catalog };

            if (string.Equals(pageStyleId, style.DefaultPageStyleId, StringComparison.OrdinalIgnoreCase))
            {
                updatedStyle = updatedStyle with
                {
                    PageWidthInches = width,
                    PageHeightInches = height,
                    MarginTopInches = top,
                    MarginBottomInches = bottom,
                    MarginInnerInches = inner,
                    MarginOuterInches = outer
                };
            }

            await _viewModel.UpdateStyleAsync(updatedStyle.Validate());
            _status.Text = string.Equals(pageStyleId, style.DefaultPageStyleId, StringComparison.OrdinalIgnoreCase)
                ? "Default parent page updated for both the visual canvas and publishing geometry."
                : $"Parent page '{pageStyleId}' updated.";
            Refresh(_selection);
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        PageObjectSelectionHub.FurnitureSelectionChanged -= SelectionChanged;
        _window.Opened -= WindowReady;
        _window.LayoutUpdated -= WindowReady;
        _window.Closed -= WindowClosed;
    }

    private static string DisplayName(PageFurnitureKind kind)
        => kind switch
        {
            PageFurnitureKind.ParentPage => "Parent page",
            PageFurnitureKind.HeaderLeft => "Header left",
            PageFurnitureKind.HeaderCenter => "Header center",
            PageFurnitureKind.HeaderRight => "Header right",
            PageFurnitureKind.FooterLeft => "Footer left",
            PageFurnitureKind.FooterCenter => "Footer center",
            PageFurnitureKind.FooterRight => "Footer right",
            PageFurnitureKind.PageNumber => "Page number",
            _ => kind.ToString()
        };

    private static TextBox Box(string placeholder)
        => new() { PlaceholderText = placeholder };

    private static Grid Field(string label, Control control)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 3 };
        grid.Children.Add(new TextBlock { Text = label, FontSize = 10.5, Opacity = .7 });
        Grid.SetRow(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private static Grid TwoColumns(Control left, Control right)
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

    private static IReadOnlyList<TabItem> TabItems(TabControl tabs)
        => tabs.ItemsSource is IEnumerable source
            ? source.Cast<object?>().OfType<TabItem>().ToArray()
            : tabs.Items.OfType<TabItem>().ToArray();

    private static IEnumerable<string> Headers(TabControl tabs)
        => TabItems(tabs).Select(item => item.Header?.ToString() ?? string.Empty);

    private static double Parse(string? text, double fallback)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant) ||
           double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out invariant)
            ? invariant
            : fallback;

    private static string Number(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);
}
