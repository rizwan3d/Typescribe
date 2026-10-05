using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Typescribe.Application.Abstractions;
using Typescribe.Desktop.Editing;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;
using ToolTip = Avalonia.Controls.ToolTip;

namespace Typescribe.Desktop;

/// <summary>
/// Adds the manuscript Table command and context menu. All current-table operations route through
/// TableEditingEngine so row/column changes preserve alignments and merged-cell metadata.
/// </summary>
internal sealed class RichTableMenuFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly IDocumentParser _parser;
    private readonly HashSet<ManuscriptEditor> _editors = [];
    private readonly HashSet<ManuscriptEditor> _contextMenusInstalled = [];
    private ManuscriptEditor? _lastEditor;
    private bool _toolbarInstalled;
    private bool _disposed;

    private RichTableMenuFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel, IDocumentParser parser)
    {
        _window = window;
        _viewModel = viewModel;
        _parser = parser;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel, IDocumentParser parser)
    {
        var feature = new RichTableMenuFeature(window, viewModel, parser);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        feature.Discover();
    }

    private void OnOpened(object? sender, EventArgs e) => Discover();
    private void OnLayoutUpdated(object? sender, EventArgs e) => Discover();

    private void Discover()
    {
        if (_disposed) return;
        foreach (var editor in _window.GetVisualDescendants().OfType<ManuscriptEditor>())
        {
            if (_editors.Add(editor))
            {
                editor.GotFocus += EditorGotFocus;
                _lastEditor ??= editor;
            }
            InstallContextMenu(editor);
        }
        InstallToolbarButton();
    }

    private void EditorGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is ManuscriptEditor editor) _lastEditor = editor;
    }

    private void InstallToolbarButton()
    {
        if (_toolbarInstalled) return;
        foreach (var panel in _window.GetVisualDescendants().OfType<StackPanel>()
                     .Where(static panel => panel.Orientation == Orientation.Horizontal))
        {
            var labels = panel.Children.OfType<Button>()
                .Select(static button => button.Content?.ToString() ?? string.Empty)
                .ToHashSet(StringComparer.Ordinal);
            if (!labels.Contains("B") || !labels.Contains("I") || !labels.Contains("H1") || !labels.Contains("H2"))
                continue;
            if (panel.Children.OfType<Button>().Any(static button => string.Equals(button.Name, "QuickTableToolbarButton", StringComparison.Ordinal)))
            {
                _toolbarInstalled = true;
                return;
            }

            var table = new Button
            {
                Name = "QuickTableToolbarButton",
                Content = "Table",
                MinWidth = 54,
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(6, 1),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(table, "Insert a table and edit it visually");
            table.Click += async (_, _) => await ShowQuickTableAsync(ResolveEditor());
            panel.Children.Add(table);
            _toolbarInstalled = true;
            return;
        }
    }

    private void InstallContextMenu(ManuscriptEditor editor)
    {
        if (_contextMenusInstalled.Contains(editor) || editor.ContextMenu is null) return;
        var existing = editor.ContextMenu.ItemsSource is IEnumerable enumerable
            ? enumerable.Cast<object>().ToList()
            : [];
        if (existing.OfType<MenuItem>().Any(static item =>
                string.Equals(item.Header?.ToString()?.Replace("_", string.Empty, StringComparison.Ordinal), "Table", StringComparison.OrdinalIgnoreCase)))
        {
            _contextMenusInstalled.Add(editor);
            return;
        }

        var tableMenu = new MenuItem { Header = "_Table" };
        tableMenu.ItemsSource = new object[]
        {
            Command("_Quick Table…", async () => await ShowQuickTableAsync(editor)),
            Command("_Edit Current Table…", async () => await EditCurrentTableAsync(editor)),
            new Separator(),
            Command("Insert Row _Above", () => Mutate(editor, TableMutation.RowAbove)),
            Command("Insert Row _Below", () => Mutate(editor, TableMutation.RowBelow)),
            Command("_Delete Current Row", () => Mutate(editor, TableMutation.DeleteRow)),
            new Separator(),
            Command("Insert Column _Left", () => Mutate(editor, TableMutation.ColumnLeft)),
            Command("Insert Column _Right", () => Mutate(editor, TableMutation.ColumnRight)),
            Command("Delete Current _Column", () => Mutate(editor, TableMutation.DeleteColumn)),
            new Separator(),
            Command("Merge _Right", () => Mutate(editor, TableMutation.MergeRight)),
            Command("Merge _Down", () => Mutate(editor, TableMutation.MergeDown)),
            Command("_Unmerge Cell", () => Mutate(editor, TableMutation.Unmerge)),
            new Separator(),
            Command("Align _Left", () => Mutate(editor, TableMutation.AlignLeft)),
            Command("Align _Center", () => Mutate(editor, TableMutation.AlignCenter)),
            Command("Align _Right", () => Mutate(editor, TableMutation.AlignRight)),
            new Separator(),
            Command("Delete _Table", () => Mutate(editor, TableMutation.DeleteTable))
        };
        existing.Add(new Separator());
        existing.Add(tableMenu);
        editor.ContextMenu.ItemsSource = existing;
        _contextMenusInstalled.Add(editor);
    }

    private static MenuItem Command(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    private static MenuItem Command(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private async Task ShowQuickTableAsync(ManuscriptEditor? editor)
    {
        if (editor is null || !_viewModel.HasDocument) return;
        _lastEditor = editor;
        var size = await PickTableSizeAsync();
        if (size is null) return;

        var rows = new List<IReadOnlyList<string>>
        {
            Enumerable.Range(1, size.Columns).Select(static index => $"Heading {index}").ToArray()
        };
        for (var row = 1; row < size.Rows; row++)
            rows.Add(Enumerable.Repeat(string.Empty, size.Columns).ToArray());

        var seed = new TableEditResult(
            rows,
            HeaderRow: true,
            Enumerable.Repeat(TableAlignment.Default, size.Columns).ToArray(),
            string.Empty,
            string.Empty);
        var result = await TableEditorWindow.ShowAsync(_window, seed);
        if (result is null) return;
        InsertBlock(editor, TableEditingEngine.Serialize(result));
    }

    private async Task<TableSize?> PickTableSizeAsync()
    {
        const int maxRows = 8;
        const int maxColumns = 8;
        var status = new TextBlock
        {
            Text = "2 × 2 table",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        for (var column = 0; column < maxColumns; column++) grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(30)));
        for (var row = 0; row < maxRows; row++) grid.RowDefinitions.Add(new RowDefinition(new GridLength(30)));

        var dialog = new Window
        {
            Title = "Quick Table",
            Width = 360,
            Height = 365,
            MinWidth = 340,
            MinHeight = 340,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var buttons = new Button[maxRows, maxColumns];
        void Highlight(int row, int column)
        {
            status.Text = $"{row + 1} × {column + 1} table";
            for (var r = 0; r < maxRows; r++)
                for (var c = 0; c < maxColumns; c++)
                    buttons[r, c].Opacity = r <= row && c <= column ? 1.0 : 0.45;
        }

        for (var row = 0; row < maxRows; row++)
        {
            for (var column = 0; column < maxColumns; column++)
            {
                var r = row;
                var c = column;
                var button = new Button
                {
                    Width = 26,
                    Height = 26,
                    Margin = new Thickness(1),
                    Padding = new Thickness(0)
                };
                button.PointerEntered += (_, _) => Highlight(r, c);
                button.Click += (_, _) => dialog.Close(new TableSize(r + 1, c + 1));
                Grid.SetRow(button, row);
                Grid.SetColumn(button, column);
                grid.Children.Add(button);
                buttons[row, column] = button;
            }
        }
        Highlight(1, 1);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(14),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Choose rows × columns", FontWeight = Avalonia.Media.FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                status,
                grid,
                new TextBlock { Text = "The table opens in the visual editor before insertion.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.72, HorizontalAlignment = HorizontalAlignment.Center }
            }
        };
        return await dialog.ShowDialog<TableSize?>(_window);
    }

    private async Task EditCurrentTableAsync(ManuscriptEditor editor)
    {
        if (!_viewModel.HasDocument) return;
        var context = TableEditingEngine.FindCurrent(editor, _parser);
        if (context is null) return;
        var result = await TableEditorWindow.ShowAsync(_window, context.Edit);
        if (result is null) return;
        Replace(editor, context, result);
    }

    private void Mutate(ManuscriptEditor editor, TableMutation mutation)
    {
        if (!_viewModel.HasDocument) return;
        var context = TableEditingEngine.FindCurrent(editor, _parser);
        if (context is null) return;
        if (mutation == TableMutation.DeleteTable)
        {
            editor.Document.Remove(context.Offset, context.Length);
            editor.CaretIndex = Math.Clamp(context.Offset, 0, editor.Document.TextLength);
            return;
        }

        var edit = context.Edit;
        edit = mutation switch
        {
            TableMutation.RowAbove => TableEditingEngine.InsertRow(edit, context.Row),
            TableMutation.RowBelow => TableEditingEngine.InsertRow(edit, context.Row + 1),
            TableMutation.DeleteRow => TableEditingEngine.DeleteRow(edit, context.Row),
            TableMutation.ColumnLeft => TableEditingEngine.InsertColumn(edit, context.Column),
            TableMutation.ColumnRight => TableEditingEngine.InsertColumn(edit, context.Column + 1),
            TableMutation.DeleteColumn => TableEditingEngine.DeleteColumn(edit, context.Column),
            TableMutation.MergeRight => TableEditingEngine.Merge(edit, context.Row, context.Column, 1, 2),
            TableMutation.MergeDown => TableEditingEngine.Merge(edit, context.Row, context.Column, 2, 1),
            TableMutation.Unmerge => TableEditingEngine.Unmerge(edit, context.Row, context.Column),
            TableMutation.AlignLeft => TableEditingEngine.SetAlignment(edit, context.Column, TableAlignment.Left),
            TableMutation.AlignCenter => TableEditingEngine.SetAlignment(edit, context.Column, TableAlignment.Center),
            TableMutation.AlignRight => TableEditingEngine.SetAlignment(edit, context.Column, TableAlignment.Right),
            _ => edit
        };
        Replace(editor, context, edit);
    }

    private static void Replace(ManuscriptEditor editor, TableEditingContext context, TableEditResult edit)
    {
        var markup = TableEditingEngine.Serialize(edit);
        editor.Document.Replace(context.Offset, context.Length, markup);
        editor.CaretIndex = Math.Clamp(context.Offset, 0, editor.Document.TextLength);
        editor.Focus();
    }

    private static void InsertBlock(ManuscriptEditor editor, string block)
    {
        var text = editor.Text ?? string.Empty;
        var caret = Math.Clamp(editor.CaretIndex, 0, text.Length);
        var prefix = caret > 0 && text[caret - 1] != '\n' ? Environment.NewLine + Environment.NewLine : string.Empty;
        var suffix = caret < text.Length && text[caret] != '\n' ? Environment.NewLine + Environment.NewLine : Environment.NewLine;
        var insert = prefix + block.Trim() + suffix;
        editor.Document.Insert(caret, insert);
        editor.CaretIndex = Math.Min(editor.Document.TextLength, caret + prefix.Length + Math.Max(0, insert.Length - suffix.Length));
        editor.Focus();
    }

    private ManuscriptEditor? ResolveEditor()
    {
        if (_lastEditor is { IsVisible: true }) return _lastEditor;
        _lastEditor = _window.GetVisualDescendants().OfType<ManuscriptEditor>().FirstOrDefault(static editor => editor.IsVisible);
        return _lastEditor;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        foreach (var editor in _editors) editor.GotFocus -= EditorGotFocus;
        _editors.Clear();
        _contextMenusInstalled.Clear();
    }

    private sealed record TableSize(int Rows, int Columns);

    private enum TableMutation
    {
        RowAbove,
        RowBelow,
        DeleteRow,
        ColumnLeft,
        ColumnRight,
        DeleteColumn,
        MergeRight,
        MergeDown,
        Unmerge,
        AlignLeft,
        AlignCenter,
        AlignRight,
        DeleteTable
    }
}