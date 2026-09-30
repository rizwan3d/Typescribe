using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;
using AvaloniaApplication = Avalonia.Application;

namespace Typescribe.Desktop;

/// <summary>
/// Final Corkboard/Outliner UX pass. Keeps the existing editing/repository wiring, but makes
/// card editing explicit and turns the Outliner into a cleaner spreadsheet surface with inline
/// label-color editing instead of a detached global label toolbar.
/// </summary>
internal sealed class CorkboardOutlinerUxFixFeature
{
    private static readonly LabelColorChoice[] LabelColors =
    [
        new("Auto", null),
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
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(260) };
    private readonly Dictionary<string, string> _labelColors = new(StringComparer.OrdinalIgnoreCase);

    private string? _labelProjectRoot;
    private bool _disposed;

    private CorkboardOutlinerUxFixFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
        _timer.Tick += (_, _) => PolishAvailableUi();
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var feature = new CorkboardOutlinerUxFixFeature(window, viewModel);
        window.Opened += feature.OnOpened;
        window.Closed += feature.OnClosed;
        viewModel.StateChanged += feature.OnStateChanged;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _timer.Start();
        PolishAvailableUi();
    }

    private void OnStateChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(PolishAvailableUi, DispatcherPriority.Background);

    private void PolishAvailableUi()
    {
        if (_disposed) return;
        EnsureLabelColors();
        FixOutliner();
        PolishCorkboardHelp();
        PolishCardEditorDialogs();
    }

    private void FixOutliner()
    {
        var outliner = FindCenterTab("Outliner");
        if (outliner?.Content is not Control content) return;

        // Remove the old detached Label/Color toolbar. Label + color now live together in each row.
        if (content is Grid wrapper && wrapper.Classes.Contains("ux-outliner-labels"))
        {
            var body = wrapper.Children
                .OfType<Control>()
                .FirstOrDefault(child => Grid.GetRow(child) == 1);
            if (body is not null)
            {
                wrapper.Children.Remove(body);
                outliner.Content = null;
                outliner.Content = body;
                content = body;
            }
        }

        var controls = EnumerateControls(content).ToArray();
        var filter = controls.OfType<Avalonia.Controls.TextBox>()
            .FirstOrDefault(box => string.Equals(box.Watermark?.ToString(), "Filter rows", StringComparison.Ordinal));
        if (filter is not null)
        {
            filter.Watermark = "Filter title, type, status or label";
            filter.MinWidth = 250;
            filter.Padding = new Thickness(9, 3);
        }

        foreach (var grid in controls.OfType<Grid>())
        {
            var directButtons = grid.Children.OfType<Button>().ToArray();
            var titleHeader = directButtons.FirstOrDefault(button =>
                Grid.GetColumn(button) == 0 && ButtonText(button).StartsWith("Title", StringComparison.Ordinal));
            if (titleHeader is not null)
            {
                SetOutlinerWidths(grid);
                grid.MinHeight = 32;
                grid.Margin = new Thickness(4, 3, 4, 2);
                grid.Background = new SolidColorBrush(Color.FromArgb(22, 128, 128, 128));
                foreach (var button in directButtons)
                {
                    button.MinHeight = 30;
                    button.Padding = new Thickness(7, 4);
                    button.BorderThickness = new Thickness(0);
                    button.Background = Brushes.Transparent;
                }

                var labelHeader = directButtons.FirstOrDefault(button => Grid.GetColumn(button) == 3);
                if (labelHeader is not null)
                {
                    var old = ButtonText(labelHeader);
                    var marker = old.Contains('↑') ? " ↑" : old.Contains('↓') ? " ↓" : string.Empty;
                    labelHeader.Content = "Label / Color" + marker;
                }
                continue;
            }

            var titleBox = grid.Children.OfType<Avalonia.Controls.TextBox>()
                .FirstOrDefault(box => Grid.GetColumn(box) == 0);
            var compile = grid.Children.OfType<CheckBox>()
                .FirstOrDefault(box => Grid.GetColumn(box) == 6);
            if (titleBox is null || compile is null) continue;

            SetOutlinerWidths(grid);
            grid.MinHeight = 36;
            grid.Margin = new Thickness(4, 0);

            foreach (var box in grid.Children.OfType<Avalonia.Controls.TextBox>())
            {
                box.MinHeight = 32;
                box.Padding = new Thickness(7, 3);
                box.Margin = new Thickness(1);
            }

            var status = grid.Children.OfType<Avalonia.Controls.TextBox>()
                .FirstOrDefault(box => Grid.GetColumn(box) == 2);
            if (status is not null && string.IsNullOrWhiteSpace(status.Watermark?.ToString()))
                status.Watermark = "Status";

            var target = grid.Children.OfType<Avalonia.Controls.TextBox>()
                .FirstOrDefault(box => Grid.GetColumn(box) == 5);
            if (target is not null)
            {
                target.Watermark = "0";
                ToolTip.SetTip(target, "Word target · 0 means no target");
            }
            ToolTip.SetTip(compile, "Include this document in compilation");

            InstallInlineLabelColor(grid);
        }
    }

    private void InstallInlineLabelColor(Grid row)
    {
        if (row.Children.OfType<Grid>().Any(child =>
                Grid.GetColumn(child) == 3 && child.Classes.Contains("ux-outliner-label-cell")))
            return;

        var labelBox = row.Children.OfType<Avalonia.Controls.TextBox>()
            .FirstOrDefault(box => Grid.GetColumn(box) == 3);
        if (labelBox is null) return;

        row.Children.Remove(labelBox);
        labelBox.Margin = new Thickness(1);
        labelBox.Padding = new Thickness(7, 3);
        labelBox.MinHeight = 32;
        labelBox.Watermark = "Label";

        var color = new ComboBox
        {
            ItemsSource = LabelColors,
            SelectedItem = ChoiceForLabel(labelBox.Text),
            MinWidth = 86,
            Height = 32,
            Margin = new Thickness(2, 1, 1, 1),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(color, "Color used by this label");

        var cell = new Grid { ColumnDefinitions = new ColumnDefinitions("*,92") };
        cell.Classes.Add("ux-outliner-label-cell");
        cell.Children.Add(labelBox);
        Grid.SetColumn(color, 1);
        cell.Children.Add(color);
        Grid.SetColumn(cell, 3);
        row.Children.Add(cell);

        ApplyLabelTint(labelBox);
        color.SelectionChanged += (_, _) =>
        {
            var label = labelBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(label)) return;
            var choice = color.SelectedItem as LabelColorChoice ?? LabelColors[0];
            if (string.IsNullOrWhiteSpace(choice.Hex)) _labelColors.Remove(label);
            else _labelColors[label] = choice.Hex;
            SaveLabelColors();
            ApplyLabelTint(labelBox);
        };
        labelBox.TextChanged += (_, _) =>
        {
            color.SelectedItem = ChoiceForLabel(labelBox.Text);
            ApplyLabelTint(labelBox);
        };
    }

    private void ApplyLabelTint(Avalonia.Controls.TextBox box)
    {
        var label = box.Text?.Trim();
        if (string.IsNullOrWhiteSpace(label) || !_labelColors.TryGetValue(label, out var hex))
        {
            box.ClearValue(Avalonia.Controls.TextBox.BackgroundProperty);
            box.ClearValue(Avalonia.Controls.TextBox.BorderBrushProperty);
            box.ClearValue(Avalonia.Controls.TextBox.BorderThicknessProperty);
            return;
        }

        var color = Color.Parse(hex);
        box.Background = new SolidColorBrush(Color.FromArgb(28, color.R, color.G, color.B));
        box.BorderBrush = new SolidColorBrush(color);
        box.BorderThickness = new Thickness(3, 1, 1, 1);
    }

    private LabelColorChoice ChoiceForLabel(string? label)
    {
        if (!string.IsNullOrWhiteSpace(label) && _labelColors.TryGetValue(label.Trim(), out var hex))
            return LabelColors.FirstOrDefault(choice =>
                string.Equals(choice.Hex, hex, StringComparison.OrdinalIgnoreCase)) ?? LabelColors[0];
        return LabelColors[0];
    }

    private static void SetOutlinerWidths(Grid grid)
    {
        if (grid.ColumnDefinitions.Count < 7) return;
        grid.ColumnDefinitions[0].Width = new GridLength(280);
        grid.ColumnDefinitions[1].Width = new GridLength(110);
        grid.ColumnDefinitions[2].Width = new GridLength(140);
        grid.ColumnDefinitions[3].Width = new GridLength(230);
        grid.ColumnDefinitions[4].Width = new GridLength(80);
        grid.ColumnDefinitions[5].Width = new GridLength(100);
        grid.ColumnDefinitions[6].Width = new GridLength(82);
    }

    private void PolishCorkboardHelp()
    {
        var corkboard = FindCenterTab("Corkboard");
        if (corkboard?.Content is not Control root) return;
        var help = EnumerateControls(root).OfType<TextBlock>()
            .FirstOrDefault(text => text.Text?.StartsWith("Double-click a card to open it.", StringComparison.Ordinal) == true);
        if (help is not null)
            help.Text = "Double-click to open · Right-click or press F2 to edit card details · Drag to reorder · Freeform cards can be positioned anywhere.";
    }

    private void PolishCardEditorDialogs()
    {
        if (AvaloniaApplication.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        foreach (var dialog in desktop.Windows)
        {
            if (ReferenceEquals(dialog, _window) || !dialog.IsVisible) continue;
            if (dialog.Title?.StartsWith("Edit Corkboard Card", StringComparison.Ordinal) != true) continue;
            if (dialog.Classes.Contains("ux-card-editor-complete")) continue;
            if (dialog.Content is not Control oldRoot) continue;

            var controls = EnumerateControls(oldRoot).ToArray();
            var inputs = controls.OfType<Avalonia.Controls.TextBox>().ToArray();
            if (inputs.Length < 6) continue;

            var title = inputs[0];
            var synopsis = inputs[1];
            var status = inputs[2];
            var label = inputs[3];
            var keywords = inputs[4];
            var target = inputs[5];
            var save = controls.OfType<Button>().FirstOrDefault(button =>
                string.Equals(ButtonText(button), "Save", StringComparison.Ordinal) ||
                string.Equals(ButtonText(button), "Save Card", StringComparison.Ordinal));
            var cancel = controls.OfType<Button>().FirstOrDefault(button =>
                string.Equals(ButtonText(button), "Cancel", StringComparison.Ordinal));
            if (save is null || cancel is null) continue;

            dialog.Content = null;
            dialog.Width = Math.Max(680, double.IsNaN(dialog.Width) ? 0 : dialog.Width);
            dialog.Height = Math.Max(690, double.IsNaN(dialog.Height) ? 0 : dialog.Height);
            dialog.MinWidth = 560;
            dialog.MinHeight = 560;
            dialog.Classes.Add("ux-card-editor-complete");

            PrepareCardInput(title, "Card title", 36);
            PrepareCardInput(synopsis, "Write a short synopsis for the card…", 170);
            synopsis.AcceptsReturn = true;
            synopsis.TextWrapping = TextWrapping.Wrap;
            PrepareCardInput(status, "Draft, In Progress, Revised, Final…", 36);
            PrepareCardInput(label, "Storyline, POV, character arc…", 36);
            PrepareCardInput(keywords, "Comma-separated keywords", 36);
            PrepareCardInput(target, "0", 36);
            target.MaxWidth = double.PositiveInfinity;
            target.HorizontalAlignment = HorizontalAlignment.Stretch;

            save.Content = "Save Card";
            save.MinWidth = 112;
            save.MinHeight = 34;
            cancel.MinWidth = 88;
            cancel.MinHeight = 34;

            var metadata = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                ColumnSpacing = 10
            };
            metadata.Children.Add(Field("Status", status, "Use any status name; common choices are Draft, Revised and Final."));
            var labelField = Field("Label", label, "Use labels for POV, storyline, character arc or any project grouping.");
            Grid.SetColumn(labelField, 1);
            metadata.Children.Add(labelField);

            var details = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("2*,*"),
                ColumnSpacing = 10
            };
            details.Children.Add(Field("Keywords", keywords, "Comma-separated search keywords."));
            var targetField = Field("Word target", target, "Set 0 for no card target.");
            Grid.SetColumn(targetField, 1);
            details.Children.Add(targetField);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Children = { cancel, save }
            };

            var form = new StackPanel
            {
                Spacing = 12,
                Margin = new Thickness(20),
                Children =
                {
                    new TextBlock
                    {
                        Text = "Edit Corkboard Card",
                        FontSize = 20,
                        FontWeight = FontWeight.SemiBold
                    },
                    new TextBlock
                    {
                        Text = "Edit the information displayed on this card. These fields are stored with the manuscript document.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.68,
                        Margin = new Thickness(0, -5, 0, 3)
                    },
                    Field("Title", title, "The card and manuscript document title."),
                    Field("Synopsis", synopsis, "A concise card summary; this is shown directly on the Corkboard."),
                    new TextBlock { Text = "Card metadata", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, -4) },
                    metadata,
                    details,
                    new Separator { Margin = new Thickness(0, 5, 0, 0) },
                    actions
                }
            };

            dialog.Content = new ScrollViewer
            {
                Content = form,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };
            Dispatcher.UIThread.Post(() => title.Focus(), DispatcherPriority.Background);
        }
    }

    private static void PrepareCardInput(Avalonia.Controls.TextBox box, string watermark, double minHeight)
    {
        box.Watermark = watermark;
        box.MinHeight = minHeight;
        box.Padding = new Thickness(9, 6);
        box.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private static Border Field(string title, Control input, string help)
    {
        var stack = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                input,
                new TextBlock { Text = help, FontSize = 11, Opacity = 0.55, TextWrapping = TextWrapping.Wrap }
            }
        };
        return new Border
        {
            Padding = new Thickness(0, 2),
            Child = stack
        };
    }

    private void EnsureLabelColors()
    {
        var root = CurrentProject()?.RootPath;
        if (string.Equals(root, _labelProjectRoot, StringComparison.Ordinal)) return;
        _labelProjectRoot = root;
        _labelColors.Clear();
        if (root is null) return;
        var path = LabelColorPath(root);
        if (!File.Exists(path)) return;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split('\t', 2);
                if (parts.Length == 2 && IsHexColor(parts[1]) && !string.IsNullOrWhiteSpace(parts[0]))
                    _labelColors[parts[0]] = parts[1];
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
            File.WriteAllLines(path, _labelColors
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => pair.Key.Replace('\t', ' ') + "\t" + pair.Value));
        }
        catch
        {
        }
    }

    private BookProject? CurrentProject()
        => typeof(WorkspaceViewModel)
            .GetField("_project", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_viewModel) as BookProject;

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

    private static IEnumerable<Control> EnumerateControls(Control root)
    {
        yield return root;
        if (root is Panel panel)
        {
            foreach (var child in panel.Children)
                foreach (var descendant in EnumerateControls(child))
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

    private static bool HeaderEquals(TabItem item, string header)
        => string.Equals(item.Header?.ToString(), header, StringComparison.OrdinalIgnoreCase);

    private static List<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is IEnumerable source)
            return source.Cast<object?>().OfType<TabItem>().ToList();
        return tabs.Items.Cast<object?>().OfType<TabItem>().ToList();
    }

    private static string ButtonText(Button button)
        => button.Content is TextBlock text ? text.Text ?? string.Empty : button.Content?.ToString() ?? string.Empty;

    private static bool IsHexColor(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length == 7 && value[0] == '#' &&
           value.AsSpan(1).ToArray().All(static ch => Uri.IsHexDigit(ch));

    private static string LabelColorPath(string projectRoot)
        => Path.Combine(projectRoot, ".typescribe", "label-colors.tsv");

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _timer.Stop();
        _viewModel.StateChanged -= OnStateChanged;
        _window.Opened -= OnOpened;
        _window.Closed -= OnClosed;
    }

    private sealed record LabelColorChoice(string Name, string? Hex)
    {
        public override string ToString() => Name;
    }
}
