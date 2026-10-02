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

namespace Typescribe.Desktop;

/// <summary>
/// Safe Corkboard/Outliner UI repair. It deliberately styles the existing Corkboard editor
/// controls in-place so no control is ever assigned a second visual parent. It also removes
/// the detached Outliner label toolbar and keeps label/color editing inside each row.
/// </summary>
internal sealed class CorkboardOutlinerUiRepairFeature
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
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private readonly Dictionary<string, string> _labelColors = new(StringComparer.OrdinalIgnoreCase);
    private string? _labelProjectRoot;
    private bool _disposed;

    private CorkboardOutlinerUiRepairFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
        _timer.Tick += (_, _) => PolishAvailableUi();
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        var feature = new CorkboardOutlinerUiRepairFeature(window, viewModel);
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

        // WorkspaceUxCompletionFeature previously wrapped the grid with a detached global
        // Label/Color bar. Remove that wrapper after detaching the body from its old parent.
        if (content is Grid wrapper && wrapper.Classes.Contains("ux-outliner-labels"))
        {
            var body = wrapper.Children.OfType<Control>()
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
            .FirstOrDefault(box => string.Equals(box.PlaceholderText, "Filter rows", StringComparison.Ordinal) ||
                                   string.Equals(box.Watermark?.ToString(), "Filter rows", StringComparison.Ordinal));
        if (filter is not null)
        {
            filter.PlaceholderText = "Filter title, type, status or label";
            filter.MinWidth = 260;
            filter.Padding = new Thickness(9, 4);
        }

        foreach (var grid in controls.OfType<Grid>())
        {
            var directButtons = grid.Children.OfType<Button>().ToArray();
            var titleHeader = directButtons.FirstOrDefault(button =>
                Grid.GetColumn(button) == 0 && ButtonText(button).StartsWith("Title", StringComparison.Ordinal));
            if (titleHeader is not null)
            {
                SetOutlinerWidths(grid);
                grid.MinHeight = 34;
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
            grid.MinHeight = 38;
            grid.Margin = new Thickness(4, 0);
            foreach (var box in grid.Children.OfType<Avalonia.Controls.TextBox>())
            {
                box.MinHeight = 32;
                box.Padding = new Thickness(7, 4);
                box.Margin = new Thickness(1);
            }

            var status = grid.Children.OfType<Avalonia.Controls.TextBox>()
                .FirstOrDefault(box => Grid.GetColumn(box) == 2);
            if (status is not null && string.IsNullOrWhiteSpace(status.PlaceholderText))
                status.PlaceholderText = "Status";

            var target = grid.Children.OfType<Avalonia.Controls.TextBox>()
                .FirstOrDefault(box => Grid.GetColumn(box) == 5);
            if (target is not null)
            {
                target.PlaceholderText = "0";
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

        // Explicitly detach before reparenting. Reset its attached Grid.Column as well: the
        // editor previously kept column 3 from the outer Outliner grid, so once it was moved
        // into this two-column label/color cell Avalonia laid it outside the visible columns.
        row.Children.Remove(labelBox);
        Grid.SetColumn(labelBox, 0);
        labelBox.Margin = new Thickness(1);
        labelBox.Padding = new Thickness(7, 4);
        labelBox.MinHeight = 32;
        labelBox.MinWidth = 0;
        labelBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        labelBox.PlaceholderText = "Label";

        var color = new ComboBox
        {
            ItemsSource = LabelColors,
            SelectedItem = ChoiceForLabel(labelBox.Text),
            MinWidth = 86,
            Height = 32,
            Margin = new Thickness(2, 1, 1, 1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(color, "Color used by this label");

        var cell = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,92"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
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

    private void PolishCorkboardHelp()
    {
        var corkboard = FindCenterTab("Corkboard");
        if (corkboard?.Content is not Control root) return;
        var help = EnumerateControls(root).OfType<TextBlock>()
            .FirstOrDefault(text => text.Text?.StartsWith("Double-click a card to open", StringComparison.Ordinal) == true);
        if (help is not null)
            help.Text = "Double-click to open · Right-click or F2 to edit card details · Drag to reorder · Freeform cards can be positioned anywhere.";
    }

    private void PolishCardEditorDialogs()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        foreach (var dialog in desktop.Windows)
        {
            if (ReferenceEquals(dialog, _window) || !dialog.IsVisible) continue;
            if (dialog.Title?.StartsWith("Edit Corkboard Card", StringComparison.Ordinal) != true) continue;
            if (dialog.Classes.Contains("ux-card-editor-safe")) continue;
            if (dialog.Content is not Control root) continue;

            var controls = EnumerateControls(root).ToArray();
            var inputs = controls.OfType<Avalonia.Controls.TextBox>().Take(6).ToArray();
            if (inputs.Length < 6) continue;

            dialog.Classes.Add("ux-card-editor-safe");
            dialog.Width = Math.Max(660, double.IsNaN(dialog.Width) ? 0 : dialog.Width);
            dialog.Height = Math.Max(690, double.IsNaN(dialog.Height) ? 0 : dialog.Height);
            dialog.MinWidth = 560;
            dialog.MinHeight = 560;
            dialog.Background = Brush("#1E1E1E");

            if (root is ScrollViewer scroll)
            {
                scroll.Margin = new Thickness(18);
                if (scroll.Content is StackPanel form)
                {
                    form.Spacing = 10;
                    if (!form.Children.OfType<TextBlock>().Any(text => text.Text == "Edit Corkboard Card"))
                    {
                        form.Children.Insert(0, new TextBlock
                        {
                            Text = "Edit the information shown on the Corkboard card. Changes are saved with the manuscript document.",
                            TextWrapping = TextWrapping.Wrap,
                            Opacity = 0.68,
                            Margin = new Thickness(0, 0, 0, 4)
                        });
                        form.Children.Insert(0, new TextBlock
                        {
                            Text = "Edit Corkboard Card",
                            FontSize = 20,
                            FontWeight = FontWeight.SemiBold,
                            Margin = new Thickness(0, 0, 0, 2)
                        });
                    }
                }
            }

            PrepareInput(inputs[0], "Card title", 38);
            PrepareInput(inputs[1], "Write a short synopsis for the card…", 160);
            inputs[1].AcceptsReturn = true;
            inputs[1].TextWrapping = TextWrapping.Wrap;
            PrepareInput(inputs[2], "Draft, In Progress, Revised, Final…", 38);
            PrepareInput(inputs[3], "Storyline, POV, character arc…", 38);
            PrepareInput(inputs[4], "Comma-separated keywords", 38);
            PrepareInput(inputs[5], "0", 38);
            inputs[5].MaxWidth = 220;
            inputs[5].HorizontalAlignment = HorizontalAlignment.Left;

            foreach (var label in controls.OfType<TextBlock>())
                label.Foreground = Brush("#DCDCDC");

            foreach (var button in controls.OfType<Button>())
            {
                button.MinHeight = 34;
                button.Padding = new Thickness(12, 5);
                if (string.Equals(ButtonText(button), "Save", StringComparison.Ordinal))
                    button.Content = "Save Card";
            }
        }
    }

    private static void PrepareInput(Avalonia.Controls.TextBox box, string placeholder, double minHeight)
    {
        box.PlaceholderText = placeholder;
        box.MinHeight = minHeight;
        box.Padding = new Thickness(10, 7);
        box.HorizontalAlignment = HorizontalAlignment.Stretch;
        box.Background = Brush("#252526");
        box.Foreground = Brush("#F1F1F1");
        box.BorderBrush = Brush("#55555A");
        box.BorderThickness = new Thickness(1);
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
                if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && IsHexColor(parts[1]))
                    _labelColors[parts[0]] = parts[1];
            }
        }
        catch { }
    }

    private void SaveLabelColors()
    {
        var root = CurrentProject()?.RootPath;
        if (root is null) return;
        try
        {
            var path = LabelColorPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, _labelColors.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key.Replace('\t', ' ') + "\t" + pair.Value));
        }
        catch { }
    }

    private BookProject? CurrentProject()
        => typeof(WorkspaceViewModel)
            .GetField("_project", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_viewModel) as BookProject;

    private static string LabelColorPath(string projectRoot)
        => Path.Combine(projectRoot, ".typescribe", "label-colors.tsv");

    private TabItem? FindCenterTab(string header)
    {
        foreach (var tabs in _window.GetVisualDescendants().OfType<TabControl>())
        {
            var items = TabItems(tabs);
            if (!items.Any(item => HeaderEquals(item, "Editor"))) continue;
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
    }

    private static bool IsHexColor(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length == 7 && value[0] == '#' &&
           value.AsSpan(1).ToArray().All(Uri.IsHexDigit);

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
