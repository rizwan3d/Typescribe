using System.Collections;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;
using Typescribe.Infrastructure.Services;
using NativeTextBlock = Avalonia.Controls.TextBlock;
using NativeTextBox = Avalonia.Controls.TextBox;

namespace Typescribe.Desktop;

/// <summary>
/// Event-driven label editing shared by the Outliner and Project Explorer.
/// Label/color files are loaded once when a project becomes active; normal layout and edit
/// events update the in-memory state, so there is no polling timer or repeated disk read loop.
/// </summary>
internal sealed class EventDrivenLabelEditingFeature
{
    private const double OutlinerLabelColumnWidth = 230;
    private const double OutlinerSwatchColumnWidth = 34;

    private static readonly ColorChoice[] ColorChoices =
    [
        new("Automatic", null),
        new("Slate", "#64748B"),
        new("Blue", "#3B82F6"),
        new("Violet", "#8B5CF6"),
        new("Rose", "#F43F5E"),
        new("Amber", "#F59E0B"),
        new("Emerald", "#10B981"),
        new("Cyan", "#06B6D4"),
        new("Pink", "#EC4899"),
        new("Custom…", null, true)
    ];

    private static readonly string[] FallbackPalette =
    [
        "#64748B", "#3B82F6", "#8B5CF6", "#F43F5E",
        "#F59E0B", "#10B981", "#06B6D4", "#EC4899"
    ];

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly FileSystemProjectRepository _repository = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Dictionary<string, string> _labelColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _itemColors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _headingLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<Grid, ProjectNode> _outlinerNodes = [];
    private readonly HashSet<Grid> _upgradedCells = [];
    private readonly Dictionary<Control, CancellationTokenSource> _pendingSaves = [];
    private readonly Dictionary<TreeViewItem, string> _treeVisualSignatures = [];

    private TreeView? _projectTree;
    private Grid? _outlinerSurface;
    private string? _projectRoot;
    private bool _treeHooked;
    private bool _outlinerHooked;
    private bool _scanQueued;
    private bool _disposed;

    private EventDrivenLabelEditingFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var feature = new EventDrivenLabelEditingFeature(window, viewModel);
        window.Opened += feature.WindowOpened;
        window.LayoutUpdated += feature.WindowLayoutUpdated;
        window.Closed += feature.WindowClosed;
        viewModel.StateChanged += feature.ViewModelStateChanged;
        feature.QueueScan();
    }

    private void WindowOpened(object? sender, EventArgs e) => QueueScan();

    private void WindowLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_disposed && (!_treeHooked || !_outlinerHooked)) QueueScan();
    }

    private void ViewModelStateChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            EnsureProjectState();
            UpgradeOutlinerCells();
            RefreshProjectTreeAppearance();
        }, DispatcherPriority.Background);

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

        _projectTree ??= _window.GetVisualDescendants()
            .OfType<TreeView>()
            .FirstOrDefault(static tree => tree.Classes.Contains("project-explorer-tree"));
        _outlinerSurface ??= _window.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(static grid => grid.Classes.Contains("reliable-outliner"));

        if (_projectTree is not null && !_treeHooked)
        {
            HookProjectTree(_projectTree);
            _treeHooked = true;
        }

        if (_outlinerSurface is not null && !_outlinerHooked)
        {
            _outlinerSurface.LayoutUpdated += OutlinerLayoutUpdated;
            _outlinerHooked = true;
        }

        UpgradeOutlinerCells();
        RefreshProjectTreeAppearance();

        if (_treeHooked && _outlinerHooked)
            _window.LayoutUpdated -= WindowLayoutUpdated;
    }

    private void OutlinerLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_disposed) UpgradeOutlinerCells();
    }

    private void HookProjectTree(TreeView tree)
    {
        tree.LayoutUpdated += ProjectTreeLayoutUpdated;
        tree.AddHandler(
            InputElement.PointerPressedEvent,
            ProjectTreePointerPressed,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        InstallProjectTreeContextMenu(tree);
    }

    private void ProjectTreeLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_disposed) RefreshProjectTreeAppearance();
    }

    private void ProjectTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_disposed || _projectTree is null || e.Source is not Control source) return;
        var button = source as Button ?? source.GetVisualAncestors().OfType<Button>().FirstOrDefault();
        if (button is null || !button.Classes.Contains("project-hierarchy-label-badge")) return;

        var item = button.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
        if (item?.DataContext is not { } node || !IsEditableExplorerNode(node)) return;

        _projectTree.SelectedItem = node;
        e.Handled = true;
        _ = ShowLabelEditorAsync(node);
    }

    private void InstallProjectTreeContextMenu(TreeView tree)
    {
        if (tree.ContextMenu is not { } menu) return;
        var items = menu.ItemsSource is IEnumerable source
            ? source.Cast<object?>().Where(static item => item is not null).Cast<object>().ToList()
            : [];

        foreach (var legacy in items.OfType<MenuItem>().Where(static item =>
                     string.Equals(item.Header?.ToString(), "Label / Color…", StringComparison.Ordinal)))
            legacy.IsVisible = false;

        if (items.OfType<MenuItem>().Any(static item => item.Classes.Contains("event-label-editor"))) return;

        var command = new MenuItem { Header = "Label / Color…" };
        command.Classes.Add("event-label-editor");
        command.Click += async (_, _) =>
        {
            if (_projectTree?.SelectedItem is { } node && IsEditableExplorerNode(node))
                await ShowLabelEditorAsync(node);
        };

        var insertAt = Math.Max(0, items.Count - 2);
        items.Insert(insertAt, new Separator());
        items.Insert(insertAt + 1, command);
        menu.ItemsSource = items;
    }

    private void UpgradeOutlinerCells()
    {
        var surface = _outlinerSurface;
        if (surface is null) return;

        foreach (var grid in surface.GetVisualDescendants().OfType<Grid>())
        {
            if (grid.ColumnDefinitions.Count >= 7)
                grid.ColumnDefinitions[3].Width = new GridLength(OutlinerLabelColumnWidth);
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cell in surface.GetVisualDescendants()
                     .OfType<Grid>()
                     .Where(static grid => grid.Classes.Contains("ux-outliner-label-cell"))
                     .ToArray())
        {
            if (_upgradedCells.Contains(cell))
            {
                if (_outlinerNodes.TryGetValue(cell, out var known)) used.Add(known.PersistentId);
                continue;
            }

            var node = ResolveOutlinerNode(cell, used);
            if (node is null) continue;
            used.Add(node.PersistentId);
            UpgradeOutlinerCell(cell, node);
        }

        foreach (var stale in _upgradedCells.Where(cell => !cell.IsAttachedToVisualTree()).ToArray())
        {
            _upgradedCells.Remove(stale);
            _outlinerNodes.Remove(stale);
        }
    }

    private ProjectNode? ResolveOutlinerNode(Grid labelCell, HashSet<string> used)
    {
        if (labelCell.Parent is not Grid row) return null;

        var title = row.Children.OfType<NativeTextBox>()
            .FirstOrDefault(box => Grid.GetColumn(box) == 0)?.Text?.Trim() ?? string.Empty;
        var status = row.Children.OfType<NativeTextBox>()
            .FirstOrDefault(box => Grid.GetColumn(box) == 2)?.Text?.Trim() ?? string.Empty;
        var kind = row.Children.OfType<ComboBox>()
            .FirstOrDefault(box => Grid.GetColumn(box) == 1)?.SelectedItem as NodeKind?;

        var candidates = _viewModel.BinderRows
            .Select(static rowVm => rowVm.Node)
            .Where(static node => node.IsDocument)
            .Where(node => !used.Contains(node.PersistentId))
            .Where(node => string.Equals(node.Title, title, StringComparison.Ordinal))
            .ToArray();

        if (candidates.Length == 1) return candidates[0];
        return candidates.FirstOrDefault(node =>
                   (!kind.HasValue || node.Kind == kind.Value) &&
                   string.Equals(node.Status ?? string.Empty, status, StringComparison.Ordinal))
               ?? candidates.FirstOrDefault();
    }

    private void UpgradeOutlinerCell(Grid cell, ProjectNode node)
    {
        var oldLabel = cell.Children.OfType<NativeTextBox>().FirstOrDefault();
        var oldColor = cell.Children.OfType<ComboBox>().FirstOrDefault();
        if (oldLabel is null || oldColor is null) return;

        var storageKey = ProjectNodeStorageKey(node);
        var explicitColor = ExplicitColor(storageKey, node.Label);
        string? customHex = IsCustomHex(explicitColor) ? explicitColor : null;

        var label = new NativeTextBox
        {
            Text = node.Label,
            Watermark = "Label",
            MinHeight = 32,
            Height = 32,
            Margin = new Thickness(1),
            Padding = new Thickness(7, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(label, "Type a label for this document");

        var selectedColor = ChoiceForHex(explicitColor);

        var swatch = new Button
        {
            Content = BuildColorChip(explicitColor ?? DisplayColor(storageKey, node.Label), true),
            Width = OutlinerSwatchColumnWidth,
            Height = 32,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 1, 1, 1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(swatch, "Choose label color");

        cell.Children.Remove(oldLabel);
        cell.Children.Remove(oldColor);
        cell.ColumnDefinitions = new ColumnDefinitions($"*,{OutlinerSwatchColumnWidth}");
        cell.ColumnSpacing = 4;
        cell.ClipToBounds = true;
        cell.Children.Add(label);
        Grid.SetColumn(swatch, 1);
        cell.Children.Add(swatch);

        _upgradedCells.Add(cell);
        _outlinerNodes[cell] = node;

        var syncing = false;
        void RefreshAppearance()
        {
            var text = label.Text?.Trim() ?? string.Empty;
            var hex = EffectiveColor(storageKey, text, selectedColor, customHex);
            ApplyTextBoxTint(label, text, hex);
            ApplySwatch(swatch, hex);
        }

        async Task ApplyColorChoiceAsync(ColorChoice choice)
        {
            if (syncing) return;
            if (choice.IsCustom)
            {
                var initial = customHex ?? ExplicitColor(storageKey, label.Text) ?? FallbackColor(label.Text ?? string.Empty);
                var picked = await PickColorAsync(_window, initial);
                if (picked is null)
                {
                    selectedColor = ChoiceForHex(ExplicitColor(storageKey, label.Text));
                    RefreshAppearance();
                    return;
                }
                customHex = picked;
            }
            else
            {
                customHex = null;
            }

            selectedColor = choice;
            RefreshAppearance();
            await SaveOutlinerAsync(node, label, selectedColor, customHex);
        }

        swatch.Flyout = BuildColorFlyout(ApplyColorChoiceAsync);

        label.TextChanged += (_, _) =>
        {
            if (syncing) return;
            var text = label.Text?.Trim() ?? string.Empty;
            var known = ExplicitColor(storageKey, text);
            if (!string.IsNullOrWhiteSpace(known))
            {
                syncing = true;
                selectedColor = ChoiceForHex(known);
                customHex = IsCustomHex(known) ? known : null;
                syncing = false;
            }
            else if (text.Length > 0)
            {
                syncing = true;
                selectedColor = ColorChoices[0];
                customHex = null;
                syncing = false;
            }
            RefreshAppearance();
            ScheduleOutlinerSave(label, node, () => selectedColor, () => customHex);
        };

        label.LostFocus += async (_, _) => await SaveOutlinerAsync(node, label, selectedColor, customHex);
        label.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await SaveOutlinerAsync(node, label, selectedColor, customHex);
        };

        RefreshAppearance();
    }

    private void ScheduleOutlinerSave(
        NativeTextBox label,
        ProjectNode node,
        Func<ColorChoice> color,
        Func<string?> customHex)
    {
        if (_pendingSaves.Remove(label, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        var cts = new CancellationTokenSource();
        _pendingSaves[label] = cts;
        _ = SaveAfterDelayAsync(label, node, color, customHex, cts.Token);
    }

    private async Task SaveAfterDelayAsync(
        NativeTextBox label,
        ProjectNode node,
        Func<ColorChoice> color,
        Func<string?> customHex,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(260, cancellationToken);
            await SaveOutlinerAsync(node, label, color(), customHex(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task SaveOutlinerAsync(
        ProjectNode node,
        NativeTextBox label,
        ColorChoice choice,
        string? customHex,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || CurrentProject() is not { } project) return;

        var value = label.Text?.Trim() ?? string.Empty;
        var explicitHex = choice.IsCustom ? NormalizeHex(customHex) : NormalizeHex(choice.Hex);
        if (choice.IsCustom && explicitHex is null) return;

        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            if (!string.Equals(node.Label, value, StringComparison.Ordinal))
            {
                await _repository.SaveNodeMetadataAsync(
                    project,
                    node,
                    node.Synopsis,
                    node.Notes,
                    node.Status,
                    value,
                    node.Keywords,
                    node.TargetWords,
                    cancellationToken);
            }

            PersistColor(ProjectNodeStorageKey(node), value, explicitHex);
        }
        finally
        {
            _saveGate.Release();
        }

        RefreshProjectTreeAppearance();
    }

    private void RefreshProjectTreeAppearance()
    {
        var tree = _projectTree;
        if (tree is null) return;

        foreach (var item in tree.GetVisualDescendants().OfType<TreeViewItem>().ToArray())
        {
            if (item.DataContext is not { } node || !IsEditableExplorerNode(node)) continue;
            var signature = VisualSignature(node);
            if (_treeVisualSignatures.TryGetValue(item, out var old) &&
                string.Equals(old, signature, StringComparison.Ordinal))
                continue;

            _treeVisualSignatures[item] = signature;
            DecorateTreeItem(item, node);
        }

        foreach (var stale in _treeVisualSignatures.Keys.Where(item => !item.IsAttachedToVisualTree()).ToArray())
            _treeVisualSignatures.Remove(stale);
    }

    private string VisualSignature(object node)
    {
        var label = LabelForExplorerNode(node);
        var key = ExplorerStorageKey(node);
        return key + "\u001f" + label + "\u001f" + (DisplayColor(key, label) ?? string.Empty);
    }

    private void DecorateTreeItem(TreeViewItem item, object node, string? previewLabel = null, string? previewHex = null)
    {
        var header = item.GetVisualDescendants().OfType<Grid>()
            .FirstOrDefault(static grid => grid.GetType().Name.Contains("ExplorerNodeHeader", StringComparison.Ordinal));
        if (header is null) return;

        var badge = header.Children.OfType<Button>()
            .FirstOrDefault(static button => button.Classes.Contains("project-hierarchy-label-badge"));
        if (badge is null)
        {
            badge = BuildBadgeButton();
            Grid.SetColumn(badge, 2);
            header.Children.Add(badge);
        }

        var label = previewLabel ?? LabelForExplorerNode(node);
        var key = ExplorerStorageKey(node);
        var hex = previewHex ?? DisplayColor(key, label);
        UpdateBadgeAppearance(badge, label, hex);

        var title = header.Children.OfType<NativeTextBlock>()
            .FirstOrDefault(static text => Grid.GetColumn(text) == 2 &&
                                           !text.Classes.Contains("project-hierarchy-label-text"));
        if (title is not null)
        {
            var reserve = string.IsNullOrWhiteSpace(label) && string.IsNullOrWhiteSpace(hex)
                ? 28
                : string.IsNullOrWhiteSpace(label)
                    ? 34
                    : Math.Clamp(26 + (label.Length * 6.2), 54, 112);
            title.Margin = new Thickness(0, 0, reserve, 0);
        }

        var accent = header.Children.OfType<Border>()
            .FirstOrDefault(static border => Grid.GetColumn(border) == 0);
        if (accent is not null)
            accent.Background = string.IsNullOrWhiteSpace(hex)
                ? Brushes.Transparent
                : new SolidColorBrush(Color.Parse(hex));
    }

    private static Button BuildBadgeButton()
    {
        var text = new NativeTextBlock
        {
            FontSize = 9.5,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 82,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.Classes.Add("project-hierarchy-label-text");

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

    private static void UpdateBadgeAppearance(Button badge, string label, string? hex)
    {
        if (badge.Content is not NativeTextBlock text) return;

        if (string.IsNullOrWhiteSpace(label) && string.IsNullOrWhiteSpace(hex))
        {
            text.Text = "+";
            text.Foreground = new SolidColorBrush(Color.Parse("#969696"));
            badge.Background = Brushes.Transparent;
            badge.BorderBrush = new SolidColorBrush(Color.Parse("#55555A"));
            badge.Opacity = 0.62;
            ToolTip.SetTip(badge, "Add label / color");
            return;
        }

        var parsed = Color.Parse(hex ?? "#64748B");
        text.Text = string.IsNullOrWhiteSpace(label) ? "●" : label.Trim();
        text.Foreground = new SolidColorBrush(parsed);
        badge.Background = new SolidColorBrush(Color.FromArgb(34, parsed.R, parsed.G, parsed.B));
        badge.BorderBrush = new SolidColorBrush(Color.FromArgb(190, parsed.R, parsed.G, parsed.B));
        badge.Opacity = 1;
        ToolTip.SetTip(badge, string.IsNullOrWhiteSpace(label)
            ? "Color only · click to edit"
            : $"{label.Trim()} · click to edit label / color");
    }

    private async Task ShowLabelEditorAsync(object explorerNode)
    {
        if (_disposed || !IsEditableExplorerNode(explorerNode)) return;
        EnsureProjectState();

        var storageKey = ExplorerStorageKey(explorerNode);
        var originalLabel = LabelForExplorerNode(explorerNode);
        var explicitColor = ExplicitColor(storageKey, originalLabel);
        string? customHex = IsCustomHex(explicitColor) ? explicitColor : null;

        var label = new ComboBox
        {
            IsEditable = true,
            ItemsSource = ExistingLabels(),
            Text = originalLabel,
            PlaceholderText = "Type or select a label",
            MinHeight = 34,
            MinWidth = 300,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ToolTip.SetTip(label, "Type a new label or select one already used in this project");

        var color = new ComboBox
        {
            ItemsSource = ColorChoices,
            SelectedItem = ChoiceForHex(explicitColor),
            MinHeight = 34,
            MinWidth = 170
        };
        var swatch = new Button
        {
            Content = "●",
            Width = 40,
            Height = 34,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(0)
        };
        ToolTip.SetTip(swatch, "Open custom color picker");

        var previewText = new NativeTextBlock
        {
            Text = "Changes preview immediately. You can keep Label empty and save only a color.",
            Opacity = 0.7,
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
                new NativeTextBlock { Text = "Label & Color", FontSize = 19, FontWeight = FontWeight.SemiBold },
                new NativeTextBlock
                {
                    Text = NodeDescription(explorerNode),
                    Opacity = 0.72,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 5)
                },
                new NativeTextBlock { Text = "Label", FontWeight = FontWeight.SemiBold },
                label,
                new NativeTextBlock { Text = "Color", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) },
                colorRow,
                previewText,
                new Separator { Margin = new Thickness(0, 6) },
                actions
            }
        };
        var dialog = new Window
        {
            Title = "Label & Color — Project Explorer",
            Width = 460,
            Height = 370,
            MinWidth = 410,
            MinHeight = 340,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content
        };

        var syncing = false;
        void Preview()
        {
            var text = label.Text?.Trim() ?? string.Empty;
            var choice = color.SelectedItem as ColorChoice ?? ColorChoices[0];
            var hex = EffectiveColor(storageKey, text, choice, customHex);
            ApplySwatch(swatch, hex);
            PreviewExplorerNode(explorerNode, text, hex);
        }

        label.PropertyChanged += (_, e) =>
        {
            if (e.Property != ComboBox.TextProperty || syncing) return;
            var text = label.Text?.Trim() ?? string.Empty;
            var known = ExplicitColor(storageKey, text);
            if (!string.IsNullOrWhiteSpace(known))
            {
                syncing = true;
                color.SelectedItem = ChoiceForHex(known);
                customHex = IsCustomHex(known) ? known : null;
                syncing = false;
            }
            Preview();
        };
        label.SelectionChanged += (_, _) =>
        {
            if (label.SelectedItem is string selected) label.Text = selected;
        };
        color.SelectionChanged += async (_, _) =>
        {
            if (syncing) return;
            var choice = color.SelectedItem as ColorChoice ?? ColorChoices[0];
            if (choice.IsCustom)
            {
                var initial = customHex ?? ExplicitColor(storageKey, label.Text) ?? FallbackColor(label.Text ?? string.Empty);
                var picked = await PickColorAsync(dialog, initial);
                if (picked is null)
                {
                    syncing = true;
                    color.SelectedItem = ChoiceForHex(ExplicitColor(storageKey, label.Text));
                    syncing = false;
                    Preview();
                    return;
                }
                customHex = picked;
            }
            else
            {
                customHex = null;
            }
            Preview();
        };
        swatch.Click += async (_, _) =>
        {
            var initial = EffectiveColor(
                storageKey,
                label.Text ?? string.Empty,
                color.SelectedItem as ColorChoice ?? ColorChoices[0],
                customHex) ?? "#64748B";
            var picked = await PickColorAsync(dialog, initial);
            if (picked is null) return;
            customHex = picked;
            syncing = true;
            color.SelectedItem = ColorChoices[^1];
            syncing = false;
            Preview();
        };
        done.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Opened += (_, _) => Preview();

        var accepted = await dialog.ShowDialog<bool>(_window);
        if (!accepted)
        {
            RefreshProjectTreeAppearance();
            return;
        }

        var finalLabel = label.Text?.Trim() ?? string.Empty;
        var finalChoice = color.SelectedItem as ColorChoice ?? ColorChoices[0];
        var finalHex = finalChoice.IsCustom ? NormalizeHex(customHex) : NormalizeHex(finalChoice.Hex);
        if (finalChoice.IsCustom && finalHex is null) return;
        await PersistExplorerLabelAsync(explorerNode, finalLabel, finalHex);
    }

    private void PreviewExplorerNode(object node, string label, string? hex)
    {
        if (_projectTree is null) return;
        var key = ExplorerStorageKey(node);
        foreach (var item in _projectTree.GetVisualDescendants().OfType<TreeViewItem>())
        {
            if (item.DataContext is not { } candidate ||
                !string.Equals(ExplorerStorageKey(candidate), key, StringComparison.Ordinal))
                continue;
            DecorateTreeItem(item, candidate, label, hex);
            _treeVisualSignatures.Remove(item);
        }
    }

    private async Task PersistExplorerLabelAsync(object explorerNode, string label, string? explicitHex)
    {
        if (_disposed || CurrentProject() is not { } project) return;
        await _saveGate.WaitAsync();
        try
        {
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

            PersistColor(ExplorerStorageKey(explorerNode), label, explicitHex);
            RaiseState(string.IsNullOrWhiteSpace(label)
                ? (explicitHex is null ? "Project Explorer label/color cleared" : "Project Explorer color saved")
                : $"Project Explorer label saved: {label}");
        }
        finally
        {
            _saveGate.Release();
        }

        RefreshProjectTreeAppearance();
    }

    private string[] ExistingLabels()
        => _viewModel.BinderRows
            .Select(static row => row.Node.Label)
            .Concat(_headingLabels.Values)
            .Concat(_labelColors.Keys)
            .Where(static label => !string.IsNullOrWhiteSpace(label))
            .Select(static label => label.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static label => label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private void EnsureProjectState()
    {
        var root = CurrentProject()?.RootPath;
        if (string.Equals(root, _projectRoot, StringComparison.Ordinal)) return;

        _projectRoot = root;
        _labelColors.Clear();
        _itemColors.Clear();
        _headingLabels.Clear();
        _treeVisualSignatures.Clear();
        _upgradedCells.Clear();
        _outlinerNodes.Clear();
        if (root is null) return;

        LoadLabelColors(root);
        LoadItemColors(root);
        LoadHeadingLabels(root);
    }

    private void LoadLabelColors(string root)
    {
        var path = Path.Combine(root, ".typescribe", "label-colors.tsv");
        if (!File.Exists(path)) return;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split('\t', 2);
                if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && IsHexColor(parts[1]))
                    _labelColors[parts[0].Trim()] = parts[1].ToUpperInvariant();
            }
        }
        catch { }
    }

    private void LoadItemColors(string root)
    {
        var path = Path.Combine(root, ".typescribe", "project-item-colors.tsv");
        if (!File.Exists(path)) return;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split('\t', 2);
                if (parts.Length != 2 || !IsHexColor(parts[1])) continue;
                var key = Decode(parts[0]);
                if (!string.IsNullOrWhiteSpace(key)) _itemColors[key] = parts[1].ToUpperInvariant();
            }
        }
        catch { }
    }

    private void LoadHeadingLabels(string root)
    {
        var path = HeadingLabelPath(root);
        if (!File.Exists(path)) return;
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
        }
        catch { }
    }

    private void PersistColor(string storageKey, string label, string? explicitHex)
    {
        label = label.Trim();
        explicitHex = NormalizeHex(explicitHex);

        if (label.Length == 0)
        {
            RemoveLegacyItemColorAlias(storageKey);
            if (explicitHex is null) _itemColors.Remove(storageKey);
            else _itemColors[storageKey] = explicitHex;
            SaveItemColors();
            return;
        }

        RemoveLegacyItemColorAlias(storageKey);
        _itemColors.Remove(storageKey);
        SaveItemColors();
        if (explicitHex is null) _labelColors.Remove(label);
        else _labelColors[label] = explicitHex;
        SaveLabelColors();
    }

    private void RemoveLegacyItemColorAlias(string storageKey)
    {
        if (storageKey.StartsWith("node:", StringComparison.Ordinal))
            _itemColors.Remove(storageKey[5..]);
        else
            _itemColors.Remove("node:" + storageKey);
    }

    private string? ExplicitColor(string storageKey, string? label)
    {
        label = label?.Trim() ?? string.Empty;
        if (label.Length > 0) return _labelColors.GetValueOrDefault(label);

        if (_itemColors.TryGetValue(storageKey, out var direct)) return direct;
        if (storageKey.StartsWith("node:", StringComparison.Ordinal) &&
            _itemColors.TryGetValue(storageKey[5..], out var legacy)) return legacy;
        if (!storageKey.StartsWith("node:", StringComparison.Ordinal) &&
            _itemColors.TryGetValue("node:" + storageKey, out var canonical)) return canonical;
        return null;
    }

    private string? DisplayColor(string storageKey, string label)
        => ExplicitColor(storageKey, label) ??
           (string.IsNullOrWhiteSpace(label) ? null : FallbackColor(label));

    private string? EffectiveColor(string storageKey, string label, ColorChoice choice, string? customHex)
    {
        if (choice.IsCustom) return NormalizeHex(customHex);
        if (!string.IsNullOrWhiteSpace(choice.Hex)) return choice.Hex;
        return DisplayColor(storageKey, label);
    }

    private void SaveLabelColors()
    {
        if (_projectRoot is null) return;
        try
        {
            var path = Path.Combine(_projectRoot, ".typescribe", "label-colors.tsv");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, _labelColors
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => pair.Key.Replace('\t', ' ') + "\t" + pair.Value));
        }
        catch { }
    }

    private void SaveItemColors()
    {
        if (_projectRoot is null) return;
        try
        {
            var path = Path.Combine(_projectRoot, ".typescribe", "project-item-colors.tsv");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, _itemColors
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => Encode(pair.Key) + "\t" + pair.Value));
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
            File.WriteAllLines(path, _headingLabels
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => Encode(pair.Key) + "\t" + Encode(pair.Value)));
        }
        catch { }
    }

    private string LabelForExplorerNode(object node)
    {
        if (NodeRow(node) is { } row) return row.Node.Label;
        var key = NodeKey(node);
        return key is null ? string.Empty : _headingLabels.GetValueOrDefault(key, string.Empty);
    }

    private static string ExplorerStorageKey(object node)
    {
        if (NodeRow(node) is { } row) return ProjectNodeStorageKey(row.Node);
        return NodeKey(node) is { Length: > 0 } key ? key : string.Empty;
    }

    private static string ProjectNodeStorageKey(ProjectNode node) => "node:" + node.PersistentId;

    private static ColorChoice ChoiceForHex(string? hex)
    {
        if (!IsHexColor(hex)) return ColorChoices[0];
        return ColorChoices.FirstOrDefault(choice =>
                   !choice.IsCustom && string.Equals(choice.Hex, hex, StringComparison.OrdinalIgnoreCase))
               ?? ColorChoices[^1];
    }

    private static bool IsCustomHex(string? hex)
        => IsHexColor(hex) && !ColorChoices.Any(choice =>
            !choice.IsCustom && string.Equals(choice.Hex, hex, StringComparison.OrdinalIgnoreCase));

    private static void ApplyTextBoxTint(NativeTextBox box, string label, string? hex)
    {
        if (!IsHexColor(hex))
        {
            box.ClearValue(NativeTextBox.BackgroundProperty);
            box.ClearValue(NativeTextBox.BorderBrushProperty);
            box.ClearValue(NativeTextBox.BorderThicknessProperty);
            return;
        }

        var parsed = Color.Parse(hex!);
        box.Background = new SolidColorBrush(Color.FromArgb(24, parsed.R, parsed.G, parsed.B));
        box.BorderBrush = new SolidColorBrush(parsed);
        box.BorderThickness = new Thickness(3, 1, 1, 1);
        ToolTip.SetTip(box, string.IsNullOrWhiteSpace(label) ? "Color-only item" : $"Label: {label}");
    }

    private static void ApplySwatch(Button swatch, string? hex)
    {
        var chip = swatch.Content as Border;
        if (!IsHexColor(hex))
        {
            swatch.Foreground = new SolidColorBrush(Color.Parse("#969696"));
            swatch.Background = Brushes.Transparent;
            swatch.BorderBrush = new SolidColorBrush(Color.Parse("#55555A"));
            if (chip is not null)
            {
                chip.Background = Brushes.Transparent;
                chip.BorderBrush = new SolidColorBrush(Color.Parse("#777777"));
            }
            return;
        }

        var parsed = Color.Parse(hex!);
        swatch.Foreground = new SolidColorBrush(parsed);
        swatch.Background = new SolidColorBrush(Color.FromArgb(42, parsed.R, parsed.G, parsed.B));
        swatch.BorderBrush = new SolidColorBrush(parsed);
        if (chip is not null)
        {
            chip.Background = new SolidColorBrush(parsed);
            chip.BorderBrush = new SolidColorBrush(parsed);
        }
    }

    private static MenuFlyout BuildColorFlyout(Func<ColorChoice, Task> choose)
    {
        var items = new List<object>();
        foreach (var choice in ColorChoices)
        {
            if (choice.IsCustom && items.Count > 0)
                items.Add(new Separator());

            var item = new MenuItem
            {
                Header = choice.Name,
                Icon = BuildColorChip(ColorForChoice(choice), false)
            };
            item.Click += async (_, _) => await choose(choice);
            items.Add(item);
        }

        return new MenuFlyout { ItemsSource = items };
    }

    private static Border BuildColorChip(string? hex, bool large)
    {
        var hasColor = IsHexColor(hex);
        var parsed = hasColor ? Color.Parse(hex!) : Color.Parse("#777777");
        return new Border
        {
            Width = large ? 14 : 13,
            Height = large ? 14 : 13,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(hasColor ? parsed : Color.Parse("#777777")),
            Background = hasColor ? new SolidColorBrush(parsed) : Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static string? ColorForChoice(ColorChoice choice)
    {
        if (!string.IsNullOrWhiteSpace(choice.Hex)) return choice.Hex;
        return choice.IsCustom ? "#A78BFA" : null;
    }

    private static async Task<string?> PickColorAsync(Window owner, string? initialHex)
    {
        var initial = ParseColor(initialHex, Color.Parse("#64748B"));
        var syncing = false;

        var preview = new Border
        {
            Width = 72,
            Height = 72,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse("#666666")),
            Background = new SolidColorBrush(initial)
        };
        var hex = new NativeTextBox
        {
            Text = ToHex(initial),
            PlaceholderText = "#RRGGBB",
            MaxLength = 7,
            MinWidth = 110,
            Height = 32,
            Padding = new Thickness(7, 3),
            VerticalContentAlignment = VerticalAlignment.Center
        };

        static Slider Channel(string name, byte value)
        {
            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 255,
                Value = value,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                MinWidth = 230
            };
            ToolTip.SetTip(slider, name);
            return slider;
        }

        var red = Channel("Red", initial.R);
        var green = Channel("Green", initial.G);
        var blue = Channel("Blue", initial.B);

        var redValue = new NativeTextBlock { Width = 34, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var greenValue = new NativeTextBlock { Width = 34, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var blueValue = new NativeTextBlock { Width = 34, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };

        static StackPanel SliderRow(string name, Slider slider, NativeTextBlock value)
            => new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new NativeTextBlock { Text = name, Width = 48, VerticalAlignment = VerticalAlignment.Center },
                    slider,
                    value
                }
            };

        void SetColor(Color value)
        {
            syncing = true;
            try
            {
                red.Value = value.R;
                green.Value = value.G;
                blue.Value = value.B;
                preview.Background = new SolidColorBrush(value);
                hex.Text = ToHex(value);
                redValue.Text = value.R.ToString(System.Globalization.CultureInfo.InvariantCulture);
                greenValue.Text = value.G.ToString(System.Globalization.CultureInfo.InvariantCulture);
                blueValue.Text = value.B.ToString(System.Globalization.CultureInfo.InvariantCulture);
                hex.ClearValue(NativeTextBox.BorderBrushProperty);
            }
            finally
            {
                syncing = false;
            }
        }

        void UpdateFromSliders()
        {
            if (syncing) return;
            var value = Color.FromRgb(
                (byte)Math.Clamp((int)Math.Round(red.Value), 0, 255),
                (byte)Math.Clamp((int)Math.Round(green.Value), 0, 255),
                (byte)Math.Clamp((int)Math.Round(blue.Value), 0, 255));
            SetColor(value);
        }

        foreach (var slider in new[] { red, green, blue })
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty) UpdateFromSliders();
            };

        hex.TextChanged += (_, _) =>
        {
            if (syncing) return;
            var normalized = NormalizeHex(hex.Text);
            if (normalized is null)
            {
                hex.BorderBrush = Brushes.IndianRed;
                return;
            }
            SetColor(Color.Parse(normalized));
        };

        var quick = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7
        };
        foreach (var colorHex in new[]
                 {
                     "#64748B", "#3B82F6", "#8B5CF6", "#F43F5E", "#F59E0B",
                     "#10B981", "#06B6D4", "#EC4899", "#111111", "#FFFFFF"
                 })
        {
            var parsed = Color.Parse(colorHex);
            var button = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(parsed),
                BorderBrush = new SolidColorBrush(Color.FromArgb(180, 128, 128, 128)),
                BorderThickness = new Thickness(1)
            };
            ToolTip.SetTip(button, colorHex);
            button.Click += (_, _) => SetColor(parsed);
            quick.Children.Add(button);
        }

        var use = new Button { Content = "Use Color", MinWidth = 92 };
        var cancel = new Button { Content = "Cancel", MinWidth = 86, Margin = new Thickness(8, 0, 0, 0) };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { use, cancel }
        };

        var top = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Children =
            {
                preview,
                new StackPanel
                {
                    Spacing = 5,
                    Children =
                    {
                        new NativeTextBlock { Text = "Hex color", FontWeight = FontWeight.SemiBold },
                        hex,
                        new NativeTextBlock { Text = "Choose visually or enter #RRGGBB.", Opacity = 0.68 }
                    }
                }
            }
        };

        var content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                new NativeTextBlock { Text = "Custom Color", FontSize = 19, FontWeight = FontWeight.SemiBold },
                top,
                new NativeTextBlock { Text = "Quick colors", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) },
                quick,
                SliderRow("Red", red, redValue),
                SliderRow("Green", green, greenValue),
                SliderRow("Blue", blue, blueValue),
                new Separator { Margin = new Thickness(0, 5) },
                actions
            }
        };

        var dialog = new Window
        {
            Title = "Select Custom Color",
            Width = 455,
            Height = 425,
            MinWidth = 430,
            MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content
        };
        use.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Opened += (_, _) => SetColor(initial);

        var accepted = await dialog.ShowDialog<bool>(owner);
        return accepted ? NormalizeHex(hex.Text) : null;
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        var normalized = NormalizeHex(value);
        if (normalized is null) return fallback;
        try { return Color.Parse(normalized); }
        catch { return fallback; }
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string? NormalizeHex(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 6 && text.All(Uri.IsHexDigit)) text = "#" + text;
        return IsHexColor(text) ? text.ToUpperInvariant() : null;
    }

    private static string FallbackColor(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "#64748B";
        unchecked
        {
            var hash = 17;
            foreach (var ch in label.Trim()) hash = (hash * 31) + ch;
            return FallbackPalette[(hash & int.MaxValue) % FallbackPalette.Length];
        }
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

    private static bool IsEditableExplorerNode(object node)
        => NodeRow(node) is not null || IsHeadingNode(node);

    private static bool IsHeadingNode(object node)
        => string.Equals(NodeKindName(node), "Heading", StringComparison.Ordinal);

    private static string NodeDescription(object node)
    {
        if (NodeRow(node) is { } row) return $"{row.Node.Kind} · {row.Node.Title}";
        var heading = NodeHeading(node);
        return heading is null ? "Heading" : $"Heading H{heading.Level} · {heading.Title}";
    }

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

    private static string HeadingLabelPath(string root)
        => Path.Combine(root, ".typescribe", "project-explorer-heading-labels.tsv");

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

        if (_outlinerSurface is not null) _outlinerSurface.LayoutUpdated -= OutlinerLayoutUpdated;
        if (_projectTree is not null)
        {
            _projectTree.LayoutUpdated -= ProjectTreeLayoutUpdated;
            _projectTree.RemoveHandler(InputElement.PointerPressedEvent, ProjectTreePointerPressed);
        }

        foreach (var cts in _pendingSaves.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _pendingSaves.Clear();
        _saveGate.Dispose();
    }

    private sealed record ColorChoice(string Name, string? Hex, bool IsCustom = false)
    {
        public override string ToString() => Name;
    }
}
