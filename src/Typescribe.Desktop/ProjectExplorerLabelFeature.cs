using System.Collections;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;
using Typescribe.Infrastructure.Services;
using NativeTextBox = Avalonia.Controls.TextBox;

namespace Typescribe.Desktop;

/// <summary>
/// Adds compact editable label chips to the native Project Explorer without replacing its
/// hierarchy. Project nodes persist their label through binder metadata; heading labels are
/// stored as project-local explorer metadata. Label colors share the same project color file
/// used by the Outliner so the two surfaces use one label palette.
/// </summary>
internal sealed class ProjectExplorerLabelFeature
{
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

    private static readonly string[] FallbackPalette =
    [
        "#64748B", "#3B82F6", "#8B5CF6", "#F43F5E",
        "#F59E0B", "#10B981", "#06B6D4", "#EC4899"
    ];

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly FileSystemProjectRepository _repository = new();
    private readonly Dictionary<string, string> _labelColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _headingLabels = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private TreeView? _tree;
    private string? _projectRoot;
    private DateTime _labelColorStampUtc;
    private DateTime _headingLabelStampUtc;
    private bool _contextMenuInstalled;
    private bool _scanQueued;
    private bool _disposed;

    private ProjectExplorerLabelFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var feature = new ProjectExplorerLabelFeature(window, viewModel);
        window.Opened += feature.WindowOpened;
        window.LayoutUpdated += feature.WindowLayoutUpdated;
        window.Closed += feature.WindowClosed;
        viewModel.StateChanged += feature.ViewModelStateChanged;
        feature.QueueScan();
    }

    private void WindowOpened(object? sender, EventArgs e) => QueueScan();

    private void WindowLayoutUpdated(object? sender, EventArgs e) => QueueScan();

    private void ViewModelStateChanged(object? sender, EventArgs e) => QueueScan();

    private void QueueScan()
    {
        if (_disposed || _scanQueued) return;
        _scanQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scanQueued = false;
            if (!_disposed) Scan();
        }, DispatcherPriority.Background);
    }

    private void Scan()
    {
        EnsureProjectState();

        var tree = _window.GetVisualDescendants()
            .OfType<TreeView>()
            .FirstOrDefault(static candidate => candidate.Classes.Contains("project-explorer-tree"));
        if (tree is null) return;

        if (!ReferenceEquals(_tree, tree))
        {
            _tree = tree;
            _contextMenuInstalled = false;
        }

        InstallContextMenuCommand();
        RefreshExternalStateIfChanged();

        foreach (var item in tree.GetVisualDescendants().OfType<TreeViewItem>().ToArray())
            DecorateItem(item);
    }

    private void InstallContextMenuCommand()
    {
        if (_contextMenuInstalled || _tree?.ContextMenu is not { } menu) return;

        var items = menu.ItemsSource is IEnumerable source
            ? source.Cast<object?>().Where(static item => item is not null).Cast<object>().ToList()
            : [];
        if (items.OfType<MenuItem>().Any(static item =>
                string.Equals(item.Header?.ToString(), "Label / Color…", StringComparison.Ordinal)))
        {
            _contextMenuInstalled = true;
            return;
        }

        var label = new MenuItem { Header = "Label / Color…" };
        label.Click += async (_, _) =>
        {
            if (_tree?.SelectedItem is { } node && IsEditableNode(node))
                await ShowLabelEditorAsync(node);
        };

        var insertAt = Math.Max(0, items.Count - 2);
        items.Insert(insertAt, new Separator());
        items.Insert(insertAt + 1, label);
        menu.ItemsSource = items;
        _contextMenuInstalled = true;
    }

    private void DecorateItem(TreeViewItem item, string? previewLabel = null, string? previewHex = null)
    {
        if (item.DataContext is not { } node || !IsEditableNode(node)) return;

        var header = item.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(static grid =>
                grid.GetType().Name.Contains("ExplorerNodeHeader", StringComparison.Ordinal));
        if (header is null) return;

        var badge = header.Children
            .OfType<Button>()
            .FirstOrDefault(static button => button.Classes.Contains("project-hierarchy-label-badge"));
        if (badge is null)
        {
            badge = BuildBadgeButton();
            Grid.SetColumn(badge, 2);
            header.Children.Add(badge);
            badge.Click += async (sender, _) =>
            {
                if (sender is not Control control) return;
                var currentItem = control.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
                if (currentItem?.DataContext is { } currentNode && IsEditableNode(currentNode))
                    await ShowLabelEditorAsync(currentNode);
            };
        }

        var labelText = previewLabel ?? LabelForNode(node);
        var hex = previewHex ?? ResolveColorHex(labelText);
        UpdateBadgeAppearance(badge, labelText, hex);

        var title = header.Children
            .OfType<TextBlock>()
            .FirstOrDefault(static text => Grid.GetColumn(text) == 2);
        if (title is not null)
        {
            var reserve = string.IsNullOrWhiteSpace(labelText)
                ? 28
                : Math.Clamp(26 + (labelText.Length * 6.2), 54, 112);
            title.Margin = new Thickness(0, 0, reserve, 0);
        }

        var accent = header.Children
            .OfType<Border>()
            .FirstOrDefault(static border => Grid.GetColumn(border) == 0);
        if (accent is not null)
            accent.Background = string.IsNullOrWhiteSpace(labelText)
                ? Brushes.Transparent
                : new SolidColorBrush(Color.Parse(hex));
    }

    private static Button BuildBadgeButton()
    {
        var text = new TextBlock
        {
            FontSize = 9.5,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 82,
            VerticalAlignment = VerticalAlignment.Center
        };
        var button = new Button
        {
            Content = text,
            Height = 19,
            MinHeight = 19,
            MinWidth = 21,
            MaxWidth = 100,
            Padding = new Thickness(5, 0),
            Margin = new Thickness(5, 0, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1)
        };
        button.Classes.Add("project-hierarchy-label-badge");
        return button;
    }

    private static void UpdateBadgeAppearance(Button badge, string label, string hex)
    {
        if (badge.Content is not TextBlock text) return;

        if (string.IsNullOrWhiteSpace(label))
        {
            text.Text = "+";
            text.Foreground = new SolidColorBrush(Color.Parse("#969696"));
            badge.Background = Brushes.Transparent;
            badge.BorderBrush = new SolidColorBrush(Color.Parse("#55555A"));
            badge.Opacity = 0.62;
            ToolTip.SetTip(badge, "Add label / color");
            return;
        }

        var color = Color.Parse(hex);
        text.Text = label.Trim();
        text.Foreground = new SolidColorBrush(color);
        badge.Background = new SolidColorBrush(Color.FromArgb(34, color.R, color.G, color.B));
        badge.BorderBrush = new SolidColorBrush(Color.FromArgb(190, color.R, color.G, color.B));
        badge.Opacity = 1;
        ToolTip.SetTip(badge, $"{label.Trim()} · click to edit label / color");
    }

    private async Task ShowLabelEditorAsync(object explorerNode)
    {
        if (_disposed || !IsEditableNode(explorerNode)) return;
        EnsureProjectState();

        var originalLabel = LabelForNode(explorerNode);
        var initialChoice = ChoiceForLabel(originalLabel);
        var label = new NativeTextBox
        {
            Text = originalLabel,
            Watermark = "POV, storyline, arc, section…",
            MinHeight = 34,
            MinWidth = 280,
            Padding = new Thickness(8, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var color = new ComboBox
        {
            ItemsSource = LabelColors,
            SelectedItem = initialChoice,
            MinHeight = 34,
            MinWidth = 170,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var swatch = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        var previewText = new TextBlock
        {
            Text = "Changes preview immediately in Project Explorer.",
            Opacity = 0.68,
            TextWrapping = TextWrapping.Wrap
        };
        var done = new Button { Content = "Done", MinWidth = 86 };
        var cancel = new Button { Content = "Cancel", MinWidth = 86, Margin = new Thickness(8, 0, 0, 0) };

        var colorRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { color, swatch }
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { done, cancel }
        };
        var content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "Label & Color",
                    FontSize = 19,
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = NodeDescription(explorerNode),
                    Opacity = 0.72,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 5)
                },
                new TextBlock { Text = "Label", FontWeight = FontWeight.SemiBold },
                label,
                new TextBlock { Text = "Color", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) },
                colorRow,
                previewText,
                new Separator { Margin = new Thickness(0, 6) },
                actions
            }
        };
        var dialog = new Window
        {
            Title = "Label & Color — Project Explorer",
            Width = 440,
            Height = 345,
            MinWidth = 390,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content
        };

        void Preview()
        {
            var value = label.Text?.Trim() ?? string.Empty;
            var choice = color.SelectedItem as LabelColorChoice ?? LabelColors[0];
            var previewColor = !string.IsNullOrWhiteSpace(choice.Hex)
                ? choice.Hex!
                : ResolveColorHex(value);
            var parsed = Color.Parse(previewColor);
            swatch.Background = new SolidColorBrush(Color.FromArgb(42, parsed.R, parsed.G, parsed.B));
            swatch.BorderBrush = new SolidColorBrush(parsed);
            PreviewNode(explorerNode, value, previewColor);
        }

        label.TextChanged += (_, _) => Preview();
        color.SelectionChanged += (_, _) => Preview();
        done.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Opened += (_, _) =>
        {
            label.Focus();
            label.SelectAll();
            Preview();
        };

        var accepted = await dialog.ShowDialog<bool>(_window);
        if (!accepted)
        {
            QueueScan();
            return;
        }

        var finalLabel = label.Text?.Trim() ?? string.Empty;
        var finalChoice = color.SelectedItem as LabelColorChoice ?? LabelColors[0];
        await PersistLabelAsync(explorerNode, finalLabel, finalChoice.Hex);
    }

    private void PreviewNode(object explorerNode, string label, string hex)
    {
        if (_tree is null) return;
        var key = NodeKey(explorerNode);
        foreach (var item in _tree.GetVisualDescendants().OfType<TreeViewItem>())
        {
            if (item.DataContext is not { } candidate) continue;
            if (!ReferenceEquals(candidate, explorerNode) &&
                !string.Equals(NodeKey(candidate), key, StringComparison.Ordinal))
                continue;
            DecorateItem(item, label, hex);
        }
    }

    private async Task PersistLabelAsync(object explorerNode, string label, string? explicitHex)
    {
        if (_disposed) return;
        await _saveGate.WaitAsync();
        try
        {
            var project = CurrentProject();
            if (project is null) return;

            if (NodeRow(explorerNode) is { } row)
            {
                await _repository.SaveNodeMetadataAsync(
                    project,
                    row.Node,
                    row.Node.Synopsis,
                    row.Node.Notes,
                    row.Node.Status,
                    label,
                    row.Node.Keywords,
                    row.Node.TargetWords);
            }
            else if (IsHeadingNode(explorerNode))
            {
                var key = NodeKey(explorerNode);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    if (string.IsNullOrWhiteSpace(label)) _headingLabels.Remove(key);
                    else _headingLabels[key] = label;
                    SaveHeadingLabels();
                }
            }

            if (!string.IsNullOrWhiteSpace(label))
            {
                if (string.IsNullOrWhiteSpace(explicitHex)) _labelColors.Remove(label);
                else _labelColors[label] = explicitHex;
                SaveLabelColors();
            }

            RaiseState(string.IsNullOrWhiteSpace(label)
                ? "Project Explorer label cleared"
                : $"Project Explorer label saved: {label}");
        }
        finally
        {
            _saveGate.Release();
        }

        RefreshAllRealizedItems();
    }

    private void RefreshAllRealizedItems()
    {
        if (_tree is null) return;
        foreach (var item in _tree.GetVisualDescendants().OfType<TreeViewItem>().ToArray())
            DecorateItem(item);
    }

    private void EnsureProjectState()
    {
        var root = CurrentProject()?.RootPath;
        if (string.Equals(root, _projectRoot, StringComparison.Ordinal)) return;

        _projectRoot = root;
        _labelColors.Clear();
        _headingLabels.Clear();
        _labelColorStampUtc = default;
        _headingLabelStampUtc = default;
        if (root is null) return;
        LoadLabelColors();
        LoadHeadingLabels();
    }

    private void RefreshExternalStateIfChanged()
    {
        if (_projectRoot is null) return;
        var colorPath = LabelColorPath(_projectRoot);
        var colorStamp = File.Exists(colorPath) ? File.GetLastWriteTimeUtc(colorPath) : default;
        if (colorStamp != _labelColorStampUtc)
            LoadLabelColors();

        var headingPath = HeadingLabelPath(_projectRoot);
        var headingStamp = File.Exists(headingPath) ? File.GetLastWriteTimeUtc(headingPath) : default;
        if (headingStamp != _headingLabelStampUtc)
            LoadHeadingLabels();
    }

    private void LoadLabelColors()
    {
        _labelColors.Clear();
        if (_projectRoot is null) return;
        var path = LabelColorPath(_projectRoot);
        if (!File.Exists(path))
        {
            _labelColorStampUtc = default;
            return;
        }

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split('\t', 2);
                if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && IsHexColor(parts[1]))
                    _labelColors[parts[0]] = parts[1];
            }
            _labelColorStampUtc = File.GetLastWriteTimeUtc(path);
        }
        catch { }
    }

    private void SaveLabelColors()
    {
        if (_projectRoot is null) return;
        try
        {
            var path = LabelColorPath(_projectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(
                path,
                _labelColors.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(static pair => pair.Key.Replace('\t', ' ') + "\t" + pair.Value));
            _labelColorStampUtc = File.GetLastWriteTimeUtc(path);
        }
        catch { }
    }

    private void LoadHeadingLabels()
    {
        _headingLabels.Clear();
        if (_projectRoot is null) return;
        var path = HeadingLabelPath(_projectRoot);
        if (!File.Exists(path))
        {
            _headingLabelStampUtc = default;
            return;
        }

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split('\t', 2);
                if (parts.Length != 2) continue;
                var key = Decode(parts[0]);
                var value = Decode(parts[1]);
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                    _headingLabels[key] = value;
            }
            _headingLabelStampUtc = File.GetLastWriteTimeUtc(path);
        }
        catch { }
    }

    private void SaveHeadingLabels()
    {
        if (_projectRoot is null) return;
        try
        {
            var path = HeadingLabelPath(_projectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(
                path,
                _headingLabels.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Select(static pair => Encode(pair.Key) + "\t" + Encode(pair.Value)));
            _headingLabelStampUtc = File.GetLastWriteTimeUtc(path);
        }
        catch { }
    }

    private string LabelForNode(object node)
    {
        if (NodeRow(node) is { } row) return row.Node.Label;
        if (IsHeadingNode(node) && NodeKey(node) is { Length: > 0 } key)
            return _headingLabels.GetValueOrDefault(key, string.Empty);
        return string.Empty;
    }

    private string ResolveColorHex(string? label)
    {
        label = label?.Trim() ?? string.Empty;
        if (label.Length == 0) return "#64748B";
        if (_labelColors.TryGetValue(label, out var stored) && IsHexColor(stored)) return stored;

        unchecked
        {
            var hash = 17;
            foreach (var ch in label) hash = (hash * 31) + ch;
            return FallbackPalette[(hash & int.MaxValue) % FallbackPalette.Length];
        }
    }

    private LabelColorChoice ChoiceForLabel(string? label)
    {
        if (!string.IsNullOrWhiteSpace(label) && _labelColors.TryGetValue(label.Trim(), out var hex))
            return LabelColors.FirstOrDefault(choice =>
                string.Equals(choice.Hex, hex, StringComparison.OrdinalIgnoreCase)) ?? LabelColors[0];
        return LabelColors[0];
    }

    private static bool IsEditableNode(object node)
        => NodeRow(node) is not null || IsHeadingNode(node);

    private static bool IsHeadingNode(object node)
        => string.Equals(NodeKindName(node), "Heading", StringComparison.Ordinal);

    private static string NodeDescription(object node)
    {
        if (NodeRow(node) is { } row) return $"{row.Node.Kind} · {row.Node.Title}";
        var heading = NodeHeading(node);
        return heading is null ? "Heading" : $"Heading H{heading.Level} · {heading.Title}";
    }

    private static BinderRowViewModel? NodeRow(object node)
        => node.GetType().GetProperty("Row", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(node) as BinderRowViewModel;

    private static OutlineItemViewModel? NodeHeading(object node)
        => node.GetType().GetProperty("Heading", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(node) as OutlineItemViewModel;

    private static string? NodeKey(object node)
        => node.GetType().GetProperty("Key", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(node)?.ToString();

    private static string? NodeKindName(object node)
        => node.GetType().GetProperty("Kind", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(node)?.ToString();

    private BookProject? CurrentProject()
        => typeof(WorkspaceViewModel)
            .GetField("_project", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_viewModel) as BookProject;

    private void RaiseState(string status)
    {
        typeof(WorkspaceViewModel)
            .GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(_viewModel, [status]);
    }

    private static string LabelColorPath(string projectRoot)
        => Path.Combine(projectRoot, ".typescribe", "label-colors.tsv");

    private static string HeadingLabelPath(string projectRoot)
        => Path.Combine(projectRoot, ".typescribe", "project-explorer-heading-labels.tsv");

    private static string Encode(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Decode(string value)
        => Encoding.UTF8.GetString(Convert.FromBase64String(value));

    private static bool IsHexColor(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length == 7 && value[0] == '#' &&
           value.AsSpan(1).ToArray().All(Uri.IsHexDigit);

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= WindowOpened;
        _window.LayoutUpdated -= WindowLayoutUpdated;
        _window.Closed -= WindowClosed;
        _viewModel.StateChanged -= ViewModelStateChanged;
        _saveGate.Dispose();
    }

    private sealed record LabelColorChoice(string Name, string? Hex)
    {
        public override string ToString() => Name;
    }
}
