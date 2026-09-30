using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;
using Typescribe.Infrastructure.Services;

namespace Typescribe.Desktop;

/// <summary>
/// Upgrades snapshot history with a two-column paragraph comparison and selective paragraph
/// restore while preserving the existing full-restore safety snapshot behavior.
/// </summary>
internal sealed class SnapshotHistoryFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly FileSystemProjectRepository _repository = new();
    private readonly ListBox _list = new();
    private readonly TextBlock _summary = new() { Opacity = 0.68, VerticalAlignment = VerticalAlignment.Center };
    private TabItem? _snapshotsTab;
    private string? _selectedSnapshotId;
    private bool _installed;
    private bool _disposed;

    private SnapshotHistoryFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        var feature = new SnapshotHistoryFeature(window, viewModel);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        viewModel.StateChanged += feature.OnStateChanged;
        feature.TryInstall();
    }

    private void OnOpened(object? sender, EventArgs e) => TryInstall();
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_installed) TryInstall();
    }

    private void TryInstall()
    {
        if (_installed || _disposed) return;
        foreach (var tabs in _window.GetVisualDescendants().OfType<TabControl>())
        {
            var items = TabItems(tabs).ToArray();
            var snapshot = items.FirstOrDefault(static item => HeaderEquals(item, "Snapshots"));
            if (snapshot is null) continue;
            _snapshotsTab = snapshot;
            snapshot.Content = BuildSurface();
            _installed = true;
            Refresh();
            return;
        }
    }

    private Control BuildSurface()
    {
        var take = Button("Take Snapshot", TakeSnapshotAsync);
        var compare = Button("Compare", CompareAsync);
        var restore = Button("Restore Full", RestoreFullAsync);
        var delete = Button("Delete", DeleteAsync);

        var actions = new WrapPanel
        {
            Margin = new Thickness(8),
            Orientation = Orientation.Horizontal,
            Children = { take, compare, restore, delete }
        };
        compare.Margin = restore.Margin = delete.Margin = new Thickness(6, 0, 0, 0);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(actions);
        Grid.SetColumn(_summary, 1);
        _summary.Margin = new Thickness(8);
        header.Children.Add(_summary);

        _list.DoubleTapped += async (_, _) => await CompareAsync();
        _list.SelectionChanged += (_, _) =>
        {
            _selectedSnapshotId = Selected()?.Id;
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(header);
        Grid.SetRow(_list, 1);
        root.Children.Add(_list);
        return root;
    }

    private static Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text, MinHeight = 28 };
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch { }
        };
        return button;
    }

    private void OnStateChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);

    private void Refresh()
    {
        if (!_installed || _disposed) return;
        var selectedId = _selectedSnapshotId ?? Selected()?.Id;
        var items = _viewModel.Snapshots
            .Select(static snapshot => new SnapshotRow(snapshot))
            .ToArray();
        _list.ItemsSource = items;
        _summary.Text = $"{items.Length:N0} snapshot{(items.Length == 1 ? string.Empty : "s")}";
        if (selectedId is not null)
        {
            var index = Array.FindIndex(items, item => string.Equals(item.Snapshot.Id, selectedId, StringComparison.Ordinal));
            if (index >= 0) _list.SelectedIndex = index;
        }
        if (_list.SelectedIndex < 0 && items.Length > 0) _list.SelectedIndex = 0;
    }

    private SnapshotInfo? Selected()
        => _list.SelectedItem is SnapshotRow row ? row.Snapshot : null;

    private async Task TakeSnapshotAsync()
    {
        if (!_viewModel.HasDocument) return;
        var label = await DesktopDialogService.PromptAsync(
            _window,
            "Take Snapshot",
            "Snapshot label",
            $"Snapshot {DateTime.Now:g}");
        if (label is null) return;
        var snapshot = await _viewModel.CreateSnapshotAsync(label);
        _selectedSnapshotId = snapshot?.Id;
        Refresh();
    }

    private async Task CompareAsync()
    {
        var snapshot = Selected();
        if (snapshot is null || !_viewModel.HasDocument) return;
        var snapshotText = await ReadSnapshotAsync(snapshot);
        await ShowParagraphDiffAsync(snapshot, snapshotText, _viewModel.EditorText);
    }

    private async Task RestoreFullAsync()
    {
        var snapshot = Selected();
        if (snapshot is null) return;
        var confirmed = await DesktopDialogService.ConfirmAsync(
            _window,
            "Restore Snapshot",
            $"Restore '{snapshot.Label}' from {snapshot.CreatedAt.LocalDateTime:g}? Current text is snapshotted first.",
            "Restore");
        if (!confirmed) return;
        await _viewModel.RestoreSnapshotAsync(snapshot);
    }

    private async Task DeleteAsync()
    {
        var snapshot = Selected();
        if (snapshot is null) return;
        var confirmed = await DesktopDialogService.ConfirmAsync(
            _window,
            "Delete Snapshot",
            $"Delete '{snapshot.Label}' from {snapshot.CreatedAt.LocalDateTime:g}?",
            "Delete");
        if (!confirmed) return;
        await _viewModel.DeleteSnapshotAsync(snapshot);
        _selectedSnapshotId = null;
        Refresh();
    }

    private async Task<string> ReadSnapshotAsync(SnapshotInfo snapshot)
    {
        var project = CurrentProject() ?? throw new InvalidOperationException("Open a project first.");
        var node = _viewModel.SelectedRow?.Node;
        if (node?.IsDocument != true) throw new InvalidOperationException("Select a document first.");
        return await _repository.ReadSnapshotAsync(project, node, snapshot);
    }

    private async Task ShowParagraphDiffAsync(SnapshotInfo snapshot, string snapshotText, string currentText)
    {
        var oldParagraphs = SplitParagraphs(snapshotText);
        var newParagraphs = SplitParagraphs(currentText);
        var count = Math.Max(oldParagraphs.Length, newParagraphs.Length);
        var selections = new List<(int Index, CheckBox Box)>();
        var rows = new StackPanel { Spacing = 5 };
        var changed = 0;

        for (var index = 0; index < count; index++)
        {
            var oldText = index < oldParagraphs.Length ? oldParagraphs[index] : string.Empty;
            var newText = index < newParagraphs.Length ? newParagraphs[index] : string.Empty;
            var isChanged = !string.Equals(NormalizeParagraph(oldText), NormalizeParagraph(newText), StringComparison.Ordinal);
            if (isChanged) changed++;

            var choose = new CheckBox
            {
                IsChecked = isChanged,
                IsEnabled = isChanged,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(5, 8, 2, 0)
            };
            selections.Add((index, choose));

            var left = DiffText(oldText.Length == 0 ? "(paragraph absent)" : oldText);
            var right = DiffText(newText.Length == 0 ? "(paragraph absent)" : newText);
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("34,*,*"),
                Margin = new Thickness(2),
                Background = isChanged ? new SolidColorBrush(Color.FromArgb(22, 255, 190, 70)) : Brushes.Transparent
            };
            row.Children.Add(choose);
            Grid.SetColumn(left, 1);
            row.Children.Add(left);
            Grid.SetColumn(right, 2);
            row.Children.Add(right);
            rows.Children.Add(row);
        }

        var selectChanged = new Button { Content = "Select changed" };
        var clear = new Button { Content = "Clear", Margin = new Thickness(6, 0, 0, 0) };
        var restoreSelected = new Button { Content = "Restore selected paragraphs", Margin = new Thickness(12, 0, 0, 0) };
        var restoreFull = new Button { Content = "Restore full snapshot", Margin = new Thickness(6, 0, 0, 0) };
        var close = new Button { Content = "Close", Margin = new Thickness(6, 0, 0, 0) };

        var dialog = new Window
        {
            Title = $"Snapshot Compare — {snapshot.Label}",
            Width = 1180,
            Height = 780,
            MinWidth = 760,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        selectChanged.Click += (_, _) =>
        {
            foreach (var selection in selections)
                if (selection.Box.IsEnabled) selection.Box.IsChecked = true;
        };
        clear.Click += (_, _) =>
        {
            foreach (var selection in selections) selection.Box.IsChecked = false;
        };
        restoreSelected.Click += async (_, _) =>
        {
            var selected = selections.Where(static item => item.Box.IsChecked == true).Select(static item => item.Index).ToArray();
            if (selected.Length == 0) return;
            await RestoreParagraphsAsync(snapshot, oldParagraphs, newParagraphs, selected);
            dialog.Close();
        };
        restoreFull.Click += async (_, _) =>
        {
            var confirmed = await DesktopDialogService.ConfirmAsync(
                dialog,
                "Restore Full Snapshot",
                "Replace the full current document? A safety snapshot is created first.",
                "Restore");
            if (!confirmed) return;
            await _viewModel.RestoreSnapshotAsync(snapshot);
            dialog.Close();
        };
        close.Click += (_, _) => dialog.Close();

        var labels = new Grid { ColumnDefinitions = new ColumnDefinitions("34,*,*") };
        var oldLabel = new TextBlock { Text = $"Snapshot · {snapshot.CreatedAt.LocalDateTime:g}", FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 4) };
        var newLabel = new TextBlock { Text = "Current manuscript", FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 4) };
        Grid.SetColumn(oldLabel, 1);
        Grid.SetColumn(newLabel, 2);
        labels.Children.Add(oldLabel);
        labels.Children.Add(newLabel);

        var buttons = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { selectChanged, clear, restoreSelected, restoreFull, close }
        };
        var summary = new TextBlock
        {
            Text = $"{changed:N0} changed paragraph row{(changed == 1 ? string.Empty : "s")} · choose exactly what to restore",
            Opacity = 0.72,
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(8) };
        footer.Children.Add(summary);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(8) };
        root.Children.Add(labels);
        var scroll = new ScrollViewer
        {
            Content = rows,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        dialog.Content = root;
        await dialog.ShowDialog(_window);
    }

    private static TextBox DiffText(string text) => new()
    {
        Text = text,
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("monospace"),
        MinHeight = 56,
        Margin = new Thickness(2),
        Padding = new Thickness(8),
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
    };

    private async Task RestoreParagraphsAsync(
        SnapshotInfo snapshot,
        string[] snapshotParagraphs,
        string[] currentParagraphs,
        IReadOnlyList<int> selectedIndices)
    {
        await _viewModel.CreateSnapshotAsync($"Before selective restore {DateTime.Now:g}");
        var merged = currentParagraphs.ToList();

        foreach (var index in selectedIndices.OrderByDescending(static value => value))
        {
            if (index < snapshotParagraphs.Length)
            {
                if (index < merged.Count) merged[index] = snapshotParagraphs[index];
                else merged.Insert(Math.Min(index, merged.Count), snapshotParagraphs[index]);
            }
            else if (index < merged.Count)
            {
                merged.RemoveAt(index);
            }
        }

        _viewModel.UpdateEditorText(string.Join(Environment.NewLine + Environment.NewLine, merged));
        await _viewModel.SaveNowAsync();
        _selectedSnapshotId = snapshot.Id;
    }

    private static string[] SplitParagraphs(string text)
    {
        var normalized = (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd();
        if (normalized.Length == 0) return [string.Empty];
        return System.Text.RegularExpressions.Regex
            .Split(normalized, "\\n[ \\t]*\\n+")
            .Select(static paragraph => paragraph.Trim('\n'))
            .ToArray();
    }

    private static string NormalizeParagraph(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

    private BookProject? CurrentProject()
        => typeof(WorkspaceViewModel)
            .GetField("_project", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_viewModel) as BookProject;

    private static bool HeaderEquals(TabItem item, string header)
        => string.Equals(item.Header?.ToString(), header, StringComparison.Ordinal);

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

    private sealed record SnapshotRow(SnapshotInfo Snapshot)
    {
        public override string ToString()
            => $"{Snapshot.CreatedAt.LocalDateTime:g}  ·  {Snapshot.WordCount:N0} words  ·  {Snapshot.Label}";
    }
}
