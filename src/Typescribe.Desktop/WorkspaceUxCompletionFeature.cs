using System.Collections;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.Editing;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

/// <summary>
/// Finishes the studio surface without introducing another ownership layer for project data.
/// It improves navigation/preview discoverability, turns the top utility buttons into real
/// appearance/settings controls, and polishes the existing bookmark/comment/snapshot/target flows.
/// </summary>
internal sealed class WorkspaceUxCompletionFeature
{
    private static readonly PaletteChoice[] LabelPalette =
    [
        new("Automatic", null),
        new("Slate", "#64748B"),
        new("Blue", "#3B82F6"),
        new("Violet", "#8B5CF6"),
        new("Rose", "#F43F5E"),
        new("Amber", "#F59E0B"),
        new("Emerald", "#10B981"),
        new("Cyan", "#06B6D4"),
        new("Pink", "#EC4899")
    ];

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly TextBlock _centerPreviewTitle = new()
    {
        FontSize = 17,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(18, 12, 18, 4)
    };
    private readonly TextBlock _centerPreviewMeta = new()
    {
        Opacity = 0.62,
        Margin = new Thickness(18, 0, 18, 8)
    };
    private readonly TextBox _centerPreviewText = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(18, 12, 18, 24),
        FontSize = 14.5,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch
    };
    private readonly TextBox _outlinerLabelBox = new()
    {
        Watermark = "Label (POV, storyline, arc…)",
        MinWidth = 190,
        Height = 30,
        Padding = new Thickness(8, 3),
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly ComboBox _outlinerLabelColor = new()
    {
        ItemsSource = LabelPalette,
        SelectedIndex = 0,
        MinWidth = 118,
        Height = 30
    };
    private readonly Border _outlinerLabelSwatch = new()
    {
        Width = 14,
        Height = 14,
        CornerRadius = new CornerRadius(7),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.Parse("#808080")),
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Button _outlinerApplyLabel = new()
    {
        Content = "Apply Label",
        MinWidth = 92,
        Height = 30,
        Padding = new Thickness(9, 3)
    };
    private readonly DispatcherTimer _dialogPolishTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private readonly Dictionary<string, string> _labelColors = new(StringComparer.OrdinalIgnoreCase);

    private TabControl? _centerTabs;
    private TabItem? _centerPreviewTab;
    private TabItem? _outlinerTab;
    private Button? _themeButton;
    private UiSettings _settings = new();
    private string? _loadedLabelProject;
    private bool _topChromeInstalled;
    private bool _centerPreviewInstalled;
    private bool _inspectorArranged;
    private bool _leftTabsCleaned;
    private bool _bookmarksPolished;
    private bool _commentsPolished;
    private bool _snapshotsPolished;
    private bool _projectTargetsPolished;
    private bool _outlinerPaletteInstalled;
    private bool _opened;
    private bool _installQueued;
    private bool _syncingLabelControls;
    private bool _disposed;

    private WorkspaceUxCompletionFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
        _settings = LoadSettings();
        _dialogPolishTimer.Tick += (_, _) => PolishOpenCardEditors();
        _outlinerLabelColor.SelectionChanged += (_, _) => UpdateLabelSwatch();
        _outlinerApplyLabel.Click += async (_, _) => await ApplySelectedLabelAsync();
        _outlinerLabelBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter) return;
            e.Handled = true;
            await ApplySelectedLabelAsync();
        };
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var feature = new WorkspaceUxCompletionFeature(window, viewModel);
        window.Opened += feature.WindowOpened;
        window.LayoutUpdated += feature.WindowLayoutUpdated;
        window.Closed += feature.WindowClosed;
        viewModel.StateChanged += feature.ViewModelStateChanged;
    }

    private void WindowOpened(object? sender, EventArgs e)
    {
        _opened = true;
        ApplySettings();
        _dialogPolishTimer.Start();
        QueueInstall();
    }

    private void WindowLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_opened || _disposed) return;
        QueueInstall();
        StyleOutlinerLabelCells();
    }

    private void ViewModelStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            UpdateCenterPreview();
            EnsureLabelColorProject();
            UpdateOutlinerLabelControls();
            StyleOutlinerLabelCells();
            ApplyEditorSettings();
        }, DispatcherPriority.Background);
    }

    private void QueueInstall()
    {
        if (_disposed || _installQueued) return;
        _installQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _installQueued = false;
            if (!_disposed) InstallAvailableSurfaces();
        }, DispatcherPriority.Background);
    }

    private void InstallAvailableSurfaces()
    {
        InstallTopChromeUtilities();
        InstallCenterPreview();
        RemoveLegacyProjectTabs();
        ArrangeInspectorTabs();
        PolishBookmarks();
        PolishComments();
        PolishSnapshots();
        PolishProjectTargets();
        InstallOutlinerLabelPalette();
        EnsureLabelColorProject();
        UpdateCenterPreview();
        UpdateOutlinerLabelControls();
        StyleOutlinerLabelCells();
        ApplyEditorSettings();
        PolishOpenCardEditors();
    }

    private void InstallTopChromeUtilities()
    {
        if (_topChromeInstalled) return;
        var chrome = _window.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(static grid => string.Equals(grid.Name, "ReferenceTopChrome", StringComparison.Ordinal));
        if (chrome is null) return;

        var search = chrome.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(static box => string.Equals(box.Name, "ProjectSearchBox", StringComparison.Ordinal));
        if (search is not null && search.Parent is Grid parent &&
            !string.Equals(parent.Name, "ProjectSearchField", StringComparison.Ordinal))
        {
            var field = new Grid
            {
                Name = "ProjectSearchField",
                Width = 380,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = search.Margin
            };
            var row = Grid.GetRow(search);
            var column = Grid.GetColumn(search);
            parent.Children.Remove(search);
            search.Margin = new Thickness(0);
            search.Width = double.NaN;
            search.HorizontalAlignment = HorizontalAlignment.Stretch;
            search.Padding = new Thickness(30, 3, 10, 3);
            field.Children.Add(search);
            field.Children.Add(new TextBlock
            {
                Text = "🔍",
                FontSize = 11,
                Opacity = 0.72,
                Margin = new Thickness(9, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            });
            Grid.SetRow(field, row);
            Grid.SetColumn(field, column);
            parent.Children.Add(field);
        }

        var gear = chrome.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(static button => string.Equals(ButtonGlyph(button), "⚙", StringComparison.Ordinal));
        if (gear?.Parent is StackPanel utilities)
        {
            utilities.Children.Clear();
            _themeButton = TopUtilityButton(CurrentThemeGlyph(), "Toggle light / dark theme");
            var settings = TopUtilityButton("⚙", "Settings");
            _themeButton.Click += (_, _) => ToggleTheme();
            settings.Click += async (_, _) => await ShowSettingsAsync();
            utilities.Children.Add(_themeButton);
            utilities.Children.Add(settings);
            _topChromeInstalled = true;
            ApplyTheme();
        }
    }

    private static Button TopUtilityButton(string glyph, string toolTip)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Width = 32,
            Height = 26,
            MinWidth = 32,
            MinHeight = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(2),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, toolTip);
        return button;
    }

    private void ToggleTheme()
    {
        _settings.Theme = string.Equals(_settings.Theme, "Dark", StringComparison.OrdinalIgnoreCase)
            ? "Light"
            : "Dark";
        SaveSettings();
        ApplyTheme();
    }

    private string CurrentThemeGlyph()
        => string.Equals(_settings.Theme, "Light", StringComparison.OrdinalIgnoreCase) ? "☀" : "☾";

    private void ApplySettings()
    {
        ApplyTheme();
        ApplyEditorSettings();
    }

    private void ApplyTheme()
    {
        if (Application.Current is not { } app) return;
        var theme = _settings.Theme;
        app.RequestedThemeVariant = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase)
            ? ThemeVariant.Light
            : string.Equals(theme, "System", StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Default
                : ThemeVariant.Dark;

        var light = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);
        app.Resources["TsAppBackgroundBrush"] = Brush(light ? "#F3F3F3" : "#1E1E1E");
        app.Resources["TsSurfaceBrush"] = Brush(light ? "#FFFFFF" : "#252526");
        app.Resources["TsSurfaceMutedBrush"] = Brush(light ? "#F0F0F0" : "#2D2D30");
        app.Resources["TsSurfaceHoverBrush"] = Brush(light ? "#E8E8E8" : "#2A2D2E");
        app.Resources["TsEditorBrush"] = Brush(light ? "#FFFFFF" : "#1E1E1E");
        app.Resources["TsBorderBrush"] = Brush(light ? "#C8C8C8" : "#3F3F46");
        app.Resources["TsTextBrush"] = Brush(light ? "#1F1F1F" : "#CCCCCC");
        app.Resources["TsMutedTextBrush"] = Brush(light ? "#666666" : "#969696");
        app.Resources["TsAccentBrush"] = Brush("#007ACC");
        app.Resources["TsAccentHoverBrush"] = Brush("#1C97EA");
        app.Resources["TsAccentSoftBrush"] = Brush(light ? "#DDEEFF" : "#094771");
        app.Resources["TsDangerBrush"] = Brush(light ? "#C42B1C" : "#F14C4C");

        _window.Background = Brush(light ? "#FFFFFF" : "#1E1E1E");
        var chrome = _window.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(static grid => string.Equals(grid.Name, "ReferenceTopChrome", StringComparison.Ordinal));
        if (chrome is not null)
        {
            var chromeBackground = Brush(light ? "#F3F3F3" : "#181818");
            var chromeText = Brush(light ? "#202020" : "#CCCCCC");
            chrome.Background = chromeBackground;
            foreach (var grid in chrome.GetVisualDescendants().OfType<Grid>())
            {
                if (grid.Background is not null && grid.Background != Brushes.Transparent)
                    grid.Background = chromeBackground;
            }
            foreach (var border in chrome.GetVisualDescendants().OfType<Border>())
            {
                if (border.Child is Menu or Grid) border.Background = chromeBackground;
            }
            foreach (var text in chrome.GetVisualDescendants().OfType<TextBlock>())
                text.Foreground = chromeText;
            foreach (var menu in chrome.GetVisualDescendants().OfType<Menu>())
            {
                menu.Background = chromeBackground;
                menu.Foreground = chromeText;
            }
            var search = chrome.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(static box => string.Equals(box.Name, "ProjectSearchBox", StringComparison.Ordinal));
            if (search is not null)
            {
                search.Background = Brush(light ? "#FFFFFF" : "#1E1E1E");
                search.Foreground = chromeText;
                search.BorderBrush = Brush(light ? "#B8B8B8" : "#3F3F46");
            }
        }

        if (_themeButton?.Content is TextBlock glyph) glyph.Text = CurrentThemeGlyph();
    }

    private void ApplyEditorSettings()
    {
        foreach (var editor in _window.GetVisualDescendants().OfType<ManuscriptEditor>())
            editor.ShowLineNumbers = _settings.ShowLineNumbers;
    }

    private async Task ShowSettingsAsync()
    {
        var theme = new ComboBox
        {
            ItemsSource = new[] { "Dark", "Light", "System" },
            SelectedItem = NormalizeTheme(_settings.Theme),
            MinWidth = 190,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var lineNumbers = new CheckBox
        {
            Content = "Show manuscript line numbers",
            IsChecked = _settings.ShowLineNumbers
        };
        var save = new Button { Content = "Save Settings", MinWidth = 108 };
        var cancel = new Button { Content = "Cancel", MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { save, cancel }
        };
        var content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Typescribe Settings", FontSize = 20, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "Appearance and editor preferences are saved for future sessions.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.68,
                    Margin = new Thickness(0, 0, 0, 8)
                },
                Field("Theme", theme),
                lineNumbers,
                new Separator { Margin = new Thickness(0, 8) },
                actions
            }
        };
        var dialog = new Window
        {
            Title = "Settings — Typescribe",
            Width = 470,
            Height = 300,
            MinWidth = 420,
            MinHeight = 270,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content
        };
        save.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        if (!await dialog.ShowDialog<bool>(_window)) return;

        _settings.Theme = theme.SelectedItem?.ToString() ?? "Dark";
        _settings.ShowLineNumbers = lineNumbers.IsChecked == true;
        SaveSettings();
        ApplySettings();
    }

    private void InstallCenterPreview()
    {
        if (_centerPreviewInstalled) return;
        foreach (var tabs in _window.GetVisualDescendants().OfType<TabControl>())
        {
            var items = TabItems(tabs);
            if (!items.Any(static item => HeaderEquals(item, "Editor")) ||
                !items.Any(static item => HeaderEquals(item, "Corkboard")) ||
                !items.Any(static item => HeaderEquals(item, "Outliner")))
                continue;

            _centerTabs = tabs;
            _outlinerTab = items.FirstOrDefault(static item => HeaderEquals(item, "Outliner"));
            var existing = items.FirstOrDefault(static item => HeaderEquals(item, "Preview"));
            if (existing is not null)
            {
                _centerPreviewTab = existing;
                _centerPreviewTab.Content = BuildCenterPreviewSurface();
                _centerPreviewInstalled = true;
                UpdateCenterPreview();
                return;
            }

            var editorIndex = items.FindIndex(static item => HeaderEquals(item, "Editor"));
            _centerPreviewTab = new TabItem { Header = "Preview", Content = BuildCenterPreviewSurface() };
            items.Insert(Math.Clamp(editorIndex + 1, 0, items.Count), _centerPreviewTab);
            tabs.ItemsSource = items.ToArray();
            _centerPreviewInstalled = true;
            UpdateCenterPreview();
            return;
        }
    }

    private Control BuildCenterPreviewSurface()
    {
        var heading = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        heading.Children.Add(_centerPreviewTitle);
        Grid.SetRow(_centerPreviewMeta, 1);
        heading.Children.Add(_centerPreviewMeta);

        var scroll = new ScrollViewer
        {
            Content = _centerPreviewText,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(heading);
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        return root;
    }

    private void UpdateCenterPreview()
    {
        if (!_centerPreviewInstalled) return;
        if (_viewModel.HasDocument)
        {
            _centerPreviewTitle.Text = _viewModel.SelectedTitle;
            _centerPreviewMeta.Text = $"Formatted manuscript preview · {_viewModel.WordCount:N0} words";
            _centerPreviewText.Text = string.IsNullOrWhiteSpace(_viewModel.PreviewText)
                ? "Nothing to preview yet."
                : _viewModel.PreviewText;
        }
        else
        {
            _centerPreviewTitle.Text = "Preview";
            _centerPreviewMeta.Text = "Select a manuscript document to preview it.";
            _centerPreviewText.Text = string.Empty;
        }
    }

    private void RemoveLegacyProjectTabs()
    {
        if (_leftTabsCleaned) return;
        var tabs = _window.GetVisualDescendants().OfType<TabControl>()
            .FirstOrDefault(control => TabItems(control).Any(static item =>
                HeaderEquals(item, "Binder") || HeaderEquals(item, "Project")) &&
                TabItems(control).Any(static item => HeaderEquals(item, "Bookmarks")));
        if (tabs is null) return;

        var items = TabItems(tabs);
        var filtered = items.Where(static item =>
            !HeaderEquals(item, "Search") &&
            !HeaderEquals(item, "Collections") &&
            !HeaderEquals(item, "Favorites")).ToArray();
        if (filtered.Length != items.Count) tabs.ItemsSource = filtered;
        if (tabs.SelectedIndex < 0 && filtered.Length > 0) tabs.SelectedIndex = 0;
        _leftTabsCleaned = true;
    }

    private void ArrangeInspectorTabs()
    {
        if (_inspectorArranged) return;
        foreach (var tabs in _window.GetVisualDescendants().OfType<TabControl>())
        {
            var items = TabItems(tabs);
            if (!items.Any(static item => HeaderEquals(item, "Comments")) ||
                !items.Any(static item => HeaderEquals(item, "Snapshots")) ||
                !items.Any(static item => HeaderEquals(item, "Project")))
                continue;
            if (items.Any(static item => HeaderEquals(item, "Editor"))) continue;

            var preview = items.FirstOrDefault(static item => HeaderEquals(item, "PDF") || HeaderEquals(item, "Preview"));
            if (preview is null) continue;
            preview.Header = "Preview";

            var ordered = new List<TabItem> { preview };
            AddIfPresent(ordered, items, "Comments");
            AddIfPresent(ordered, items, "Outline");
            AddIfPresent(ordered, items, "Snapshots");
            AddIfPresent(ordered, items, "Project");
            foreach (var item in items)
            {
                if (HeaderEquals(item, "Inspector")) continue;
                if (!ordered.Contains(item)) ordered.Add(item);
            }
            var oldSelection = tabs.SelectedItem as TabItem;
            tabs.ItemsSource = ordered.ToArray();
            tabs.SelectedItem = oldSelection is not null && ordered.Contains(oldSelection) ? oldSelection : preview;
            _inspectorArranged = true;
            return;
        }
    }

    private static void AddIfPresent(ICollection<TabItem> output, IEnumerable<TabItem> items, string header)
    {
        var item = items.FirstOrDefault(candidate => HeaderEquals(candidate, header));
        if (item is not null && !output.Contains(item)) output.Add(item);
    }

    private void PolishBookmarks()
    {
        if (_bookmarksPolished) return;
        var tab = FindTab("Bookmarks", leftSide: true);
        if (tab?.Content is not Control oldContent) return;
        WrapSection(tab, oldContent, "ux-bookmarks", "Bookmarks", "Save important manuscript positions. Add uses the current editor caret; double-click a bookmark to jump back.");
        foreach (var button in EnumerateControls(oldContent).OfType<Button>())
        {
            var text = ButtonText(button);
            if (string.Equals(text, "Add Bookmark", StringComparison.Ordinal))
            {
                button.Content = "+ Bookmark";
                button.MinWidth = 100;
                ToolTip.SetTip(button, "Add a labeled bookmark at the current caret");
            }
            else if (string.Equals(text, "Remove", StringComparison.Ordinal))
            {
                button.Content = "Remove";
                ToolTip.SetTip(button, "Remove the selected bookmark");
            }
        }
        foreach (var list in EnumerateControls(oldContent).OfType<ListBox>())
        {
            list.Margin = new Thickness(10, 4, 10, 10);
            ToolTip.SetTip(list, "Double-click a bookmark to navigate to it");
        }
        _bookmarksPolished = true;
    }

    private void PolishComments()
    {
        if (_commentsPolished) return;
        var tab = FindTab("Comments", leftSide: false);
        if (tab?.Content is not Control oldContent) return;
        WrapSection(tab, oldContent, "ux-comments", "Comments", "Attach notes to the current cursor location without changing manuscript text. Double-click an item to jump to its source line.");
        foreach (var button in EnumerateControls(oldContent).OfType<Button>())
        {
            var text = ButtonText(button);
            if (string.Equals(text, "Add at Caret", StringComparison.Ordinal)) button.Content = "+ Comment";
            else if (string.Equals(text, "Resolve / Reopen", StringComparison.Ordinal)) button.Content = "Toggle Resolved";
            else if (string.Equals(text, "Delete", StringComparison.Ordinal)) button.Content = "Delete";
            button.MinHeight = 29;
            button.Padding = new Thickness(9, 3);
        }
        foreach (var list in EnumerateControls(oldContent).OfType<ListBox>())
            list.Margin = new Thickness(8, 4, 8, 8);
        _commentsPolished = true;
    }

    private void PolishSnapshots()
    {
        if (_snapshotsPolished) return;
        var tab = FindTab("Snapshots", leftSide: false);
        if (tab?.Content is not Control oldContent) return;
        WrapSection(tab, oldContent, "ux-snapshots", "Snapshots", "Capture a named version before major edits. Compare, selectively restore changed paragraphs, or restore the complete snapshot.");
        foreach (var button in EnumerateControls(oldContent).OfType<Button>())
        {
            var text = ButtonText(button);
            if (string.Equals(text, "Take Snapshot", StringComparison.Ordinal)) button.Content = "+ Snapshot";
            button.MinHeight = 29;
            button.Padding = new Thickness(9, 3);
        }
        _snapshotsPolished = true;
    }

    private void PolishProjectTargets()
    {
        if (_projectTargetsPolished) return;
        var tab = FindTab("Project", leftSide: false);
        if (tab?.Content is not Control oldContent) return;
        WrapSection(tab, oldContent, "ux-project-targets", "Writing Targets", "Set project, daily, and session word targets. Project progress tracks the complete manuscript.");
        var inputs = EnumerateControls(oldContent).OfType<TextBox>().ToArray();
        for (var index = 0; index < Math.Min(3, inputs.Length); index++)
        {
            inputs[index].MinHeight = 32;
            inputs[index].Padding = new Thickness(8, 4);
            inputs[index].MinWidth = 140;
        }
        foreach (var button in EnumerateControls(oldContent).OfType<Button>())
        {
            if (string.Equals(ButtonText(button), "Save Writing Targets", StringComparison.Ordinal))
                button.Content = "Save Targets";
        }
        _projectTargetsPolished = true;
    }

    private void InstallOutlinerLabelPalette()
    {
        if (_outlinerPaletteInstalled) return;
        _outlinerTab ??= FindCenterTab("Outliner");
        if (_outlinerTab?.Content is not Control oldContent) return;

        var label = new TextBlock
        {
            Text = "Label",
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0)
        };
        var color = new TextBlock
        {
            Text = "Color",
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.72,
            Margin = new Thickness(9, 0, 3, 0)
        };
        _outlinerLabelBox.Margin = new Thickness(3, 0);
        _outlinerLabelSwatch.Margin = new Thickness(3, 0);
        _outlinerLabelColor.Margin = new Thickness(3, 0);
        _outlinerApplyLabel.Margin = new Thickness(3, 0);
        var bar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(10, 6, 10, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                label, _outlinerLabelBox, color, _outlinerLabelSwatch,
                _outlinerLabelColor, _outlinerApplyLabel
            }
        };
        ToolTip.SetTip(_outlinerLabelColor, "Choose the shared color used for this label in the Outliner");
        ToolTip.SetTip(_outlinerApplyLabel, "Apply the label and color to the selected manuscript item");

        _outlinerTab.Content = null;
        var wrapper = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        wrapper.Classes.Add("ux-outliner-labels");
        wrapper.Children.Add(bar);
        Grid.SetRow(oldContent, 1);
        wrapper.Children.Add(oldContent);
        _outlinerTab.Content = wrapper;
        _outlinerPaletteInstalled = true;
        EnsureLabelColorProject();
        UpdateOutlinerLabelControls();
        UpdateLabelSwatch();
    }

    private async Task ApplySelectedLabelAsync()
    {
        if (_syncingLabelControls) return;
        var node = _viewModel.SelectedRow?.Node;
        if (node?.IsDocument != true) return;
        var value = _outlinerLabelBox.Text?.Trim() ?? string.Empty;
        var choice = _outlinerLabelColor.SelectedItem as PaletteChoice ?? LabelPalette[0];

        await _viewModel.SaveSelectedMetadataAsync(
            node.Synopsis,
            node.Notes,
            node.Status,
            value,
            node.Keywords,
            node.TargetWords);

        EnsureLabelColorProject();
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (string.IsNullOrWhiteSpace(choice.Hex)) _labelColors.Remove(value);
            else _labelColors[value] = choice.Hex;
            SaveLabelColors();
        }
        UpdateOutlinerLabelControls();
        StyleOutlinerLabelCells();
    }

    private void UpdateOutlinerLabelControls()
    {
        if (!_outlinerPaletteInstalled) return;
        var node = _viewModel.SelectedRow?.Node;
        var enabled = node?.IsDocument == true;
        _syncingLabelControls = true;
        try
        {
            _outlinerLabelBox.IsEnabled = enabled;
            _outlinerLabelColor.IsEnabled = enabled;
            _outlinerApplyLabel.IsEnabled = enabled;
            _outlinerLabelBox.Text = enabled ? node!.Label : string.Empty;
            var selected = LabelPalette[0];
            if (enabled && !string.IsNullOrWhiteSpace(node!.Label) &&
                _labelColors.TryGetValue(node.Label, out var hex))
            {
                selected = LabelPalette.FirstOrDefault(choice =>
                    string.Equals(choice.Hex, hex, StringComparison.OrdinalIgnoreCase)) ?? LabelPalette[0];
            }
            _outlinerLabelColor.SelectedItem = selected;
            UpdateLabelSwatch();
        }
        finally { _syncingLabelControls = false; }
    }

    private void UpdateLabelSwatch()
    {
        var choice = _outlinerLabelColor.SelectedItem as PaletteChoice;
        _outlinerLabelSwatch.Background = string.IsNullOrWhiteSpace(choice?.Hex)
            ? Brushes.Transparent
            : Brush(choice!.Hex!);
    }

    private void StyleOutlinerLabelCells()
    {
        if (!_outlinerPaletteInstalled || _outlinerTab?.Content is not Control root) return;
        foreach (var grid in EnumerateControls(root).OfType<Grid>())
        {
            var label = grid.Children.OfType<TextBox>().FirstOrDefault(box => Grid.GetColumn(box) == 3);
            if (label is null) continue;
            var text = label.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text) || !_labelColors.TryGetValue(text, out var hex))
            {
                label.ClearValue(TextBox.BackgroundProperty);
                label.ClearValue(TextBox.BorderBrushProperty);
                label.ClearValue(TextBox.BorderThicknessProperty);
                continue;
            }
            var color = Color.Parse(hex);
            label.Background = new SolidColorBrush(Color.FromArgb(32, color.R, color.G, color.B));
            label.BorderBrush = new SolidColorBrush(color);
            label.BorderThickness = new Thickness(3, 1, 1, 1);
        }
    }

    private void EnsureLabelColorProject()
    {
        var project = CurrentProject();
        var root = project?.RootPath;
        if (string.Equals(root, _loadedLabelProject, StringComparison.Ordinal)) return;
        _loadedLabelProject = root;
        _labelColors.Clear();
        if (root is null) return;
        var path = LabelColorPath(root);
        if (!File.Exists(path)) return;
        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (data is null) return;
            foreach (var pair in data)
                if (!string.IsNullOrWhiteSpace(pair.Key) && IsHexColor(pair.Value))
                    _labelColors[pair.Key] = pair.Value;
        }
        catch
        {
            // Label color preferences are presentation-only and should never block authoring.
        }
    }

    private void SaveLabelColors()
    {
        var root = CurrentProject()?.RootPath;
        if (root is null) return;
        try
        {
            var path = LabelColorPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_labelColors, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    private static string LabelColorPath(string root)
        => Path.Combine(root, ".typescribe", "label-colors.json");

    private void PolishOpenCardEditors()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        foreach (var dialog in desktop.Windows)
        {
            if (dialog == _window || !dialog.IsVisible) continue;
            if (dialog.Title?.StartsWith("Edit Corkboard Card", StringComparison.Ordinal) != true) continue;
            if (dialog.Classes.Contains("typescribe-card-editor-polished")) continue;
            dialog.Classes.Add("typescribe-card-editor-polished");
            dialog.Width = double.IsNaN(dialog.Width) ? 620 : Math.Max(dialog.Width, 620);
            dialog.Height = double.IsNaN(dialog.Height) ? 650 : Math.Max(dialog.Height, 650);

            if (dialog.Content is not Control content) continue;
            var inputs = EnumerateControls(content).OfType<TextBox>().ToArray();
            foreach (var input in inputs)
            {
                input.HorizontalAlignment = HorizontalAlignment.Stretch;
                input.MinHeight = 34;
                input.Padding = new Thickness(9, 5);
                if (string.Equals(input.Watermark, "Synopsis", StringComparison.Ordinal))
                {
                    input.MinHeight = 150;
                    input.AcceptsReturn = true;
                    input.TextWrapping = TextWrapping.Wrap;
                }
                else if (string.Equals(input.Watermark, "Draft / Revised / Final", StringComparison.Ordinal))
                {
                    input.Watermark = "Status — Draft, Revised, Final…";
                }
                else if (string.Equals(input.Watermark, "Label / storyline / POV", StringComparison.Ordinal))
                {
                    input.Watermark = "Label — storyline, POV, character arc…";
                }
                else if (string.Equals(input.Watermark, "comma-separated keywords", StringComparison.Ordinal))
                {
                    input.Watermark = "Keywords — comma separated";
                }
                else if (string.Equals(input.Watermark, "Word target", StringComparison.Ordinal))
                {
                    input.Watermark = "Word target — 0 for none";
                    input.MaxWidth = 220;
                    input.HorizontalAlignment = HorizontalAlignment.Left;
                }
            }
            foreach (var stack in EnumerateControls(content).OfType<StackPanel>())
                if (stack.Children.OfType<TextBox>().Any()) stack.Spacing = Math.Max(8, stack.Spacing);
            foreach (var button in EnumerateControls(content).OfType<Button>())
            {
                if (string.Equals(ButtonText(button), "Save", StringComparison.Ordinal)) button.Content = "Save Card";
                button.MinHeight = 32;
                button.Padding = new Thickness(11, 4);
            }
        }
    }

    private TabItem? FindCenterTab(string header)
    {
        foreach (var tabs in _window.GetVisualDescendants().OfType<TabControl>())
        {
            var items = TabItems(tabs);
            if (!items.Any(static item => HeaderEquals(item, "Editor"))) continue;
            var match = items.FirstOrDefault(item => HeaderEquals(item, header));
            if (match is not null) return match;
        }
        return null;
    }

    private TabItem? FindTab(string header, bool leftSide)
    {
        foreach (var tabs in _window.GetVisualDescendants().OfType<TabControl>())
        {
            var items = TabItems(tabs);
            if (leftSide && !items.Any(static item => HeaderEquals(item, "Bookmarks"))) continue;
            if (!leftSide && items.Any(static item => HeaderEquals(item, "Editor"))) continue;
            var match = items.FirstOrDefault(item => HeaderEquals(item, header));
            if (match is not null) return match;
        }
        return null;
    }

    private static void WrapSection(TabItem tab, Control oldContent, string marker, string title, string description)
    {
        if (oldContent.Classes.Contains(marker)) return;
        var heading = new StackPanel
        {
            Margin = new Thickness(10, 10, 10, 5),
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Opacity = 0.64 }
            }
        };
        tab.Content = null;
        var wrapper = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        wrapper.Classes.Add(marker);
        wrapper.Children.Add(heading);
        Grid.SetRow(oldContent, 1);
        wrapper.Children.Add(oldContent);
        tab.Content = wrapper;
    }

    private BookProject? CurrentProject()
        => typeof(WorkspaceViewModel)
            .GetField("_project", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_viewModel) as BookProject;

    private UiSettings LoadSettings()
    {
        try
        {
            var path = SettingsPath();
            if (!File.Exists(path)) return new UiSettings();
            return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(path)) ?? new UiSettings();
        }
        catch
        {
            return new UiSettings();
        }
    }

    private void SaveSettings()
    {
        try
        {
            var path = SettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    private static string SettingsPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
        return Path.Combine(root, "Typescribe", "ui-settings.json");
    }

    private static string NormalizeTheme(string? theme)
        => theme is "Light" or "System" ? theme : "Dark";

    private static bool IsHexColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 7 || value[0] != '#') return false;
        return value.AsSpan(1).ToArray().All(static ch => Uri.IsHexDigit(ch));
    }

    private static Control Field(string label, Control input)
        => new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, Opacity = 0.78 },
                input
            }
        };

    private static string ButtonGlyph(Button button)
        => button.Content is TextBlock text ? text.Text ?? string.Empty : button.Content?.ToString() ?? string.Empty;

    private static string ButtonText(Button button)
        => button.Content is TextBlock text ? text.Text ?? string.Empty : button.Content?.ToString() ?? string.Empty;

    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));

    private static bool HeaderEquals(TabItem item, string header)
        => string.Equals(item.Header?.ToString(), header, StringComparison.OrdinalIgnoreCase);

    private static List<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is IEnumerable source)
            return source.Cast<object?>().OfType<TabItem>().ToList();
        return tabs.Items.Cast<object?>().OfType<TabItem>().ToList();
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
        if (root is Decorator { Child: Control child })
        {
            foreach (var descendant in EnumerateControls(child))
                yield return descendant;
        }
        if (root is ContentControl { Content: Control content })
        {
            foreach (var descendant in EnumerateControls(content))
                yield return descendant;
        }
        if (root is TabControl tabs)
        {
            foreach (var tab in TabItems(tabs))
                if (tab.Content is Control tabContent)
                    foreach (var descendant in EnumerateControls(tabContent))
                        yield return descendant;
        }
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _dialogPolishTimer.Stop();
        _viewModel.StateChanged -= ViewModelStateChanged;
        _window.Opened -= WindowOpened;
        _window.LayoutUpdated -= WindowLayoutUpdated;
        _window.Closed -= WindowClosed;
    }

    private sealed record PaletteChoice(string Name, string? Hex)
    {
        public override string ToString() => Name;
    }

    private sealed class UiSettings
    {
        public string Theme { get; set; } = "Dark";
        public bool ShowLineNumbers { get; set; }
    }
}
