using System.Reflection;
using Avalonia;
using Avalonia.Controls;
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
/// Owns the editable Outliner surface and persists every editable cell directly to the
/// project model/store. The row host itself is used to detect active editing so docking or
/// floating the Outliner cannot cause a refresh to discard an in-progress edit.
/// </summary>
internal sealed class ReliableOutlinerFeature
{
    private static readonly NodeKind[] EditableKinds =
    [
        NodeKind.Chapter,
        NodeKind.Section,
        NodeKind.Scene,
        NodeKind.Research,
        NodeKind.Note
    ];

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
    private readonly FileSystemProjectRepository _repository = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Dictionary<string, string> _labelColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly NativeTextBox _filter = new()
    {
        Watermark = "Filter title, type, status or label",
        MinWidth = 260,
        Height = 30,
        Padding = new Thickness(8, 3),
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _title = new()
    {
        FontSize = 18,
        FontWeight = FontWeight.SemiBold,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _status = new()
    {
        Opacity = 0.68,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly StackPanel _rows = new() { Spacing = 1 };

    private AuthoringFeatureCoordinator? _features;
    private TabControl? _centerTabs;
    private TabItem? _outlinerTab;
    private string _sortKey = "title";
    private bool _sortAscending = true;
    private string? _fieldSignature;
    private string? _collectionSignature;
    private string? _labelProjectRoot;
    private bool _installed;
    private bool _refreshPending;
    private bool _disposed;

    private ReliableOutlinerFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        var feature = new ReliableOutlinerFeature(window, viewModel);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        viewModel.StateChanged += feature.OnStateChanged;
        feature.TryInstall();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        EnsureLabelColors();
        TryInstall();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!_installed)
        {
            TryInstall();
            return;
        }

        EnsureLabelColors();
        var fields = CustomFields();
        var fieldSignature = string.Join("|", fields.Select(static field => field.Key + ":" + field.Name));
        var collectionSignature = ActiveCollection()?.Id;
        var structureChanged =
            !string.Equals(fieldSignature, _fieldSignature, StringComparison.Ordinal) ||
            !string.Equals(collectionSignature, _collectionSignature, StringComparison.Ordinal);

        if (structureChanged)
        {
            _fieldSignature = fieldSignature;
            _collectionSignature = collectionSignature;
            RequestRefresh();
        }
        else if (_refreshPending && !IsEditing())
        {
            RefreshRows();
        }
    }

    private void TryInstall()
    {
        if (_installed || _disposed) return;
        _features = typeof(StudioWorkspaceWindow)
            .GetField("_features", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_window) as AuthoringFeatureCoordinator;
        if (_features is null) return;

        foreach (var tabs in _window.GetVisualDescendants().OfType<TabControl>())
        {
            var items = TabItems(tabs).ToArray();
            if (!items.Any(static item => HeaderEquals(item, "Editor")) ||
                !items.Any(static item => HeaderEquals(item, "Outliner")))
                continue;

            _centerTabs = tabs;
            _outlinerTab = items.First(static item => HeaderEquals(item, "Outliner"));
            _outlinerTab.Content = BuildSurface();
            _installed = true;
            _fieldSignature = string.Join("|", CustomFields().Select(static field => field.Key + ":" + field.Name));
            _collectionSignature = ActiveCollection()?.Id;
            EnsureLabelColors();
            RefreshRows();
            return;
        }
    }

    private Control BuildSurface()
    {
        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(10, 7),
            ColumnSpacing = 8
        };
        toolbar.Children.Add(_title);
        Grid.SetColumn(_filter, 1);
        toolbar.Children.Add(_filter);
        Grid.SetColumn(_status, 2);
        toolbar.Children.Add(_status);

        var scroll = new ScrollViewer
        {
            Content = _rows,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Classes.Add("reliable-outliner");
        root.Children.Add(toolbar);
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        _filter.TextChanged += (_, _) => RequestRefresh();
        return root;
    }

    private void OnStateChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(RequestRefresh, DispatcherPriority.Background);

    private bool IsEditing()
        => _rows.IsKeyboardFocusWithin || _filter.IsKeyboardFocusWithin;

    private void RequestRefresh()
    {
        if (_disposed || !_installed) return;
        if (IsEditing())
        {
            _refreshPending = true;
            return;
        }
        RefreshRows();
    }

    private void RefreshRows()
    {
        if (!_installed || _disposed || _features is null) return;
        _refreshPending = false;
        EnsureLabelColors();

        var fields = CustomFields();
        var nodes = SortedAndFilteredNodes(fields).ToArray();
        var active = ActiveCollection();
        _title.Text = active is null ? "Outliner — Manuscript" : $"Outliner — {active.Name}";
        _status.Text = $"{nodes.Length:N0} row{(nodes.Length == 1 ? string.Empty : "s")} · changes save automatically";

        _rows.Children.Clear();
        var columns = "280,110,140,230,80,100,82" + string.Concat(fields.Select(static _ => ",150"));
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), Margin = new Thickness(4, 2) };
        AddSortHeader(header, 0, "Title", "title");
        AddSortHeader(header, 1, "Type", "type");
        AddSortHeader(header, 2, "Status", "status");
        AddSortHeader(header, 3, "Label / Color", "label");
        AddSortHeader(header, 4, "Words", "words");
        AddSortHeader(header, 5, "Target", "target");
        AddSortHeader(header, 6, "Compile", "compile");
        for (var index = 0; index < fields.Length; index++)
            AddSortHeader(header, 7 + index, fields[index].Name, "custom:" + fields[index].Key);
        _rows.Children.Add(header);

        foreach (var node in nodes)
            _rows.Children.Add(BuildEditableRow(node, fields, columns));
    }

    private void AddSortHeader(Grid header, int column, string text, string key)
    {
        var active = string.Equals(_sortKey, key, StringComparison.OrdinalIgnoreCase);
        var button = new Button
        {
            Content = active ? $"{text} {(_sortAscending ? "↑" : "↓")}" : text,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(7, 4),
            Margin = new Thickness(1),
            FontWeight = FontWeight.SemiBold,
            MinHeight = 30
        };
        button.Click += (_, _) =>
        {
            if (string.Equals(_sortKey, key, StringComparison.OrdinalIgnoreCase))
                _sortAscending = !_sortAscending;
            else
            {
                _sortKey = key;
                _sortAscending = true;
            }
            RefreshRows();
        };
        Grid.SetColumn(button, column);
        header.Children.Add(button);
    }

    private Grid BuildEditableRow(ProjectNode node, CustomMetadataDefinition[] fields, string columns)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(columns),
            Margin = new Thickness(4, 1),
            MinHeight = 36
        };

        var title = CellTextBox(node.Title, "Document title");
        WireTextAutosave(title, value => CommitTitleAsync(node, value));
        Add(row, title, 0);

        var type = new ComboBox
        {
            ItemsSource = EditableKinds,
            SelectedItem = node.Kind,
            MinHeight = 32,
            Margin = new Thickness(1)
        };
        type.SelectionChanged += async (_, _) =>
        {
            if (type.SelectedItem is NodeKind kind && kind != node.Kind)
                await RunSaveAsync(() => CommitKindAsync(node, kind));
        };
        Add(row, type, 1);

        var status = CellTextBox(node.Status, "Draft / In Progress / Revised / Final");
        WireTextAutosave(status, value => CommitMetadataAsync(node, status: value));
        Add(row, status, 2);

        Add(row, BuildLabelCell(node), 3);

        var words = new TextBlock
        {
            Text = IndexedWords(node).ToString("N0"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0)
        };
        Add(row, words, 4);

        var target = CellTextBox(node.TargetWords > 0 ? node.TargetWords.ToString() : "0", "0");
        ToolTip.SetTip(target, "Word target · 0 means no target");
        WireTextAutosave(target, async value =>
        {
            if (int.TryParse(value, out var targetWords) && targetWords >= 0)
                await CommitMetadataAsync(node, target: targetWords);
        });
        target.LostFocus += (_, _) =>
        {
            if (!int.TryParse(target.Text, out var value) || value < 0)
                target.Text = node.TargetWords.ToString();
        };
        Add(row, target, 5);

        var compile = new CheckBox
        {
            IsChecked = node.IncludeInCompilation,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4)
        };
        ToolTip.SetTip(compile, "Include this document in compilation");
        compile.Click += async (_, _) =>
            await RunSaveAsync(() => CommitCompileAsync(node, compile.IsChecked == true));
        Add(row, compile, 6);

        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            var input = CellTextBox(node.CustomMetadata.GetValueOrDefault(field.Key), field.Name);
            WireTextAutosave(input, value => CommitCustomAsync(node, field.Key, value));
            Add(row, input, 7 + index);
        }

        row.DoubleTapped += async (_, e) =>
        {
            if (e.Source is NativeTextBox or ComboBox or CheckBox) return;
            await SelectNodeAsync(node);
        };
        return row;
    }

    private Control BuildLabelCell(ProjectNode node)
    {
        var label = CellTextBox(node.Label, "Label");
        WireTextAutosave(label, value => CommitMetadataAsync(node, label: value));

        var color = new ComboBox
        {
            ItemsSource = LabelColors,
            SelectedItem = ChoiceForLabel(node.Label),
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
        cell.Children.Add(label);
        Grid.SetColumn(color, 1);
        cell.Children.Add(color);

        var syncing = false;
        ApplyLabelTint(label);
        label.TextChanged += (_, _) =>
        {
            syncing = true;
            color.SelectedItem = ChoiceForLabel(label.Text);
            syncing = false;
            ApplyLabelTint(label);
        };
        color.SelectionChanged += (_, _) =>
        {
            if (syncing) return;
            var name = label.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name)) return;
            var choice = color.SelectedItem as LabelColorChoice ?? LabelColors[0];
            if (string.IsNullOrWhiteSpace(choice.Hex)) _labelColors.Remove(name);
            else _labelColors[name] = choice.Hex;
            SaveLabelColors();
            ApplyLabelTint(label);
        };
        return cell;
    }

    private static NativeTextBox CellTextBox(string? text, string watermark) => new()
    {
        Text = text ?? string.Empty,
        Watermark = watermark,
        MinHeight = 32,
        Margin = new Thickness(1),
        Padding = new Thickness(7, 4),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private void WireTextAutosave(NativeTextBox box, Func<string, Task> commit)
    {
        CancellationTokenSource? cts = null;

        box.TextChanged += (_, _) =>
        {
            if (_disposed || !box.IsKeyboardFocusWithin) return;
            cts?.Cancel();
            cts?.Dispose();
            cts = new CancellationTokenSource();
            var value = box.Text ?? string.Empty;
            _ = SaveAfterDelayAsync(commit, value, cts.Token);
        };

        box.LostFocus += async (_, _) =>
        {
            cts?.Cancel();
            cts?.Dispose();
            cts = null;
            await RunSaveAsync(() => commit(box.Text ?? string.Empty));
        };

        box.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            cts?.Cancel();
            cts?.Dispose();
            cts = null;
            await RunSaveAsync(() => commit(box.Text ?? string.Empty));
            _window.Focus();
        };
    }

    private async Task SaveAfterDelayAsync(Func<string, Task> commit, string value, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(220, cancellationToken);
            await RunSaveAsync(() => commit(value), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task RunSaveAsync(Func<Task> save, CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            await save();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            RaiseState($"Outliner save failed: {ex.Message}");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task CommitTitleAsync(ProjectNode node, string value)
    {
        value = value.Trim();
        if (value.Length == 0 || string.Equals(value, node.Title, StringComparison.Ordinal)) return;
        var project = CurrentProject();
        if (project is null) return;

        if (string.Equals(_viewModel.SelectedRow?.Node.PersistentId, node.PersistentId, StringComparison.Ordinal))
            await _viewModel.RenameSelectedAsync(value);
        else
            await _repository.RenameNodeAsync(project, node, value);

        RaiseState($"Outliner title saved: {value}");
    }

    private async Task CommitKindAsync(ProjectNode node, NodeKind kind)
    {
        var project = CurrentProject();
        if (project is null || node.Kind == kind) return;
        node.ChangeKind(kind);
        await _repository.SaveNodeMetadataAsync(
            project, node, node.Synopsis, node.Notes, node.Status, node.Label, node.Keywords, node.TargetWords);
        RaiseState($"Outliner type saved: {kind}");
    }

    private async Task CommitMetadataAsync(ProjectNode node, string? status = null, string? label = null, int? target = null)
    {
        var project = CurrentProject();
        if (project is null) return;

        var nextStatus = status ?? node.Status;
        var nextLabel = label ?? node.Label;
        var nextTarget = target ?? node.TargetWords;
        if (string.Equals(nextStatus, node.Status, StringComparison.Ordinal) &&
            string.Equals(nextLabel, node.Label, StringComparison.Ordinal) &&
            nextTarget == node.TargetWords)
            return;

        if (string.Equals(_viewModel.SelectedRow?.Node.PersistentId, node.PersistentId, StringComparison.Ordinal))
        {
            await _viewModel.SaveSelectedMetadataAsync(
                node.Synopsis,
                node.Notes,
                nextStatus,
                nextLabel,
                node.Keywords,
                nextTarget);
        }
        else
        {
            await _repository.SaveNodeMetadataAsync(
                project,
                node,
                node.Synopsis,
                node.Notes,
                nextStatus,
                nextLabel,
                node.Keywords,
                nextTarget);
        }

        RaiseState("Outliner metadata saved");
    }

    private async Task CommitCompileAsync(ProjectNode node, bool included)
    {
        var project = CurrentProject();
        if (project is null || node.IncludeInCompilation == included) return;
        await _repository.SetCompilationIncludedAsync(project, node, included);
        RaiseState(included ? "Included in compilation" : "Excluded from compilation");
    }

    private async Task CommitCustomAsync(ProjectNode node, string key, string value)
    {
        if (_features is null) return;
        value = value.Trim();
        if (string.Equals(node.CustomMetadata.GetValueOrDefault(key), value, StringComparison.Ordinal)) return;
        await _features.SetCustomValueAsync(node, key, value);
        RaiseState("Outliner custom metadata saved");
    }

    private void RaiseState(string status)
    {
        typeof(WorkspaceViewModel)
            .GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(_viewModel, [status]);
    }

    private async Task SelectNodeAsync(ProjectNode node)
    {
        var row = _viewModel.BinderRows.FirstOrDefault(candidate =>
            string.Equals(candidate.Node.PersistentId, node.PersistentId, StringComparison.Ordinal));
        if (row is not null) await _viewModel.SelectAsync(row);
        if (_centerTabs is not null) _centerTabs.SelectedIndex = 0;
    }

    private IEnumerable<ProjectNode> SortedAndFilteredNodes(CustomMetadataDefinition[] fields)
    {
        IEnumerable<ProjectNode> nodes = _viewModel.BinderRows
            .Select(static row => row.Node)
            .Where(static node => node.IsDocument);

        var active = ActiveCollection();
        if (active?.Kind == ProjectCollectionKind.Manual)
        {
            var wanted = active.NodePersistentIds.ToHashSet(StringComparer.Ordinal);
            nodes = nodes.Where(node => wanted.Contains(node.PersistentId));
        }

        var query = _filter.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(query))
        {
            nodes = nodes.Where(node =>
                Contains(node.Title, query) ||
                Contains(node.Kind.ToString(), query) ||
                Contains(node.Status, query) ||
                Contains(node.Label, query) ||
                fields.Any(field => Contains(node.CustomMetadata.GetValueOrDefault(field.Key), query)));
        }

        Func<ProjectNode, object?> selector = _sortKey switch
        {
            "type" => node => node.Kind.ToString(),
            "status" => node => node.Status,
            "label" => node => node.Label,
            "words" => node => IndexedWords(node),
            "target" => node => node.TargetWords,
            "compile" => node => node.IncludeInCompilation ? 1 : 0,
            _ when _sortKey.StartsWith("custom:", StringComparison.OrdinalIgnoreCase) =>
                node => node.CustomMetadata.GetValueOrDefault(_sortKey[7..]),
            _ => node => node.Title
        };

        var comparer = Comparer<object?>.Create(CompareValues);
        return _sortAscending
            ? nodes.OrderBy(selector, comparer).ThenBy(static node => node.Title, StringComparer.OrdinalIgnoreCase)
            : nodes.OrderByDescending(selector, comparer).ThenBy(static node => node.Title, StringComparer.OrdinalIgnoreCase);
    }

    private int IndexedWords(ProjectNode node)
        => _features?.GetIndexedWords(node, _viewModel.SelectedRow?.Node.PersistentId, _viewModel.WordCount) ?? 0;

    private CustomMetadataDefinition[] CustomFields()
        => _features?.CustomFields.ToArray() ?? [];

    private ProjectCollection? ActiveCollection()
        => typeof(StudioWorkspaceWindow)
            .GetField("_activeCollection", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_window) as ProjectCollection;

    private BookProject? CurrentProject()
        => typeof(WorkspaceViewModel)
            .GetField("_project", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_viewModel) as BookProject;

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
            File.WriteAllLines(
                path,
                _labelColors.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => pair.Key.Replace('\t', ' ') + "\t" + pair.Value));
        }
        catch { }
    }

    private LabelColorChoice ChoiceForLabel(string? label)
    {
        if (!string.IsNullOrWhiteSpace(label) && _labelColors.TryGetValue(label.Trim(), out var hex))
            return LabelColors.FirstOrDefault(choice =>
                string.Equals(choice.Hex, hex, StringComparison.OrdinalIgnoreCase)) ?? LabelColors[0];
        return LabelColors[0];
    }

    private void ApplyLabelTint(NativeTextBox box)
    {
        var label = box.Text?.Trim();
        if (string.IsNullOrWhiteSpace(label) || !_labelColors.TryGetValue(label, out var hex))
        {
            box.ClearValue(NativeTextBox.BackgroundProperty);
            box.ClearValue(NativeTextBox.BorderBrushProperty);
            box.ClearValue(NativeTextBox.BorderThicknessProperty);
            return;
        }

        var color = Color.Parse(hex);
        box.Background = new SolidColorBrush(Color.FromArgb(28, color.R, color.G, color.B));
        box.BorderBrush = new SolidColorBrush(color);
        box.BorderThickness = new Thickness(3, 1, 1, 1);
    }

    private static string LabelColorPath(string projectRoot)
        => Path.Combine(projectRoot, ".typescribe", "label-colors.tsv");

    private static bool IsHexColor(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length == 7 && value[0] == '#' &&
           value.AsSpan(1).ToArray().All(Uri.IsHexDigit);

    private static int CompareValues(object? left, object? right)
    {
        if (left is int leftInt && right is int rightInt) return leftInt.CompareTo(rightInt);
        return StringComparer.OrdinalIgnoreCase.Compare(left?.ToString() ?? string.Empty, right?.ToString() ?? string.Empty);
    }

    private static bool Contains(string? value, string query)
        => (value ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase);

    private static void Add(Grid grid, Control control, int column)
    {
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }

    private static bool HeaderEquals(TabItem item, string text)
        => string.Equals(item.Header?.ToString(), text, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is System.Collections.IEnumerable source)
            return source.Cast<object?>().OfType<TabItem>();
        return tabs.Items.OfType<TabItem>();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _viewModel.StateChanged -= OnStateChanged;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
    }

    private sealed record LabelColorChoice(string Name, string? Hex)
    {
        public override string ToString() => Name;
    }
}
