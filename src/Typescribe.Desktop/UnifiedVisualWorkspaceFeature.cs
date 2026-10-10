using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.Editing;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

/// <summary>
/// Turns the existing authoring, planning and layout features into one visual workspace.
/// Markdown and the semantic document model remain canonical; this class only reorganizes
/// already-existing controls and adds lightweight contextual layout/image controls.
/// </summary>
internal sealed class UnifiedVisualWorkspaceFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;

    private TabControl? _centerTabs;
    private ToggleButton? _projectView;
    private ToggleButton? _corkboardView;
    private ToggleButton? _outlinerView;
    private bool _installed;
    private bool _queued;
    private bool _disposed;

    private UnifiedVisualWorkspaceFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var feature = new UnifiedVisualWorkspaceFeature(window, viewModel);
        window.Opened += feature.WindowOpened;
        window.LayoutUpdated += feature.WindowLayoutUpdated;
        window.Closed += feature.WindowClosed;
        feature.QueueInstall();
    }

    private void WindowOpened(object? sender, EventArgs e) => QueueInstall();

    private void WindowLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_installed) QueueInstall();
    }

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

        var controls = _window.GetVisualDescendants().OfType<Control>().ToArray();
        var centerTabs = controls.OfType<TabControl>()
            .FirstOrDefault(tabs => HasHeaders(tabs, "Editor", "Corkboard", "Outliner"));
        var inspectorTabs = controls.OfType<TabControl>()
            .FirstOrDefault(tabs => HasHeader(tabs, "Inspector") && HasHeader(tabs, "Typography"));
        var leftTabs = controls.OfType<TabControl>()
            .FirstOrDefault(tabs => HasHeader(tabs, "Binder") && HasHeader(tabs, "Search"));

        // Typography is installed asynchronously by the rich-editor feature. Waiting for it
        // keeps that feature's original discovery contract intact before we simplify the tabs.
        if (centerTabs is null || inspectorTabs is null || leftTabs is null) return;

        _centerTabs = centerTabs;
        InstallProjectViewSwitcher(leftTabs, centerTabs);
        InstallUnifiedInspector(inspectorTabs);
        CollapseCenterTabHeaders(centerTabs);

        centerTabs.SelectionChanged += CenterSelectionChanged;
        SyncViewSwitcher();

        if (!_window.Classes.Contains("unified-visual-workspace"))
            _window.Classes.Add("unified-visual-workspace");

        _installed = true;
    }

    private void InstallProjectViewSwitcher(TabControl leftTabs, TabControl centerTabs)
    {
        var binder = TabItems(leftTabs)
            .FirstOrDefault(item => string.Equals(HeaderText(item), "Binder", StringComparison.OrdinalIgnoreCase));
        if (binder?.Content is not Control binderContent) return;

        binder.Content = null;

        var heading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(10, 8, 8, 5)
        };
        heading.Children.Add(new TextBlock
        {
            Text = "Binder",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var addHint = new TextBlock
        {
            Text = "⌘K",
            FontSize = 10.5,
            Opacity = .46,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(addHint, 1);
        heading.Children.Add(addHint);

        _projectView = ViewToggle("Project", 0);
        _corkboardView = ViewToggle("Corkboard", 1);
        _outlinerView = ViewToggle("Outliner", 2);

        var switcher = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(8, 0, 8, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { _projectView, _corkboardView, _outlinerView }
        };
        switcher.Classes.Add("workspace-view-switcher");

        var shell = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*")
        };
        shell.Children.Add(heading);
        Grid.SetRow(switcher, 1);
        shell.Children.Add(switcher);
        Grid.SetRow(binderContent, 2);
        shell.Children.Add(binderContent);

        binder.Content = shell;

        ToggleButton ViewToggle(string label, int index)
        {
            var button = new ToggleButton
            {
                Content = label,
                MinWidth = label == "Corkboard" ? 82 : 68,
                Height = 29,
                MinHeight = 29,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            button.Classes.Add("workspace-view-toggle");
            button.Click += (_, _) =>
            {
                centerTabs.SelectedIndex = index;
                SyncViewSwitcher();
            };
            return button;
        }
    }

    private void CollapseCenterTabHeaders(TabControl centerTabs)
    {
        centerTabs.Classes.Add("unified-center-tabs");
        foreach (var item in TabItems(centerTabs))
        {
            item.Height = 0;
            item.MinHeight = 0;
            item.Width = 0;
            item.MinWidth = 0;
            item.Padding = new Thickness(0);
            item.Margin = new Thickness(0);
            item.Opacity = 0;
            item.IsHitTestVisible = false;
        }
    }

    private void CenterSelectionChanged(object? sender, SelectionChangedEventArgs e) => SyncViewSwitcher();

    private void SyncViewSwitcher()
    {
        if (_centerTabs is null) return;
        if (_projectView is not null) _projectView.IsChecked = _centerTabs.SelectedIndex == 0;
        if (_corkboardView is not null) _corkboardView.IsChecked = _centerTabs.SelectedIndex == 1;
        if (_outlinerView is not null) _outlinerView.IsChecked = _centerTabs.SelectedIndex == 2;
    }

    private void InstallUnifiedInspector(TabControl inspector)
    {
        var items = TabItems(inspector).ToList();

        var document = TakeContent(items, "Inspector");
        var typography = TakeContent(items, "Typography");
        var comments = TakeContent(items, "Comments");
        var pdf = TakeContent(items, "PDF");
        var outline = TakeContent(items, "Outline");
        var snapshots = TakeContent(items, "Snapshots");
        var project = TakeContent(items, "Project");

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Inspector", "Typography", "Comments", "PDF", "Outline", "Snapshots", "Project"
        };
        var extras = items
            .Where(item => !claimed.Contains(HeaderText(item)))
            .Select(item => (Header: HeaderText(item), Content: DetachContent(item)))
            .Where(entry => entry.Content is not null)
            .Select(entry => (entry.Header, entry.Content!))
            .ToArray();

        inspector.ItemsSource = new object[]
        {
            new TabItem { Header = "Text", Content = BuildTextInspector(typography, document) },
            new TabItem { Header = "Layout", Content = BuildLayoutInspector() },
            new TabItem { Header = "Image", Content = BuildImageInspector() },
            new TabItem { Header = "Section", Content = BuildSectionInspector(outline, comments, snapshots, project, pdf, extras) }
        };
        inspector.SelectedIndex = 0;
        inspector.Classes.Add("unified-inspector-tabs");
    }

    private static Control BuildTextInspector(Control? typography, Control? document)
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };

        var primary = typography ?? new TextBlock
        {
            Text = "Typography controls become available when a manuscript document is selected.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12),
            Opacity = .65
        };
        root.Children.Add(primary);

        if (document is not null)
        {
            var expander = new Expander
            {
                Header = "Document metadata",
                IsExpanded = false,
                Content = document,
                Margin = new Thickness(6, 4, 6, 6)
            };
            Grid.SetRow(expander, 1);
            root.Children.Add(expander);
        }

        return root;
    }

    private Control BuildLayoutInspector()
    {
        var style = _viewModel.CurrentStyle;

        var preset = new ComboBox
        {
            ItemsSource = new[] { "6 × 9 in", "A5", "A4", "Letter", "Custom" },
            SelectedIndex = ClosestPreset(style.PageWidthInches, style.PageHeightInches)
        };
        var width = Numeric(style.PageWidthInches);
        var height = Numeric(style.PageHeightInches);
        var top = Numeric(style.MarginTopInches);
        var bottom = Numeric(style.MarginBottomInches);
        var inner = Numeric(style.MarginInnerInches);
        var outer = Numeric(style.MarginOuterInches);
        var openRight = new CheckBox { Content = "Start chapters on right pages", IsChecked = style.OpenChaptersOnRight };
        var headers = new CheckBox { Content = "Show headers and footers", IsChecked = style.ShowHeadersAndFooters };
        var pageNumbers = new CheckBox { Content = "Show page numbers", IsChecked = style.ShowPageNumbers };
        var justify = new CheckBox { Content = "Justify body text", IsChecked = style.JustifyBody };
        var widows = new CheckBox { Content = "Avoid widows and orphans", IsChecked = style.AvoidWidowsAndOrphans };
        var status = new TextBlock
        {
            Text = "Changes use the same book style as PDF publishing.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10.5,
            Opacity = .62
        };

        preset.SelectionChanged += (_, _) =>
        {
            switch (preset.SelectedIndex)
            {
                case 0: SetSize(6, 9); break;
                case 1: SetSize(5.83, 8.27); break;
                case 2: SetSize(8.27, 11.69); break;
                case 3: SetSize(8.5, 11); break;
            }
        };

        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
        panel.Children.Add(SectionTitle("PAGE"));
        panel.Children.Add(Field("Preset", preset));

        var sizeGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 7 };
        sizeGrid.Children.Add(Field("Width (in)", width));
        var heightField = Field("Height (in)", height);
        Grid.SetColumn(heightField, 1);
        sizeGrid.Children.Add(heightField);
        panel.Children.Add(sizeGrid);

        panel.Children.Add(SectionTitle("MARGINS"));
        var firstMargins = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 7 };
        firstMargins.Children.Add(Field("Top", top));
        var bottomField = Field("Bottom", bottom);
        Grid.SetColumn(bottomField, 1);
        firstMargins.Children.Add(bottomField);
        panel.Children.Add(firstMargins);

        var secondMargins = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 7 };
        secondMargins.Children.Add(Field("Inner", inner));
        var outerField = Field("Outer", outer);
        Grid.SetColumn(outerField, 1);
        secondMargins.Children.Add(outerField);
        panel.Children.Add(secondMargins);

        panel.Children.Add(SectionTitle("FLOW"));
        panel.Children.Add(openRight);
        panel.Children.Add(headers);
        panel.Children.Add(pageNumbers);
        panel.Children.Add(justify);
        panel.Children.Add(widows);

        var apply = new Button
        {
            Content = "Apply layout",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 8, 0, 0)
        };
        apply.Classes.Add("primary");
        apply.Click += async (_, _) =>
        {
            if (!_viewModel.HasProject)
            {
                status.Text = "Open a project before changing page layout.";
                return;
            }

            if (!TryNumber(width.Text, out var pageWidth) ||
                !TryNumber(height.Text, out var pageHeight) ||
                !TryNumber(top.Text, out var marginTop) ||
                !TryNumber(bottom.Text, out var marginBottom) ||
                !TryNumber(inner.Text, out var marginInner) ||
                !TryNumber(outer.Text, out var marginOuter))
            {
                status.Text = "Enter valid numbers for page size and margins.";
                return;
            }

            try
            {
                var updated = (_viewModel.CurrentStyle with
                {
                    PageWidthInches = pageWidth,
                    PageHeightInches = pageHeight,
                    MarginTopInches = marginTop,
                    MarginBottomInches = marginBottom,
                    MarginInnerInches = marginInner,
                    MarginOuterInches = marginOuter,
                    OpenChaptersOnRight = openRight.IsChecked == true,
                    ShowHeadersAndFooters = headers.IsChecked == true,
                    ShowPageNumbers = pageNumbers.IsChecked == true,
                    JustifyBody = justify.IsChecked == true,
                    AvoidWidowsAndOrphans = widows.IsChecked == true
                }).Validate();

                await _viewModel.UpdateStyleAsync(updated);
                status.Text = "Layout applied to the live document and publishing pipeline.";
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
        };
        panel.Children.Add(apply);
        panel.Children.Add(status);

        return new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        void SetSize(double pageWidth, double pageHeight)
        {
            width.Text = pageWidth.ToString("0.##", CultureInfo.InvariantCulture);
            height.Text = pageHeight.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }

    private Control BuildImageInspector()
    {
        var caption = new TextBox { Watermark = "Figure caption" };
        var source = new TextBox { Watermark = "assets/image.png" };
        var status = new TextBlock
        {
            Text = "Images stay ordinary Markdown figures and flow with the manuscript.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10.5,
            Opacity = .62
        };

        var insert = new Button
        {
            Content = "Insert figure at caret",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        insert.Classes.Add("primary");
        insert.Click += (_, _) =>
        {
            var editor = _window.GetVisualDescendants()
                .OfType<ManuscriptEditor>()
                .FirstOrDefault(candidate => candidate.DocumentIdentity is not null)
                ?? _window.GetVisualDescendants().OfType<ManuscriptEditor>().FirstOrDefault();

            var path = source.Text?.Trim() ?? string.Empty;
            if (editor is null || !_viewModel.HasDocument)
            {
                status.Text = "Select a manuscript document first.";
                return;
            }
            if (path.Length == 0)
            {
                status.Text = "Enter a project-relative image path.";
                return;
            }

            var label = (caption.Text ?? string.Empty)
                .Replace("]", "\\]", StringComparison.Ordinal)
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
            var safePath = path.Replace(")", "\\)", StringComparison.Ordinal);
            var figure = $"![{label}]({safePath})";
            var caret = Math.Clamp(editor.CaretOffset, 0, editor.Document.TextLength);
            var before = caret > 0 && editor.Document.GetCharAt(caret - 1) != '\n' ? Environment.NewLine : string.Empty;
            var after = caret < editor.Document.TextLength && editor.Document.GetCharAt(caret) != '\n' ? Environment.NewLine : string.Empty;
            var insertion = before + figure + after;

            editor.Document.Insert(caret, insertion);
            editor.CaretOffset = caret + insertion.Length;
            editor.Focus();
            status.Text = "Figure inserted. Use the layout controls for wrapping and placement.";
        };

        var panel = new StackPanel { Spacing = 9, Margin = new Thickness(12) };
        panel.Children.Add(SectionTitle("FIGURE"));
        panel.Children.Add(Field("Caption", caption));
        panel.Children.Add(Field("Project image path", source));
        panel.Children.Add(insert);
        panel.Children.Add(status);
        panel.Children.Add(new Separator { Margin = new Thickness(0, 8) });
        panel.Children.Add(SectionTitle("LAYOUT"));
        panel.Children.Add(new TextBlock
        {
            Text = "Select an existing figure in the page-layout tools for advanced anchoring, wrapping, size and print controls.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = .68
        });

        return new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private static Control BuildSectionInspector(
        Control? outline,
        Control? comments,
        Control? snapshots,
        Control? project,
        Control? pdf,
        IReadOnlyList<(string Header, Control Content)> extras)
    {
        var panel = new StackPanel { Spacing = 5, Margin = new Thickness(8) };
        panel.Children.Add(SectionTitle("DOCUMENT TOOLS"));

        Add("Outline", outline, true);
        Add("Comments", comments, false);
        Add("Snapshots", snapshots, false);
        Add("Project targets", project, false);
        Add("PDF proof", pdf, false);
        foreach (var extra in extras)
            Add(string.IsNullOrWhiteSpace(extra.Header) ? "More" : extra.Header, extra.Content, false);

        return new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        void Add(string header, Control? content, bool expanded)
        {
            if (content is null) return;
            panel.Children.Add(new Expander
            {
                Header = header,
                IsExpanded = expanded,
                Content = content
            });
        }
    }

    private static Control Field(string label, Control control)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 3 };
        grid.Children.Add(new TextBlock
        {
            Text = label,
            Classes = { "field-label" }
        });
        Grid.SetRow(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private static TextBlock SectionTitle(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Opacity = .64,
            Margin = new Thickness(0, 7, 0, 1)
        };
        block.Classes.Add("inspector-section-title");
        return block;
    }

    private static TextBox Numeric(double value)
        => new() { Text = value.ToString("0.##", CultureInfo.InvariantCulture) };

    private static bool TryNumber(string? text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
           double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    private static int ClosestPreset(double width, double height)
    {
        if (Near(width, 6) && Near(height, 9)) return 0;
        if (Near(width, 5.83) && Near(height, 8.27)) return 1;
        if (Near(width, 8.27) && Near(height, 11.69)) return 2;
        if (Near(width, 8.5) && Near(height, 11)) return 3;
        return 4;

        static bool Near(double left, double right) => Math.Abs(left - right) < .08;
    }

    private static bool HasHeaders(TabControl tabs, params string[] headers)
        => headers.All(header => HasHeader(tabs, header));

    private static bool HasHeader(TabControl tabs, string header)
        => TabItems(tabs).Any(item => string.Equals(HeaderText(item), header, StringComparison.OrdinalIgnoreCase));

    private static List<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is IEnumerable source)
            return source.Cast<object?>().OfType<TabItem>().ToList();
        return tabs.Items.OfType<TabItem>().ToList();
    }

    private static string HeaderText(TabItem item)
        => item.Header?.ToString()?.Replace("_", string.Empty, StringComparison.Ordinal).Trim() ?? string.Empty;

    private static Control? TakeContent(IEnumerable<TabItem> items, string header)
    {
        var item = items.FirstOrDefault(candidate =>
            string.Equals(HeaderText(candidate), header, StringComparison.OrdinalIgnoreCase));
        return item is null ? null : DetachContent(item);
    }

    private static Control? DetachContent(TabItem item)
    {
        var content = item.Content as Control;
        item.Content = null;
        return content;
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        if (_centerTabs is not null)
            _centerTabs.SelectionChanged -= CenterSelectionChanged;
        _window.Opened -= WindowOpened;
        _window.LayoutUpdated -= WindowLayoutUpdated;
        _window.Closed -= WindowClosed;
    }
}
