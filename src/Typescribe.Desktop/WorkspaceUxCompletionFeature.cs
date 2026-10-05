using System.Collections;
using System.Reflection;
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
using AvaloniaApplication = Avalonia.Application;
using ToolTip = Avalonia.Controls.ToolTip;

namespace Typescribe.Desktop;

/// <summary>
/// Completes the author-facing workspace UI while keeping the existing project/view-model
/// services as the source of truth. This class only owns presentation and preference wiring.
/// </summary>
internal sealed class WorkspaceUxCompletionFeature
{
    internal static event Action<bool>? ManuscriptLineNumbersPreferenceChanged;

    internal static void PublishManuscriptLineNumbersPreference(bool enabled)
        => ManuscriptLineNumbersPreferenceChanged?.Invoke(enabled);

    private static readonly LabelColorChoice[] LabelColors =
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
    private readonly DispatcherTimer _cardDialogTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly Dictionary<string, string> _labelColorMap = new(StringComparer.OrdinalIgnoreCase);

    private readonly TextBlock _previewTitle = new()
    {
        FontSize = 17,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(18, 12, 18, 4)
    };
    private readonly TextBlock _previewMeta = new()
    {
        Opacity = 0.64,
        Margin = new Thickness(18, 0, 18, 8)
    };
    private readonly Avalonia.Controls.TextBox _previewText = new()
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

    private readonly Avalonia.Controls.TextBox _labelBox = new()
    {
        PlaceholderText = "Label (POV, storyline, arc…)",
        MinWidth = 190,
        Height = 30,
        Padding = new Thickness(8, 3),
        VerticalContentAlignment = VerticalAlignment.Center,
        Margin = new Thickness(3, 0)
    };
    private readonly ComboBox _labelColor = new()
    {
        ItemsSource = LabelColors,
        SelectedIndex = 0,
        MinWidth = 118,
        Height = 30,
        Margin = new Thickness(3, 0)
    };
    private readonly Border _labelSwatch = new()
    {
        Width = 14,
        Height = 14,
        CornerRadius = new CornerRadius(7),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.Parse("#808080")),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(3, 0)
    };
    private readonly Button _applyLabel = new()
    {
        Content = "Apply Label",
        MinWidth = 92,
        Height = 30,
        Padding = new Thickness(9, 3),
        Margin = new Thickness(3, 0)
    };

    private TabItem? _outlinerTab;
    private Button? _themeButton;
    private string _theme = "Dark";
    private bool _showLineNumbers;
    private string? _labelProjectRoot;
    private bool _searchPolished;
    private bool _utilitiesInstalled;
    private bool _centerPreviewInstalled;
    private bool _leftCleaned;
    private bool _inspectorArranged;
    private bool _bookmarksPolished;
    private bool _commentsPolished;
    private bool _snapshotsPolished;
    private bool _targetsPolished;
    private bool _outlinerLabelsInstalled;
    private bool _installQueued;
    private bool _opened;
    private bool _syncingLabelUi;
    private bool _disposed;

    private WorkspaceUxCompletionFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
        LoadSettings();
        ManuscriptLineNumbersPreferenceChanged += ManuscriptLineNumbersPreferenceChangedHandler;
        _cardDialogTimer.Tick += (_, _) => PolishCardDialogs();
        _labelColor.SelectionChanged += (_, _) => UpdateLabelSwatch();
        _applyLabel.Click += async (_, _) => await ApplySelectedLabelAsync();
        _labelBox.KeyDown += async (_, e) =>
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
        ApplyPreferences();
        _cardDialogTimer.Start();
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
            UpdateLabelControls();
            StyleOutlinerLabelCells();
            ApplyEditorPreferences();
        }, DispatcherPriority.Background);
    }

    private void QueueInstall()
    {
        if (_disposed || _installQueued) return;
        _installQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _installQueued = false;
            if (!_disposed) InstallAvailableUi();
        }, DispatcherPriority.Background);
    }

    private void InstallAvailableUi()
    {
        InstallSearchIcon();
        InstallTopUtilities();
        InstallCenterPreview();
        RemoveLegacyLeftTabs();
        ArrangeInspectorTabs();
        PolishBookmarks();
        PolishComments();
        PolishSnapshots();
        PolishProjectTargets();
        InstallOutlinerLabelControls();
        EnsureLabelColorProject();
        UpdateCenterPreview();
        UpdateLabelControls();
        ApplyPreferences();
        StyleOutlinerLabelCells();
        PolishCardDialogs();
    }

    private void InstallSearchIcon()
    {
        if (_searchPolished) return;
        var search = _window.GetVisualDescendants()
            .OfType<Avalonia.Controls.TextBox>()
            .FirstOrDefault(static box => string.Equals(box.Name, "ProjectSearchBox", StringComparison.Ordinal));
        if (search?.Parent is not Grid parent) return;

        search.Padding = new Thickness(30, 3, 10, 3);
        if (!parent.Children.OfType<TextBlock>().Any(static text => text.Classes.Contains("project-search-icon")))
        {
            var icon = new TextBlock
            {
                Text = "⌕",
                FontSize = 16,
                Opacity = 0.72,
                Margin = new Thickness(9, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            icon.Classes.Add("project-search-icon");
            parent.Children.Add(icon);
        }
        _searchPolished = true;
    }

    private void InstallTopUtilities()
    {
        if (_utilitiesInstalled) return;
        var chrome = FindTopChrome();
        if (chrome is null) return;
        var gear = chrome.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(static button => string.Equals(ButtonText(button), "⚙", StringComparison.Ordinal));
        if (gear?.Parent is not StackPanel utilities) return;

        utilities.Children.Clear();
        _themeButton = UtilityButton(ThemeGlyph(), "Toggle light / dark theme");
        var settings = UtilityButton("⚙", "Settings");
        _themeButton.Click += (_, _) => ToggleTheme();
        settings.Click += async (_, _) => await ShowSettingsAsync();
        utilities.Children.Add(_themeButton);
        utilities.Children.Add(settings);
        _utilitiesInstalled = true;
        ApplyTheme();
    }

    private static Button UtilityButton(string glyph, string toolTip)
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
        _theme = string.Equals(_theme, "Dark", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        SaveSettings();
        ApplyTheme();
    }

    private string ThemeGlyph()
        => string.Equals(_theme, "Light", StringComparison.OrdinalIgnoreCase) ? "☀" : "☾";

    private void ApplyPreferences()
    {
        ApplyTheme();
        ApplyEditorPreferences();
    }

    private void ApplyTheme()
    {
        if (AvaloniaApplication.Current is not { } app) return;
        var light = string.Equals(_theme, "Light", StringComparison.OrdinalIgnoreCase);
        app.RequestedThemeVariant = light
            ? ThemeVariant.Light
            : string.Equals(_theme, "System", StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Default
                : ThemeVariant.Dark;

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
        ApplyTopChromeTheme(light);
        if (_themeButton?.Content is TextBlock text) text.Text = ThemeGlyph();
    }

    private void ApplyTopChromeTheme(bool light)
    {
        var chrome = FindTopChrome();
        if (chrome is null) return;
        var background = Brush(light ? "#F3F3F3" : "#181818");
        var foreground = Brush(light ? "#202020" : "#CCCCCC");
        var searchBackground = Brush(light ? "#FFFFFF" : "#1E1E1E");
        var borderColor = Brush(light ? "#B8B8B8" : "#3F3F46");

        chrome.Background = background;
        foreach (var grid in chrome.GetVisualDescendants().OfType<Grid>())
            if (grid.Background is not null && grid.Background != Brushes.Transparent)
                grid.Background = background;
        foreach (var border in chrome.GetVisualDescendants().OfType<Border>())
            if (border.Child is Menu or Grid)
                border.Background = background;
        foreach (var menu in chrome.GetVisualDescendants().OfType<Menu>())
        {
            menu.Background = background;
            menu.Foreground = foreground;
        }
        foreach (var text in chrome.GetVisualDescendants().OfType<TextBlock>())
            text.Foreground = foreground;

        var search = chrome.GetVisualDescendants().OfType<Avalonia.Controls.TextBox>()
            .FirstOrDefault(static box => string.Equals(box.Name, "ProjectSearchBox", StringComparison.Ordinal));
        if (search is not null)
        {
            search.Background = searchBackground;
            search.Foreground = foreground;
            search.BorderBrush = borderColor;
        }
    }

    private Grid? FindTopChrome()
        => _window.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(static grid => string.Equals(grid.Name, "ReferenceTopChrome", StringComparison.Ordinal));

    private void ApplyEditorPreferences()
    {
        foreach (var editor in _window.GetVisualDescendants().OfType<ManuscriptEditor>())
            editor.ShowLineNumbers = _showLineNumbers;
    }

    private void ManuscriptLineNumbersPreferenceChangedHandler(bool enabled)
    {
        if (_disposed || _showLineNumbers == enabled) return;
        _showLineNumbers = enabled;
        SaveSettings();
        ApplyEditorPreferences();
    }

    private async Task ShowSettingsAsync()
    {
        var theme = new ComboBox
        {
            ItemsSource = new[] { "Dark", "Light", "System" },
            SelectedItem = NormalizeTheme(_theme),
            MinWidth = 190,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var lineNumbers = new CheckBox
        {
            Content = "Show manuscript line numbers",
            IsChecked = _showLineNumbers
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
                LabeledField("Theme", theme),
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

        _theme = theme.SelectedItem?.ToString() ?? "Dark";
        _showLineNumbers = lineNumbers.IsChecked == true;
        SaveSettings();
        ApplyPreferences();
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

            _outlinerTab = items.FirstOrDefault(static item => HeaderEquals(item, "Outliner"));
            var existing = items.FirstOrDefault(static item => HeaderEquals(item, "Preview"));
            if (existing is null)
            {
                var index = items.FindIndex(static item => HeaderEquals(item, "Editor"));
                existing = new TabItem { Header = "Preview", Content = BuildCenterPreviewSurface() };
                items.Insert(Math.Clamp(index + 1, 0, items.Count), existing);
                tabs.ItemsSource = items.ToArray();
            }
            else
            {
                existing.Content = BuildCenterPreviewSurface();
            }
            _centerPreviewInstalled = true;
            UpdateCenterPreview();
            return;
        }
    }

    private Grid BuildCenterPreviewSurface()
    {
        var heading = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        heading.Children.Add(_previewTitle);
        Grid.SetRow(_previewMeta, 1);
        heading.Children.Add(_previewMeta);

        var scroll = new ScrollViewer
        {
            Content = _previewText,
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
        if (!_viewModel.HasDocument)
        {
            _previewTitle.Text = "Preview";
            _previewMeta.Text = "Select a manuscript document to preview it.";
            _previewText.Text = string.Empty;
            return;
        }
        _previewTitle.Text = _viewModel.SelectedTitle;
        _previewMeta.Text = $"Formatted manuscript preview · {_viewModel.WordCount:N0} words";
        _previewText.Text = string.IsNullOrWhiteSpace(_viewModel.PreviewText)
            ? "Nothing to preview yet."
            : _viewModel.PreviewText;
    }

    private void RemoveLegacyLeftTabs()
    {
        if (_leftCleaned) return;
        foreach (var tabs in _window.GetVisualDescendants().OfType<TabControl>())
        {
            var items = TabItems(tabs);
            if (!items.Any(static item => HeaderEquals(item, "Bookmarks"))) continue;
            if (!items.Any(static item => HeaderEquals(item, "Binder") || HeaderEquals(item, "Project"))) continue;
            var filtered = items.Where(static item =>
                !HeaderEquals(item, "Search") &&
                !HeaderEquals(item, "Collections") &&
                !HeaderEquals(item, "Favorites")).ToArray();
            if (filtered.Length != items.Count) tabs.ItemsSource = filtered;
            if (tabs.SelectedIndex < 0 && filtered.Length > 0) tabs.SelectedIndex = 0;
            _leftCleaned = true;
            return;
        }
    }

    private void ArrangeInspectorTabs()
    {
        if (_inspectorArranged) return;
        foreach (var tabs in _window.GetVisualDescendants().OfType<TabControl>())
        {
            var items = TabItems(tabs);
            if (items.Any(static item => HeaderEquals(item, "Editor"))) continue;
            if (!items.Any(static item => HeaderEquals(item, "Comments")) ||
                !items.Any(static item => HeaderEquals(item, "Snapshots")) ||
                !items.Any(static item => HeaderEquals(item, "Project")))
                continue;

            var preview = items.FirstOrDefault(static item => HeaderEquals(item, "PDF") || HeaderEquals(item, "Preview"));
            if (preview is null) continue;
            preview.Header = "Preview";
            var ordered = new List<TabItem> { preview };
            AddTab(ordered, items, "Comments");
            AddTab(ordered, items, "Outline");
            AddTab(ordered, items, "Snapshots");
            AddTab(ordered, items, "Project");
            foreach (var item in items)
            {
                if (HeaderEquals(item, "Inspector")) continue;
                if (!ordered.Contains(item)) ordered.Add(item);
            }
            var selected = tabs.SelectedItem as TabItem;
            tabs.ItemsSource = ordered.ToArray();
            tabs.SelectedItem = selected is not null && ordered.Contains(selected) ? selected : preview;
            _inspectorArranged = true;
            return;
        }
    }

    private static void AddTab(List<TabItem> output, IEnumerable<TabItem> source, string header)
    {
        var item = source.FirstOrDefault(candidate => HeaderEquals(candidate, header));
        if (item is not null && !output.Contains(item)) output.Add(item);
    }

    private void PolishBookmarks()
    {
        if (_bookmarksPolished) return;
        var tab = FindTab("Bookmarks", leftSide: true);
        if (tab?.Content is not Control content) return;
        ToolTip.SetTip(tab, "Bookmarks · save and revisit exact manuscript positions");
        foreach (var button in EnumerateControls(content).OfType<Button>())
        {
            var text = ButtonText(button);
            if (string.Equals(text, "Add Bookmark", StringComparison.Ordinal))
            {
                button.Content = "+ Bookmark";
                button.MinWidth = 100;
                ToolTip.SetTip(button, "Add a labeled bookmark at the current editor caret");
            }
            else if (string.Equals(text, "Remove", StringComparison.Ordinal))
            {
                ToolTip.SetTip(button, "Remove the selected bookmark");
            }
            button.MinHeight = 29;
            button.Padding = new Thickness(9, 3);
        }
        foreach (var list in EnumerateControls(content).OfType<ListBox>())
        {
            list.Margin = new Thickness(8, 4, 8, 8);
            ToolTip.SetTip(list, "Double-click a bookmark to jump to it");
        }
        _bookmarksPolished = true;
    }

    private void PolishComments()
    {
        if (_commentsPolished) return;
        var tab = FindTab("Comments", leftSide: false);
        if (tab?.Content is not Control content) return;
        ToolTip.SetTip(tab, "Comments · attach notes to the current source location without changing manuscript text");
        foreach (var button in EnumerateControls(content).OfType<Button>())
        {
            var text = ButtonText(button);
            if (string.Equals(text, "Add at Caret", StringComparison.Ordinal))
            {
                button.Content = "+ Comment";
                ToolTip.SetTip(button, "Add a comment at the current editor caret");
            }
            else if (string.Equals(text, "Resolve / Reopen", StringComparison.Ordinal))
                button.Content = "Toggle Resolved";
            button.MinHeight = 29;
            button.Padding = new Thickness(9, 3);
        }
        foreach (var list in EnumerateControls(content).OfType<ListBox>())
        {
            list.Margin = new Thickness(8, 4, 8, 8);
            ToolTip.SetTip(list, "Double-click a comment to jump to its source line");
        }
        _commentsPolished = true;
    }

    private void PolishSnapshots()
    {
        if (_snapshotsPolished) return;
        var tab = FindTab("Snapshots", leftSide: false);
        if (tab?.Content is not Control content) return;
        ToolTip.SetTip(tab, "Snapshots · capture, compare and restore manuscript versions");
        foreach (var button in EnumerateControls(content).OfType<Button>())
        {
            if (string.Equals(ButtonText(button), "Take Snapshot", StringComparison.Ordinal))
            {
                button.Content = "+ Snapshot";
                ToolTip.SetTip(button, "Create a named snapshot of the current manuscript text");
            }
            button.MinHeight = 29;
            button.Padding = new Thickness(9, 3);
        }
        _snapshotsPolished = true;
    }

    private void PolishProjectTargets()
    {
        if (_targetsPolished) return;
        var tab = FindTab("Project", leftSide: false);
        if (tab?.Content is not Control content) return;
        ToolTip.SetTip(tab, "Writing targets · project, daily and session word goals");
        var inputs = EnumerateControls(content).OfType<Avalonia.Controls.TextBox>().ToArray();
        for (var index = 0; index < Math.Min(3, inputs.Length); index++)
        {
            inputs[index].MinHeight = 32;
            inputs[index].Padding = new Thickness(8, 4);
            inputs[index].MinWidth = 140;
        }
        if (inputs.Length > 0)
            ToolTip.SetTip(inputs[0], "Project word target · set 0 to disable the goal");
        foreach (var button in EnumerateControls(content).OfType<Button>())
        {
            if (string.Equals(ButtonText(button), "Save Writing Targets", StringComparison.Ordinal))
                button.Content = "Save Targets";
        }
        _targetsPolished = true;
    }

    private void InstallOutlinerLabelControls()
    {
        if (_outlinerLabelsInstalled) return;
        _outlinerTab ??= FindCenterTab("Outliner");
        if (_outlinerTab?.Content is not Control oldContent) return;

        var labelTitle = new TextBlock
        {
            Text = "Label",
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0)
        };
        var colorTitle = new TextBlock
        {
            Text = "Color",
            Opacity = 0.72,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 3, 0)
        };
        var bar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(10, 6, 10, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { labelTitle, _labelBox, colorTitle, _labelSwatch, _labelColor, _applyLabel }
        };
        ToolTip.SetTip(_labelColor, "Choose the shared color for this label");
        ToolTip.SetTip(_applyLabel, "Apply the label and color to the selected Outliner document");

        _outlinerTab.Content = null;
        var wrapper = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        wrapper.Classes.Add("ux-outliner-labels");
        wrapper.Children.Add(bar);
        Grid.SetRow(oldContent, 1);
        wrapper.Children.Add(oldContent);
        _outlinerTab.Content = wrapper;
        _outlinerLabelsInstalled = true;
        EnsureLabelColorProject();
        UpdateLabelControls();
    }

    private async Task ApplySelectedLabelAsync()
    {
        if (_syncingLabelUi) return;
        var node = _viewModel.SelectedRow?.Node;
        if (node?.IsDocument != true) return;

        var label = _labelBox.Text?.Trim() ?? string.Empty;
        var choice = _labelColor.SelectedItem as LabelColorChoice ?? LabelColors[0];
        await _viewModel.SaveSelectedMetadataAsync(
            node.Synopsis,
            node.Notes,
            node.Status,
            label,
            node.Keywords,
            node.TargetWords);

        EnsureLabelColorProject();
        if (!string.IsNullOrWhiteSpace(label))
        {
            if (string.IsNullOrWhiteSpace(choice.Hex)) _labelColorMap.Remove(label);
            else _labelColorMap[label] = choice.Hex;
            SaveLabelColors();
        }
        UpdateLabelControls();
        StyleOutlinerLabelCells();
    }

    private void UpdateLabelControls()
    {
        if (!_outlinerLabelsInstalled) return;
        var node = _viewModel.SelectedRow?.Node;
        var enabled = node?.IsDocument == true;
        _syncingLabelUi = true;
        try
        {
            _labelBox.IsEnabled = enabled;
            _labelColor.IsEnabled = enabled;
            _applyLabel.IsEnabled = enabled;
            _labelBox.Text = enabled ? node!.Label : string.Empty;

            var selected = LabelColors[0];
            if (enabled && !string.IsNullOrWhiteSpace(node!.Label) &&
                _labelColorMap.TryGetValue(node.Label, out var hex))
            {
                selected = LabelColors.FirstOrDefault(choice =>
                    string.Equals(choice.Hex, hex, StringComparison.OrdinalIgnoreCase)) ?? LabelColors[0];
            }
            _labelColor.SelectedItem = selected;
            UpdateLabelSwatch();
        }
        finally
        {
            _syncingLabelUi = false;
        }
    }

    private void UpdateLabelSwatch()
    {
        var choice = _labelColor.SelectedItem as LabelColorChoice;
        _labelSwatch.Background = string.IsNullOrWhiteSpace(choice?.Hex) ? Brushes.Transparent : Brush(choice!.Hex!);
    }

    private void StyleOutlinerLabelCells()
    {
        if (!_outlinerLabelsInstalled || _outlinerTab?.Content is not Control root) return;
        foreach (var grid in EnumerateControls(root).OfType<Grid>())
        {
            var box = grid.Children.OfType<Avalonia.Controls.TextBox>()
                .FirstOrDefault(candidate => Grid.GetColumn(candidate) == 3);
            if (box is null) continue;
            var label = box.Text?.Trim();
            if (string.IsNullOrWhiteSpace(label) || !_labelColorMap.TryGetValue(label, out var hex))
            {
                box.ClearValue(Avalonia.Controls.TextBox.BackgroundProperty);
                box.ClearValue(Avalonia.Controls.TextBox.BorderBrushProperty);
                box.ClearValue(Avalonia.Controls.TextBox.BorderThicknessProperty);
                continue;
            }
            var color = Color.Parse(hex);
            box.Background = new SolidColorBrush(Color.FromArgb(32, color.R, color.G, color.B));
            box.BorderBrush = new SolidColorBrush(color);
            box.BorderThickness = new Thickness(3, 1, 1, 1);
        }
    }

    private void EnsureLabelColorProject()
    {
        var root = CurrentProject()?.RootPath;
        if (string.Equals(root, _labelProjectRoot, StringComparison.Ordinal)) return;
        _labelProjectRoot = root;
        _labelColorMap.Clear();
        if (root is null) return;
        var path = LabelColorPath(root);
        if (!File.Exists(path)) return;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split('\t', 2);
                if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && IsHexColor(parts[1]))
                    _labelColorMap[parts[0]] = parts[1];
            }
        }
        catch
        {
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
            var lines = _labelColorMap
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => pair.Key.Replace('\t', ' ') + "\t" + pair.Value);
            File.WriteAllLines(path, lines);
        }
        catch
        {
        }
    }

    private static string LabelColorPath(string projectRoot)
        => Path.Combine(projectRoot, ".typescribe", "label-colors.tsv");

    private void PolishCardDialogs()
    {
        if (AvaloniaApplication.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        foreach (var dialog in desktop.Windows)
        {
            if (ReferenceEquals(dialog, _window) || !dialog.IsVisible) continue;
            if (dialog.Title?.StartsWith("Edit Corkboard Card", StringComparison.Ordinal) != true) continue;
            if (dialog.Classes.Contains("card-editor-polished")) continue;
            dialog.Classes.Add("card-editor-polished");
            dialog.Width = double.IsNaN(dialog.Width) ? 620 : Math.Max(620, dialog.Width);
            dialog.Height = double.IsNaN(dialog.Height) ? 650 : Math.Max(650, dialog.Height);

            if (dialog.Content is not Control root) continue;
            foreach (var input in EnumerateControls(root).OfType<Avalonia.Controls.TextBox>())
            {
                input.MinHeight = 34;
                input.Padding = new Thickness(9, 5);
                input.HorizontalAlignment = HorizontalAlignment.Stretch;
                var placeholder = input.PlaceholderText ?? string.Empty;
                if (string.Equals(placeholder, "Synopsis", StringComparison.Ordinal))
                {
                    input.MinHeight = 150;
                    input.AcceptsReturn = true;
                    input.TextWrapping = TextWrapping.Wrap;
                }
                else if (placeholder.Contains("Draft", StringComparison.OrdinalIgnoreCase))
                    input.PlaceholderText = "Status — Draft, Revised, Final…";
                else if (placeholder.Contains("storyline", StringComparison.OrdinalIgnoreCase))
                    input.PlaceholderText = "Label — storyline, POV, character arc…";
                else if (placeholder.Contains("keywords", StringComparison.OrdinalIgnoreCase))
                    input.PlaceholderText = "Keywords — comma separated";
                else if (placeholder.Contains("Word target", StringComparison.OrdinalIgnoreCase))
                {
                    input.PlaceholderText = "Word target — 0 for none";
                    input.MaxWidth = 220;
                    input.HorizontalAlignment = HorizontalAlignment.Left;
                }
            }
            foreach (var button in EnumerateControls(root).OfType<Button>())
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

    private BookProject? CurrentProject()
        => typeof(WorkspaceViewModel)
            .GetField("_project", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_viewModel) as BookProject;

    private void LoadSettings()
    {
        try
        {
            var path = SettingsPath();
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadLines(path))
            {
                var split = line.IndexOf('=');
                if (split <= 0) continue;
                var key = line[..split].Trim();
                var value = line[(split + 1)..].Trim();
                if (string.Equals(key, "theme", StringComparison.OrdinalIgnoreCase))
                    _theme = NormalizeTheme(value);
                else if (string.Equals(key, "lineNumbers", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out var enabled))
                    _showLineNumbers = enabled;
            }
        }
        catch
        {
        }
    }

    private void SaveSettings()
    {
        try
        {
            var path = SettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, ["theme=" + NormalizeTheme(_theme), "lineNumbers=" + _showLineNumbers]);
        }
        catch
        {
        }
    }

    private static string SettingsPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
        return Path.Combine(root, "Typescribe", "ui-settings.txt");
    }

    private static string NormalizeTheme(string? theme)
        => string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase)
            ? "Light"
            : string.Equals(theme, "System", StringComparison.OrdinalIgnoreCase)
                ? "System"
                : "Dark";

    private static bool IsHexColor(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Length == 7 && value[0] == '#' &&
           value.AsSpan(1).ToArray().All(static ch => Uri.IsHexDigit(ch));

    private static StackPanel LabeledField(string label, Control input)
        => new()
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, Opacity = 0.78 },
                input
            }
        };

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
            foreach (var panelChild in panel.Children)
                foreach (var descendant in EnumerateControls(panelChild))
                    yield return descendant;
        }
        if (root is Decorator { Child: Control decoratorChild })
        {
            foreach (var descendant in EnumerateControls(decoratorChild))
                yield return descendant;
        }
        if (root is ContentControl { Content: Control contentChild })
        {
            foreach (var descendant in EnumerateControls(contentChild))
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
        _cardDialogTimer.Stop();
        _viewModel.StateChanged -= ViewModelStateChanged;
        ManuscriptLineNumbersPreferenceChanged -= ManuscriptLineNumbersPreferenceChangedHandler;
        _window.Opened -= WindowOpened;
        _window.LayoutUpdated -= WindowLayoutUpdated;
        _window.Closed -= WindowClosed;
    }

    private sealed record LabelColorChoice(string Name, string? Hex)
    {
        public override string ToString() => Name;
    }
}
