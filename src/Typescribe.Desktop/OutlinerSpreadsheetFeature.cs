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

namespace Typescribe.Desktop;

/// <summary>
/// Replaces the display-only manuscript Outliner with a sortable, directly editable grid while
/// keeping ProjectNode/Binder metadata as the source of truth.
/// </summary>
internal sealed class OutlinerSpreadsheetFeature
{
    private static readonly NodeKind[] EditableKinds =
    [
        NodeKind.Chapter,
        NodeKind.Section,
        NodeKind.Scene,
        NodeKind.Research,
        NodeKind.Note
    ];

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly FileSystemProjectRepository _repository = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly List<DispatcherTimer> _editTimers = [];
    private readonly TextBox _filter = new()
    {
        Watermark = "Filter rows",
        MinWidth = 180,
        Height = 28,
        VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Thickness(7, 2)
    };
    private readonly TextBlock _title = new()
    {
        FontSize = 18,
        FontWeight = FontWeight.SemiBold,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _status = new() { Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _rows = new() { Spacing = 1 };

    private AuthoringFeatureCoordinator? _features;
    private TabControl? _centerTabs;
    private TabItem? _outlinerTab;
    private string _sortKey = "title";
    private bool _sortAscending = true;
    private string? _fieldSignature;
    private string? _collectionSignature;
    private bool _installed;
    private bool _refreshPending;
    private bool _disposed;

    private OutlinerSpreadsheetFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        var feature = new OutlinerSpreadsheetFeature(window, viewModel);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        viewModel.StateChanged += feature.OnStateChanged;
        feature.TryInstall();
    }

    private void OnOpened(object? sender, EventArgs e) => TryInstall();

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!_installed)
        {
            TryInstall();
            return;
        }

        var fields = CustomFields();
        var fieldSignature = string.Join("|", fields.Select(static field => field.Key + ":" + field.Name));
        var collectionSignature = ActiveCollection()?.Id;
        if (!string.Equals(fieldSignature, _fieldSignature, StringComparison.Ordinal) ||
            !string.Equals(collectionSignature, _collectionSignature, StringComparison.Ordinal))
        {
            _fieldSignature = fieldSignature;
            _collectionSignature = collectionSignature;
            if (_outlinerTab?.IsKeyboardFocusWithin == true)
                _refreshPending = true;
            else
                RefreshRows();
        }
        else if (_refreshPending && _outlinerTab?.IsKeyboardFocusWithin != true)
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
        root.Children.Add(toolbar);
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        _filter.TextChanged += (_, _) => RequestRefresh();
        return root;
    }

    private void OnStateChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(RequestRefresh, DispatcherPriority.Background);

    private void RequestRefresh()
    {
        if (_disposed || !_installed) return;
        if (_outlinerTab?.IsKeyboardFocusWithin == true)
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
        StopEditTimers();
        var fields = CustomFields();
        var nodes = SortedAndFilteredNodes(fields).ToArray();
        var active = ActiveCollection();
        _title.Text = active is null ? "Outliner — Manuscript" : $"Outliner — {active.Name}";
        _status.Text = $"{nodes.Length:N0} row{(nodes.Length == 1 ? string.Empty : "s")}";

        _rows.Children.Clear();
        var columns = "260,100,130,130,90,105,85" + string.Concat(fields.Select(static _ => ",150"));
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), Margin = new Thickness(4, 2) };
        AddSortHeader(header, 0, "Title", "title");
        AddSortHeader(header, 1, "Type", "type");
        AddSortHeader(header, 2, "Status", "status");
        AddSortHeader(header, 3, "Label", "label");
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
            Padding = new Thickness(6, 3),
            Margin = new Thickness(1),
            FontWeight = FontWeight.SemiBold
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
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), Margin = new Thickness(4, 1) };

        var title = CellTextBox(node.Title);
        WireTextAutosave(title, value => CommitTitleAsync(node, value));
        Add(row, title, 0);

        var type = new ComboBox
        {
            ItemsSource = EditableKinds,
            SelectedItem = node.Kind,
            MinHeight = 28,
            Margin = new Thickness(1)
        };
        type.SelectionChanged += async (_, _) =>
        {
            if (type.SelectedItem is NodeKind kind && kind != node.Kind)
                await RunSaveAsync(() => CommitKindAsync(node, kind));
        };
        Add(row, type, 1);

        var status = CellTextBox(node.Status);
        WireTextAutosave(status, value => CommitMetadataAsync(node, status: value));
        Add(row, status, 2);

        var label = CellTextBox(node.Label);
        WireTextAutosave(label, value => CommitMetadataAsync(node, label: value));
        Add(row, label, 3);

        var words = new TextBlock
        {
            Text = IndexedWords(node).ToString("N0"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0)
        };
        Add(row, words, 4);

        var target = CellTextBox(node.TargetWords > 0 ? node.TargetWords.ToString() : "0");
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
        compile.Click += async (_, _) => await RunSaveAsync(() => CommitCompileAsync(node, compile.IsChecked == true));
        Add(row, compile, 6);

        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            var input = CellTextBox(node.CustomMetadata.GetValueOrDefault(field.Key));
            WireTextAutosave(input, value => CommitCustomAsync(node, field.Key, value));
            Add(row, input, 7 + index);
        }

        row.DoubleTapped += async (_, e) =>
        {
            if (e.Source is Avalonia.Controls.TextBox or ComboBox or CheckBox) return;
            await SelectNodeAsync(node);
        };
        return row;
    }

    private static TextBox CellTextBox(string? text) => new()
    {
        Text = text ?? string.Empty,
        MinHeight = 28,
        Margin = new Thickness(1),
        Padding = new Thickness(5, 2),
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private void WireTextAutosave(Avalonia.Controls.TextBox box, Func<string, Task> commit)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            var value = box.Text ?? string.Empty;
            await RunSaveAsync(() => commit(value));
        };
        _editTimers.Add(timer);

        box.TextChanged += (_, _) =>
        {
            if (_disposed) return;
            timer.Stop();
            timer.Start();
        };
        box.LostFocus += async (_, _) =>
        {
            timer.Stop();
            var value = box.Text ?? string.Empty;
            await RunSaveAsync(() => commit(value));
        };
        box.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            timer.Stop();
            var value = box.Text ?? string.Empty;
            await RunSaveAsync(() => commit(value));
            _window.Focus();
        };
    }

    private async Task RunSaveAsync(Func<Task> save)
    {
        if (_disposed) return;
        await _saveGate.WaitAsync();
        try
        {
            await save();
        }
        catch (Exception ex)
        {
            RaiseState($"Outliner save failed: {ex.Message}");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void StopEditTimers()
    {
        foreach (var timer in _editTimers) timer.Stop();
        _editTimers.Clear();
    }

    private async Task CommitTitleAsync(ProjectNode node, string value)
    {
        value = value.Trim();
        if (value.Length == 0 || string.Equals(value, node.Title, StringComparison.Ordinal)) return;
        var project = CurrentProject();
        if (project is null) return;

        if (ReferenceEquals(_viewModel.SelectedRow?.Node, node))
            await _viewModel.RenameSelectedAsync(value);
        else
            await _repository.RenameNodeAsync(project, node, value);
        RaiseState($"Renamed to {value}");
        FinishEdit();
    }

    private async Task CommitKindAsync(ProjectNode node, NodeKind kind)
    {
        var project = CurrentProject();
        if (project is null || node.Kind == kind) return;
        node.ChangeKind(kind);
        await _repository.SaveNodeMetadataAsync(
            project, node, node.Synopsis, node.Notes, node.Status, node.Label, node.Keywords, node.TargetWords);
        RaiseState($"Changed type to {kind}");
        FinishEdit();
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

        if (ReferenceEquals(_viewModel.SelectedRow?.Node, node))
        {
            await _viewModel.SaveSelectedMetadataAsync(
                node.Synopsis, node.Notes, nextStatus, nextLabel, node.Keywords, nextTarget);
        }
        else
        {
            await _repository.SaveNodeMetadataAsync(
                project, node, node.Synopsis, node.Notes, nextStatus, nextLabel, node.Keywords, nextTarget);
        }
        RaiseState("Outliner metadata saved");
        FinishEdit();
    }

    private async Task CommitCompileAsync(ProjectNode node, bool included)
    {
        var project = CurrentProject();
        if (project is null || node.IncludeInCompilation == included) return;
        await _repository.SetCompilationIncludedAsync(project, node, included);
        RaiseState(included ? "Included in compilation" : "Excluded from compilation");
        FinishEdit();
    }

    private async Task CommitCustomAsync(ProjectNode node, string key, string value)
    {
        if (_features is null || string.Equals(node.CustomMetadata.GetValueOrDefault(key), value, StringComparison.Ordinal)) return;
        await _features.SetCustomValueAsync(node, key, value);
        RaiseState("Custom metadata saved");
        FinishEdit();
    }

    private void FinishEdit()
    {
        if (_outlinerTab?.IsKeyboardFocusWithin == true)
        {
            _refreshPending = true;
            return;
        }
        Dispatcher.UIThread.Post(RefreshRows, DispatcherPriority.Background);
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
                Contains(node.Title, query) || Contains(node.Kind.ToString(), query) ||
                Contains(node.Status, query) || Contains(node.Label, query) ||
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

    private static int CompareValues(object? left, object? right)
    {
        if (left is int li && right is int ri) return li.CompareTo(ri);
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
        => string.Equals(item.Header?.ToString(), text, StringComparison.Ordinal);

    private static IEnumerable<TabItem> TabItems(TabControl tabs)
    {
        if (tabs.ItemsSource is System.Collections.IEnumerable source)
            return source.Cast<object?>().OfType<TabItem>();
        return tabs.Items.OfType<TabItem>();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        StopEditTimers();
        _viewModel.StateChanged -= OnStateChanged;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
    }
}
