using System.Collections;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
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
/// Completes label editing across the Outliner and Project Explorer. Labels are editable
/// combo boxes populated from labels already used by the project. Colors may be selected from
/// the shared palette or entered as a custom #RRGGBB value. A color can also be attached to an
/// item that has no label, in which case it is stored by persistent project/heading key.
/// </summary>
internal sealed class UnifiedLabelEditingFeature
{
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
    private readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private readonly HashSet<Grid> _outlinerCells = [];
    private readonly HashSet<Window> _dialogs = [];
    private readonly Dictionary<Grid, ProjectNode> _outlinerNodes = [];
    private readonly Dictionary<ComboBox, CancellationTokenSource> _pendingLabelSaves = [];
    private readonly Dictionary<string, string> _labelColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _itemColors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _headingLabels = new(StringComparer.Ordinal);

    private TreeView? _projectTree;
    private string? _projectRoot;
    private DateTime _labelColorStampUtc;
    private DateTime _itemColorStampUtc;
    private DateTime _headingLabelStampUtc;
    private bool _disposed;

    private UnifiedLabelEditingFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
        _scanTimer.Tick += (_, _) => Scan();
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var feature = new UnifiedLabelEditingFeature(window, viewModel);
        window.Opened += feature.WindowOpened;
        window.Closed += feature.WindowClosed;
        viewModel.StateChanged += feature.ViewModelStateChanged;
        feature.Scan();
    }

    private void WindowOpened(object? sender, EventArgs e)
    {
        Scan();
        _scanTimer.Start();
    }

    private void ViewModelStateChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(Scan, DispatcherPriority.Background);

    private void Scan()
    {
        if (_disposed) return;
        EnsureProjectState();
        RefreshExternalState();
        FindProjectTree();
        EnhanceOutliner();
        EnhanceProjectExplorer();
        EnhanceLabelDialogs();
    }

    private void FindProjectTree()
    {
        _projectTree = _window.GetVisualDescendants()
            .OfType<TreeView>()
            .FirstOrDefault(static tree => tree.Classes.Contains("project-explorer-tree"));
    }

    private void EnhanceOutliner()
    {
        var surface = _window.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(static grid => grid.Classes.Contains("reliable-outliner"));
        if (surface is null) return;

        foreach (var grid in surface.GetVisualDescendants().OfType<Grid>())
        {
            if (grid.ColumnDefinitions.Count >= 7)
                grid.ColumnDefinitions[3].Width = new GridLength(315);
        }

        var usedNodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cell in surface.GetVisualDescendants()
                     .OfType<Grid>()
                     .Where(static grid => grid.Classes.Contains("ux-outliner-label-cell"))
                     .ToArray())
        {
            if (_outlinerCells.Contains(cell))
            {
                if (_outlinerNodes.TryGetValue(cell, out var known))
                    usedNodes.Add(known.PersistentId);
                continue;
            }

            var node = ResolveOutlinerNode(cell, usedNodes);
            if (node is null) continue;
            usedNodes.Add(node.PersistentId);
            UpgradeOutlinerCell(cell, node);
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

        var labels = ExistingLabels();
        var label = new ComboBox
        {
            IsEditable = true,
            ItemsSource = labels,
            Text = node.Label,
            PlaceholderText = "Label",
            MinHeight = 32,
            Margin = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(label, "Type a new label or choose an existing project label");

        var explicitColor = ExplicitColorForNode(node.PersistentId, node.Label);
        var color = new ComboBox
        {
            ItemsSource = ColorChoices,
            SelectedItem = ChoiceForHex(explicitColor),
            MinWidth = 92,
            Height = 32,
            Margin = new Thickness(2, 1),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(color, "Choose a preset color or Custom");

        var custom = new NativeTextBox
        {
            Text = IsCustomHex(explicitColor) ? explicitColor : string.Empty,
            Watermark = "#RRGGBB",
            MinWidth = 76,
            Height = 32,
            MaxLength = 7,
            Margin = new Thickness(2, 1, 1, 1),
            Padding = new Thickness(5, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
            IsVisible = IsCustomHex(explicitColor)
        };
        ToolTip.SetTip(custom, "Custom color in #RRGGBB format");

        cell.Children.Remove(oldLabel);
        cell.Children.Remove(oldColor);
        cell.ColumnDefinitions = new ColumnDefinitions("*,96,82");
        cell.Children.Add(label);
        Grid.SetColumn(color, 1);
        cell.Children.Add(color);
        Grid.SetColumn(custom, 2);
        cell.Children.Add(custom);

        _outlinerCells.Add(cell);
        _outlinerNodes[cell] = node;

        var syncing = false;
        void RefreshAppearance()
        {
            if (syncing) return;
            var text = label.Text?.Trim() ?? string.Empty;
            var choice = color.SelectedItem as ColorChoice ?? ColorChoices[0];
            custom.IsVisible = choice.IsCustom;
            var hex = EffectiveColor(node.PersistentId, text, choice, custom.Text);
            ApplyComboTint(label, text, hex);
        }

        label.PropertyChanged += (_, e) =>
        {
            if (e.Property != ComboBox.TextProperty || syncing) return;
            var text = label.Text?.Trim() ?? string.Empty;
            var known = ExplicitColorForNode(node.PersistentId, text);
            if (!string.IsNullOrWhiteSpace(known))
            {
                syncing = true;
                color.SelectedItem = ChoiceForHex(known);
                custom.Text = IsCustomHex(known) ? known : string.Empty;
                syncing = false;
            }
            RefreshAppearance();
            ScheduleOutlinerSave(label, node, color, custom);
        };
        label.SelectionChanged += (_, _) =>
        {
            if (label.SelectedItem is string selected)
                label.Text = selected;
        };
        label.LostFocus += async (_, _) => await SaveOutlinerLabelAsync(node, label, color, custom);
        label.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await SaveOutlinerLabelAsync(node, label, color, custom);
        };

        color.SelectionChanged += (_, _) =>
        {
            if (syncing) return;
            var choice = color.SelectedItem as ColorChoice ?? ColorChoices[0];
            custom.IsVisible = choice.IsCustom;
            if (choice.IsCustom && string.IsNullOrWhiteSpace(custom.Text))
                custom.Text = ExplicitColorForNode(node.PersistentId, label.Text) ?? "#64748B";
            RefreshAppearance();
            ScheduleOutlinerSave(label, node, color, custom);
        };
        custom.TextChanged += (_, _) =>
        {
            if (syncing) return;
            if (!string.IsNullOrWhiteSpace(custom.Text) && !IsHexColor(custom.Text))
            {
                custom.BorderBrush = Brushes.IndianRed;
                return;
            }
            custom.ClearValue(NativeTextBox.BorderBrushProperty);
            RefreshAppearance();
            ScheduleOutlinerSave(label, node, color, custom);
        };
        custom.LostFocus += async (_, _) => await SaveOutlinerLabelAsync(node, label, color, custom);

        RefreshAppearance();
    }

    private void ScheduleOutlinerSave(ComboBox label, ProjectNode node, ComboBox color, NativeTextBox custom)
    {
        if (_pendingLabelSaves.Remove(label, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        var cts = new CancellationTokenSource();
        _pendingLabelSaves[label] = cts;
        _ = SaveAfterDelayAsync(label, node, color, custom, cts.Token);
    }

    private async Task SaveAfterDelayAsync(
        ComboBox label,
        ProjectNode node,
        ComboBox color,
        NativeTextBox custom,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(260, cancellationToken);
            await SaveOutlinerLabelAsync(node, label, color, custom, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task SaveOutlinerLabelAsync(
        ProjectNode node,
        ComboBox label,
        ComboBox color,
        NativeTextBox custom,
        CancellationToken cancellationToken = default)
    {
        var project = CurrentProject();
        if (project is null || _disposed) return;

        var value = label.Text?.Trim() ?? string.Empty;
        var choice = color.SelectedItem as ColorChoice ?? ColorChoices[0];
        var explicitHex = ExplicitHex(choice, custom.Text);
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
            PersistColor(node.PersistentId, value, explicitHex);
        }
        finally
        {
            _saveGate.Release();
        }

        RefreshHierarchyAppearance();
    }

    private void EnhanceProjectExplorer()
    {
        if (_projectTree is null) return;

        foreach (var item in _projectTree.GetVisualDescendants().OfType<TreeViewItem>().ToArray())
        {
            if (item.DataContext is not { } node || !IsEditableExplorerNode(node)) continue;

            var badge = item.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(static button => button.Classes.Contains("project-hierarchy-label-badge"));
            if (badge is not null && !badge.Classes.Contains("unified-label-selection-hook"))
            {
                badge.Classes.Add("unified-label-selection-hook");
                badge.AddHandler(InputElement.PointerPressedEvent, (_, _) =>
                {
                    if (_projectTree is not null) _projectTree.SelectedItem = node;
                }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            }

            ApplyHierarchyAppearance(item, node);
        }
    }

    private void ApplyHierarchyAppearance(TreeViewItem item, object node, string? previewLabel = null, string? previewHex = null)
    {
        var header = item.GetVisualDescendants().OfType<Grid>()
            .FirstOrDefault(static grid => grid.GetType().Name.Contains("ExplorerNodeHeader", StringComparison.Ordinal));
        if (header is null) return;

        var label = previewLabel ?? LabelForExplorerNode(node);
        var key = ExplorerStorageKey(node);
        var explicitHex = previewHex ?? ExplicitColorForNode(key, label);
        var displayHex = explicitHex ?? (string.IsNullOrWhiteSpace(label) ? null : FallbackColor(label));

        var badge = header.Children.OfType<Button>()
            .FirstOrDefault(static button => button.Classes.Contains("project-hierarchy-label-badge"));
        if (badge?.Content is TextBlock text)
        {
            if (string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(displayHex))
            {
                var parsed = Color.Parse(displayHex);
                text.Text = "●";
                text.Foreground = new SolidColorBrush(parsed);
                badge.Background = new SolidColorBrush(Color.FromArgb(28, parsed.R, parsed.G, parsed.B));
                badge.BorderBrush = new SolidColorBrush(Color.FromArgb(180, parsed.R, parsed.G, parsed.B));
                badge.Opacity = 1;
                ToolTip.SetTip(badge, "Color only · click to edit label / color");
            }
            else if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(displayHex))
            {
                var parsed = Color.Parse(displayHex);
                text.Text = label.Trim();
                text.Foreground = new SolidColorBrush(parsed);
                badge.Background = new SolidColorBrush(Color.FromArgb(34, parsed.R, parsed.G, parsed.B));
                badge.BorderBrush = new SolidColorBrush(Color.FromArgb(190, parsed.R, parsed.G, parsed.B));
                badge.Opacity = 1;
            }
        }

        var accent = header.Children.OfType<Border>()
            .FirstOrDefault(static border => Grid.GetColumn(border) == 0);
        if (accent is not null)
            accent.Background = string.IsNullOrWhiteSpace(displayHex)
                ? Brushes.Transparent
                : new SolidColorBrush(Color.Parse(displayHex));
    }

    private void EnhanceLabelDialogs()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        foreach (var dialog in desktop.Windows.ToArray())
        {
            if (_dialogs.Contains(dialog) || !dialog.IsVisible ||
                !string.Equals(dialog.Title, "Label & Color — Project Explorer", StringComparison.Ordinal))
                continue;

            UpgradeLabelDialog(dialog);
        }
    }

    private void UpgradeLabelDialog(Window dialog)
    {
        var targetNode = _projectTree?.SelectedItem;
        if (targetNode is null || !IsEditableExplorerNode(targetNode)) return;

        var oldLabel = dialog.GetVisualDescendants().OfType<NativeTextBox>().FirstOrDefault();
        var oldColor = dialog.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault();
        if (oldLabel?.Parent is not StackPanel parent || oldColor is null) return;

        var index = parent.Children.IndexOf(oldLabel);
        if (index < 0) return;

        var label = new ComboBox
        {
            IsEditable = true,
            ItemsSource = ExistingLabels(),
            Text = oldLabel.Text,
            PlaceholderText = "Type or select a label",
            MinHeight = 34,
            MinWidth = 280,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ToolTip.SetTip(label, "Type a new label or choose one already used in this project");

        parent.Children.RemoveAt(index);
        oldLabel.IsVisible = false;
        parent.Children.Insert(index, label);

        var key = ExplorerStorageKey(targetNode);
        var currentLabel = oldLabel.Text?.Trim() ?? string.Empty;
        var currentExplicit = ExplicitColorForNode(key, currentLabel);
        var custom = new NativeTextBox
        {
            Text = IsCustomHex(currentExplicit) ? currentExplicit : string.Empty,
            Watermark = "#RRGGBB",
            MaxLength = 7,
            MinHeight = 34,
            MinWidth = 120,
            Padding = new Thickness(8, 4),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        ToolTip.SetTip(custom, "Optional custom color. Leave blank to use the selected preset/Automatic color.");

        var colorLabel = parent.Children.OfType<TextBlock>()
            .FirstOrDefault(static text => string.Equals(text.Text, "Color", StringComparison.Ordinal));
        if (colorLabel is not null)
        {
            var colorIndex = parent.Children.IndexOf(colorLabel);
            parent.Children.Insert(Math.Min(parent.Children.Count, colorIndex + 2),
                new TextBlock
                {
                    Text = "Custom color (#RRGGBB)",
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            parent.Children.Insert(Math.Min(parent.Children.Count, colorIndex + 3), custom);
            dialog.Height = Math.Max(dialog.Height, 415);
            dialog.MinHeight = Math.Max(dialog.MinHeight, 390);
        }

        void Preview()
        {
            oldLabel.Text = label.Text ?? string.Empty;
            var selectedHex = HexFromExistingColorChoice(oldColor.SelectedItem);
            var hex = IsHexColor(custom.Text) ? custom.Text!.ToUpperInvariant() : selectedHex;
            var display = hex ?? (!string.IsNullOrWhiteSpace(label.Text) ? FallbackColor(label.Text!) : null);
            PreviewExplorerNode(targetNode, label.Text?.Trim() ?? string.Empty, display);
            if (!string.IsNullOrWhiteSpace(custom.Text) && !IsHexColor(custom.Text))
                custom.BorderBrush = Brushes.IndianRed;
            else
                custom.ClearValue(NativeTextBox.BorderBrushProperty);
        }

        label.PropertyChanged += (_, e) =>
        {
            if (e.Property == ComboBox.TextProperty) Preview();
        };
        label.SelectionChanged += (_, _) =>
        {
            if (label.SelectedItem is string selected) label.Text = selected;
        };
        oldColor.SelectionChanged += (_, _) =>
        {
            var hex = HexFromExistingColorChoice(oldColor.SelectedItem);
            if (!string.IsNullOrWhiteSpace(hex)) custom.Text = string.Empty;
            Preview();
        };
        custom.TextChanged += (_, _) => Preview();

        var buttons = dialog.GetVisualDescendants().OfType<Button>().ToArray();
        var done = buttons.FirstOrDefault(static button => string.Equals(button.Content?.ToString(), "Done", StringComparison.Ordinal));
        var cancel = buttons.FirstOrDefault(static button => string.Equals(button.Content?.ToString(), "Cancel", StringComparison.Ordinal));
        if (done is not null)
        {
            done.Click += (_, _) =>
            {
                oldLabel.Text = label.Text ?? string.Empty;
                var chosen = IsHexColor(custom.Text)
                    ? custom.Text!.ToUpperInvariant()
                    : HexFromExistingColorChoice(oldColor.SelectedItem);
                PersistColor(key, label.Text?.Trim() ?? string.Empty, chosen);
                Dispatcher.UIThread.Post(RefreshHierarchyAppearance, DispatcherPriority.Background);
            };
        }
        if (cancel is not null)
            cancel.Click += (_, _) => Dispatcher.UIThread.Post(RefreshHierarchyAppearance, DispatcherPriority.Background);

        _dialogs.Add(dialog);
        Preview();
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
            ApplyHierarchyAppearance(item, candidate, label, hex);
        }
    }

    private void RefreshHierarchyAppearance()
    {
        if (_projectTree is null) return;
        RefreshExternalState();
        foreach (var item in _projectTree.GetVisualDescendants().OfType<TreeViewItem>().ToArray())
            if (item.DataContext is { } node && IsEditableExplorerNode(node))
                ApplyHierarchyAppearance(item, node);
    }

    private string[] ExistingLabels()
    {
        LoadHeadingLabelsIfNeeded();
        return _viewModel.BinderRows
            .Select(static row => row.Node.Label)
            .Concat(_headingLabels.Values)
            .Concat(_labelColors.Keys)
            .Where(static label => !string.IsNullOrWhiteSpace(label))
            .Select(static label => label.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static label => label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void PersistColor(string storageKey, string label, string? explicitHex)
    {
        label = label.Trim();
        explicitHex = IsHexColor(explicitHex) ? explicitHex!.ToUpperInvariant() : null;

        if (label.Length == 0)
        {
            if (explicitHex is null) _itemColors.Remove(storageKey);
            else _itemColors[storageKey] = explicitHex;
            SaveItemColors();
            return;
        }

        _itemColors.Remove(storageKey);
        SaveItemColors();
        if (explicitHex is null) _labelColors.Remove(label);
        else _labelColors[label] = explicitHex;
        SaveLabelColors();
    }

    private string? ExplicitColorForNode(string storageKey, string? label)
    {
        label = label?.Trim() ?? string.Empty;
        if (label.Length == 0)
            return _itemColors.GetValueOrDefault(storageKey);
        return _labelColors.GetValueOrDefault(label);
    }

    private string? EffectiveColor(string storageKey, string label, ColorChoice choice, string? customText)
    {
        var explicitHex = ExplicitHex(choice, customText);
        if (explicitHex is not null) return explicitHex;
        if (choice.IsCustom) return null;
        var stored = ExplicitColorForNode(storageKey, label);
        if (!string.IsNullOrWhiteSpace(stored)) return stored;
        return string.IsNullOrWhiteSpace(label) ? null : FallbackColor(label);
    }

    private static string? ExplicitHex(ColorChoice choice, string? customText)
    {
        if (choice.IsCustom)
            return IsHexColor(customText) ? customText!.ToUpperInvariant() : null;
        return choice.Hex;
    }

    private static void ApplyComboTint(ComboBox combo, string label, string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            combo.ClearValue(ComboBox.BackgroundProperty);
            combo.ClearValue(ComboBox.BorderBrushProperty);
            combo.ClearValue(ComboBox.BorderThicknessProperty);
            return;
        }
        var color = Color.Parse(hex);
        combo.Background = new SolidColorBrush(Color.FromArgb(24, color.R, color.G, color.B));
        combo.BorderBrush = new SolidColorBrush(color);
        combo.BorderThickness = new Thickness(3, 1, 1, 1);
        ToolTip.SetTip(combo, string.IsNullOrWhiteSpace(label) ? "Color-only item" : $"Label: {label}");
    }

    private ColorChoice ChoiceForHex(string? hex)
    {
        if (!IsHexColor(hex)) return ColorChoices[0];
        return ColorChoices.FirstOrDefault(choice =>
                   !choice.IsCustom && string.Equals(choice.Hex, hex, StringComparison.OrdinalIgnoreCase))
               ?? ColorChoices[^1];
    }

    private static bool IsCustomHex(string? hex)
        => IsHexColor(hex) && !ColorChoices.Any(choice =>
            !choice.IsCustom && string.Equals(choice.Hex, hex, StringComparison.OrdinalIgnoreCase));

    private static string? HexFromExistingColorChoice(object? choice)
    {
        if (choice is null) return null;
        return choice.GetType().GetProperty("Hex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(choice)?.ToString() is { } hex && IsHexColor(hex)
            ? hex.ToUpperInvariant()
            : null;
    }

    private string LabelForExplorerNode(object node)
    {
        if (NodeRow(node) is { } row) return row.Node.Label;
        var key = NodeKey(node);
        return key is null ? string.Empty : _headingLabels.GetValueOrDefault(key, string.Empty);
    }

    private static string ExplorerStorageKey(object node)
    {
        if (NodeRow(node) is { } row) return "node:" + row.Node.PersistentId;
        return NodeKey(node) is { Length: > 0 } key ? key : string.Empty;
    }

    private static BinderRowViewModel? NodeRow(object node)
        => node.GetType().GetProperty("Row", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(node) as BinderRowViewModel;

    private static string? NodeKey(object node)
        => node.GetType().GetProperty("Key", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(node)?.ToString();

    private static bool IsEditableExplorerNode(object node)
    {
        if (NodeRow(node) is not null) return true;
        return string.Equals(
            node.GetType().GetProperty("Kind", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(node)?.ToString(),
            "Heading",
            StringComparison.Ordinal);
    }

    private BookProject? CurrentProject()
        => typeof(WorkspaceViewModel)
            .GetField("_project", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_viewModel) as BookProject;

    private void EnsureProjectState()
    {
        var root = CurrentProject()?.RootPath;
        if (string.Equals(root, _projectRoot, StringComparison.Ordinal)) return;

        _projectRoot = root;
        _labelColors.Clear();
        _itemColors.Clear();
        _headingLabels.Clear();
        _labelColorStampUtc = default;
        _itemColorStampUtc = default;
        _headingLabelStampUtc = default;
        if (root is null) return;
        LoadLabelColors();
        LoadItemColors();
        LoadHeadingLabels();
    }

    private void RefreshExternalState()
    {
        if (_projectRoot is null) return;

        var labelPath = LabelColorPath(_projectRoot);
        var labelStamp = File.Exists(labelPath) ? File.GetLastWriteTimeUtc(labelPath) : default;
        if (labelStamp != _labelColorStampUtc) LoadLabelColors();

        var itemPath = ItemColorPath(_projectRoot);
        var itemStamp = File.Exists(itemPath) ? File.GetLastWriteTimeUtc(itemPath) : default;
        if (itemStamp != _itemColorStampUtc) LoadItemColors();

        var headingPath = HeadingLabelPath(_projectRoot);
        var headingStamp = File.Exists(headingPath) ? File.GetLastWriteTimeUtc(headingPath) : default;
        if (headingStamp != _headingLabelStampUtc) LoadHeadingLabels();
    }

    private void LoadLabelColors()
    {
        _labelColors.Clear();
        if (_projectRoot is null) return;
        var path = LabelColorPath(_projectRoot);
        if (!File.Exists(path)) { _labelColorStampUtc = default; return; }
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split('\t', 2);
                if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && IsHexColor(parts[1]))
                    _labelColors[parts[0]] = parts[1].ToUpperInvariant();
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
            File.WriteAllLines(path, _labelColors
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => pair.Key.Replace('\t', ' ') + "\t" + pair.Value));
            _labelColorStampUtc = File.GetLastWriteTimeUtc(path);
        }
        catch { }
    }

    private void LoadItemColors()
    {
        _itemColors.Clear();
        if (_projectRoot is null) return;
        var path = ItemColorPath(_projectRoot);
        if (!File.Exists(path)) { _itemColorStampUtc = default; return; }
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split('\t', 2);
                if (parts.Length != 2 || !IsHexColor(parts[1])) continue;
                var key = Decode(parts[0]);
                if (!string.IsNullOrWhiteSpace(key)) _itemColors[key] = parts[1].ToUpperInvariant();
            }
            _itemColorStampUtc = File.GetLastWriteTimeUtc(path);
        }
        catch { }
    }

    private void SaveItemColors()
    {
        if (_projectRoot is null) return;
        try
        {
            var path = ItemColorPath(_projectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, _itemColors
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => Encode(pair.Key) + "\t" + pair.Value));
            _itemColorStampUtc = File.GetLastWriteTimeUtc(path);
        }
        catch { }
    }

    private void LoadHeadingLabelsIfNeeded()
    {
        if (_projectRoot is null) return;
        var path = HeadingLabelPath(_projectRoot);
        var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
        if (stamp != _headingLabelStampUtc) LoadHeadingLabels();
    }

    private void LoadHeadingLabels()
    {
        _headingLabels.Clear();
        if (_projectRoot is null) return;
        var path = HeadingLabelPath(_projectRoot);
        if (!File.Exists(path)) { _headingLabelStampUtc = default; return; }
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

    private static string FallbackColor(string label)
    {
        unchecked
        {
            var hash = 17;
            foreach (var ch in label.Trim()) hash = (hash * 31) + ch;
            return FallbackPalette[(hash & int.MaxValue) % FallbackPalette.Length];
        }
    }

    private static string LabelColorPath(string root)
        => Path.Combine(root, ".typescribe", "label-colors.tsv");

    private static string ItemColorPath(string root)
        => Path.Combine(root, ".typescribe", "project-item-colors.tsv");

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
        _scanTimer.Stop();
        _scanTimer.Tick -= (_, _) => Scan();
        _window.Opened -= WindowOpened;
        _window.Closed -= WindowClosed;
        _viewModel.StateChanged -= ViewModelStateChanged;
        foreach (var cts in _pendingLabelSaves.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _pendingLabelSaves.Clear();
        _saveGate.Dispose();
    }

    private sealed record ColorChoice(string Name, string? Hex, bool IsCustom = false)
    {
        public override string ToString() => Name;
    }
}
