using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Typescribe.Desktop.Editing;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

internal sealed record TableEditResult(
    IReadOnlyList<IReadOnlyList<string>> Cells,
    bool HeaderRow,
    IReadOnlyList<TableAlignment> Alignments,
    string Caption,
    string Identifier,
    IReadOnlyList<TableMergeSpan>? Merges = null);

internal static class TableEditorWindow
{
    public static async Task<TableEditResult?> ShowAsync(Window owner, TableEditResult seed)
    {
        var edit = TableEditingEngine.Normalize(seed);
        var activeRow = 0;
        var activeColumn = 0;
        var cellEditors = new Dictionary<(int Row, int Column), TextBox>();

        var position = new TextBlock
        {
            Text = "A1",
            MinWidth = 40,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var dimensions = new TextBlock
        {
            FontSize = 10.5,
            Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0)
        };
        var alignment = new TextBlock
        {
            Text = "Default",
            MinWidth = 48,
            FontSize = 10.5,
            Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center
        };
        var caption = new TextBox
        {
            Text = edit.Caption,
            PlaceholderText = "Caption (optional)",
            MinWidth = 260
        };
        var identifier = new TextBox
        {
            Text = edit.Identifier,
            PlaceholderText = "Identifier, e.g. table-data",
            MinWidth = 220
        };
        var tableGrid = new Grid { ColumnSpacing = 1, RowSpacing = 1 };
        tableGrid.Classes.Add("word-table-grid");
        var scroll = new ScrollViewer
        {
            Content = tableGrid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 320
        };
        var ok = new Button { Content = "Apply", MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(6, 0, 0, 0) };

        Button Command(string text, string tip, Action action)
        {
            var button = new Button
            {
                Content = text,
                MinHeight = 27,
                Padding = new Thickness(7, 1),
                Margin = new Thickness(1, 0)
            };
            button.Classes.Add("table-toolbar-button");
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => action();
            return button;
        }

        void CaptureCells()
        {
            var cells = TableEditingEngine.NormalizeCells(edit.Cells);
            foreach (var pair in cellEditors)
            {
                if (pair.Key.Row < cells.Count && pair.Key.Column < cells[pair.Key.Row].Count)
                    cells[pair.Key.Row][pair.Key.Column] = pair.Value.Text ?? string.Empty;
            }
            edit = edit with
            {
                Cells = cells.Select(static row => (IReadOnlyList<string>)row.ToArray()).ToArray(),
                Caption = caption.Text?.Trim() ?? string.Empty,
                Identifier = identifier.Text?.Trim() ?? string.Empty
            };
            edit = TableEditingEngine.Normalize(edit);
        }

        void RefreshStatus()
        {
            var cells = TableEditingEngine.NormalizeCells(edit.Cells);
            activeRow = Math.Clamp(activeRow, 0, cells.Count - 1);
            activeColumn = Math.Clamp(activeColumn, 0, cells[0].Count - 1);
            position.Text = $"{ColumnName(activeColumn)}{activeRow + 1}";
            dimensions.Text = $"{cells.Count} rows × {cells[0].Count} columns";
            var currentAlignment = activeColumn < edit.Alignments.Count
                ? edit.Alignments[activeColumn]
                : TableAlignment.Default;
            alignment.Text = currentAlignment.ToString();
            foreach (var pair in cellEditors)
                SetClass(pair.Value, "active-table-cell", pair.Key.Row == activeRow && pair.Key.Column == activeColumn);
        }

        void FocusActive(bool selectAll = false)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var key = ResolveFocusable(edit, activeRow, activeColumn);
                activeRow = key.Row;
                activeColumn = key.Column;
                if (cellEditors.TryGetValue(key, out var box))
                {
                    box.Focus();
                    if (selectAll) box.SelectAll();
                }
                RefreshStatus();
            }, DispatcherPriority.Background);
        }

        void ApplyEdit(TableEditResult next, int row, int column, bool focus = true)
        {
            edit = TableEditingEngine.Normalize(next);
            activeRow = row;
            activeColumn = column;
            Rebuild();
            if (focus) FocusActive();
        }

        void InsertRow(int index)
        {
            CaptureCells();
            var next = TableEditingEngine.InsertRow(edit, index);
            ApplyEdit(next, Math.Clamp(index, 0, next.Cells.Count - 1), activeColumn);
        }

        void DeleteRow()
        {
            CaptureCells();
            if (edit.Cells.Count <= 1) return;
            var next = TableEditingEngine.DeleteRow(edit, activeRow);
            ApplyEdit(next, Math.Min(activeRow, next.Cells.Count - 1), activeColumn);
        }

        void InsertColumn(int index)
        {
            CaptureCells();
            var next = TableEditingEngine.InsertColumn(edit, index);
            ApplyEdit(next, activeRow, Math.Clamp(index, 0, next.Cells[0].Count - 1));
        }

        void DeleteColumn()
        {
            CaptureCells();
            if (edit.Cells[0].Count <= 1) return;
            var next = TableEditingEngine.DeleteColumn(edit, activeColumn);
            ApplyEdit(next, activeRow, Math.Min(activeColumn, next.Cells[0].Count - 1));
        }

        void Merge(int rowSpan, int columnSpan)
        {
            CaptureCells();
            var next = TableEditingEngine.Merge(edit, activeRow, activeColumn, rowSpan, columnSpan);
            ApplyEdit(next, activeRow, activeColumn);
        }

        void Unmerge()
        {
            CaptureCells();
            var covering = TableEditCodec.CoveringSpan(edit.Merges ?? [], activeRow, activeColumn);
            var row = covering?.Row ?? activeRow;
            var column = covering?.Column ?? activeColumn;
            var next = TableEditingEngine.Unmerge(edit, activeRow, activeColumn);
            ApplyEdit(next, row, column);
        }

        void SetAlignment(TableAlignment value)
        {
            CaptureCells();
            var next = TableEditingEngine.SetAlignment(edit, activeColumn, value);
            ApplyEdit(next, activeRow, activeColumn);
        }

        void MoveFocus(int delta)
        {
            CaptureCells();
            var keys = cellEditors.Keys.OrderBy(static key => key.Row).ThenBy(static key => key.Column).ToArray();
            var current = ResolveFocusable(edit, activeRow, activeColumn);
            var index = Array.IndexOf(keys, current);
            if (index < 0) return;
            var nextIndex = index + delta;
            if (nextIndex >= 0 && nextIndex < keys.Length)
            {
                activeRow = keys[nextIndex].Row;
                activeColumn = keys[nextIndex].Column;
                FocusActive(selectAll: true);
                return;
            }
            if (delta > 0)
            {
                var cells = TableEditingEngine.NormalizeCells(edit.Cells);
                InsertRow(cells.Count);
                activeColumn = 0;
                FocusActive(selectAll: true);
            }
        }

        void MoveVertical(int delta)
        {
            CaptureCells();
            var cells = TableEditingEngine.NormalizeCells(edit.Cells);
            var target = activeRow + delta;
            if (target >= cells.Count)
            {
                InsertRow(cells.Count);
                return;
            }
            target = Math.Clamp(target, 0, cells.Count - 1);
            var key = ResolveFocusable(edit, target, activeColumn);
            activeRow = key.Row;
            activeColumn = key.Column;
            FocusActive(selectAll: true);
        }

        void Rebuild()
        {
            edit = TableEditingEngine.Normalize(edit);
            var cells = TableEditingEngine.NormalizeCells(edit.Cells);
            var merges = edit.Merges ?? [];
            cellEditors.Clear();
            tableGrid.Children.Clear();
            tableGrid.RowDefinitions.Clear();
            tableGrid.ColumnDefinitions.Clear();

            tableGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(38)));
            for (var column = 0; column < cells[0].Count; column++)
                tableGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(180)));
            tableGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var row = 0; row < cells.Count; row++)
                tableGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var corner = new Border { MinHeight = 28 };
            corner.Classes.Add("table-coordinate-header");
            tableGrid.Children.Add(corner);

            for (var column = 0; column < cells[0].Count; column++)
            {
                var header = new Border { MinHeight = 28 };
                header.Classes.Add("table-coordinate-header");
                header.Child = new TextBlock
                {
                    Text = ColumnName(column),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 10.5,
                    FontWeight = FontWeight.SemiBold,
                    Opacity = 0.7
                };
                Grid.SetColumn(header, column + 1);
                tableGrid.Children.Add(header);
            }

            for (var row = 0; row < cells.Count; row++)
            {
                var header = new Border { MinHeight = 36 };
                header.Classes.Add("table-coordinate-header");
                header.Child = new TextBlock
                {
                    Text = (row + 1).ToString(),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 10.5,
                    Opacity = 0.68
                };
                Grid.SetRow(header, row + 1);
                tableGrid.Children.Add(header);
            }

            for (var row = 0; row < cells.Count; row++)
            {
                for (var column = 0; column < cells[row].Count; column++)
                {
                    if (TableEditCodec.IsContinuation(merges, row, column)) continue;
                    var region = TableEditCodec.CoveringSpan(merges, row, column);
                    var columnAlignment = column < edit.Alignments.Count ? edit.Alignments[column] : TableAlignment.Default;
                    var box = new TextBox
                    {
                        Text = cells[row][column],
                        AcceptsReturn = false,
                        AcceptsTab = false,
                        MinWidth = 150,
                        MinHeight = region is { RowSpan: > 1 } ? 68 : 36,
                        Padding = new Thickness(8, 5),
                        TextWrapping = TextWrapping.Wrap,
                        VerticalContentAlignment = VerticalAlignment.Center,
                        FontWeight = row == 0 ? FontWeight.SemiBold : FontWeight.Normal,
                        PlaceholderText = row == 0 ? $"Heading {column + 1}" : "Cell",
                        TextAlignment = columnAlignment switch
                        {
                            TableAlignment.Center => TextAlignment.Center,
                            TableAlignment.Right => TextAlignment.Right,
                            _ => TextAlignment.Left
                        }
                    };
                    box.Classes.Add("table-cell");
                    if (row == 0) box.Classes.Add("table-header-cell");
                    var capturedRow = row;
                    var capturedColumn = column;
                    box.GotFocus += (_, _) =>
                    {
                        activeRow = capturedRow;
                        activeColumn = capturedColumn;
                        RefreshStatus();
                    };
                    box.KeyDown += (_, e) =>
                    {
                        if (e.Key == Key.Tab)
                        {
                            e.Handled = true;
                            activeRow = capturedRow;
                            activeColumn = capturedColumn;
                            MoveFocus(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                        }
                        else if (e.Key == Key.Enter)
                        {
                            e.Handled = true;
                            activeRow = capturedRow;
                            activeColumn = capturedColumn;
                            MoveVertical(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                        }
                    };
                    cellEditors[(row, column)] = box;
                    Grid.SetRow(box, row + 1);
                    Grid.SetColumn(box, column + 1);
                    if (region is not null)
                    {
                        Grid.SetRowSpan(box, region.RowSpan);
                        Grid.SetColumnSpan(box, region.ColumnSpan);
                    }
                    tableGrid.Children.Add(box);
                }
            }
            RefreshStatus();
        }

        var commands = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        commands.Children.Add(position);
        commands.Children.Add(dimensions);
        commands.Children.Add(Command("Row ↑", "Insert row above active cell", () => InsertRow(activeRow)));
        commands.Children.Add(Command("Row ↓", "Insert row below active cell", () => InsertRow(activeRow + 1)));
        commands.Children.Add(Command("Col ←", "Insert column left of active cell", () => InsertColumn(activeColumn)));
        commands.Children.Add(Command("Col →", "Insert column right of active cell", () => InsertColumn(activeColumn + 1)));
        commands.Children.Add(Command("− Row", "Delete active row", DeleteRow));
        commands.Children.Add(Command("− Col", "Delete active column", DeleteColumn));
        commands.Children.Add(Command("Merge →", "Merge active cell with the cell on the right", () => Merge(1, 2)));
        commands.Children.Add(Command("Merge ↓", "Merge active cell with the cell below", () => Merge(2, 1)));
        commands.Children.Add(Command("Unmerge", "Split the active merged cell", Unmerge));
        commands.Children.Add(Command("L", "Align active column left", () => SetAlignment(TableAlignment.Left)));
        commands.Children.Add(Command("C", "Align active column center", () => SetAlignment(TableAlignment.Center)));
        commands.Children.Add(Command("R", "Align active column right", () => SetAlignment(TableAlignment.Right)));
        commands.Children.Add(alignment);

        var metadata = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 9, 0, 0) };
        metadata.Children.Add(caption);
        Grid.SetColumn(identifier, 1);
        identifier.Margin = new Thickness(6, 0, 0, 0);
        metadata.Children.Add(identifier);

        var note = new TextBlock
        {
            Text = "The first row is the Markdown table header. Tab moves across cells; Enter moves down a row.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.62,
            FontSize = 10.5,
            Margin = new Thickness(0, 7, 0, 0)
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { ok, cancel }
        };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto"), Margin = new Thickness(12) };
        root.Children.Add(commands);
        Grid.SetRow(scroll, 1);
        scroll.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(scroll);
        Grid.SetRow(metadata, 2);
        root.Children.Add(metadata);
        Grid.SetRow(note, 3);
        root.Children.Add(note);
        Grid.SetRow(buttons, 4);
        buttons.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "Table Editor",
            Width = 1040,
            Height = 720,
            MinWidth = 700,
            MinHeight = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };

        ok.Click += (_, _) =>
        {
            CaptureCells();
            dialog.Close(TableEditingEngine.Normalize(edit));
        };
        cancel.Click += (_, _) => dialog.Close(null);

        Rebuild();
        return await dialog.ShowDialog<TableEditResult?>(owner);
    }

    private static (int Row, int Column) ResolveFocusable(TableEditResult edit, int row, int column)
    {
        var cells = TableEditingEngine.NormalizeCells(edit.Cells);
        row = Math.Clamp(row, 0, cells.Count - 1);
        column = Math.Clamp(column, 0, cells[0].Count - 1);
        var covering = TableEditCodec.CoveringSpan(edit.Merges ?? [], row, column);
        return covering is null ? (row, column) : (covering.Row, covering.Column);
    }

    private static string ColumnName(int index)
    {
        index++;
        var result = string.Empty;
        while (index > 0)
        {
            index--;
            result = (char)('A' + index % 26) + result;
            index /= 26;
        }
        return result;
    }

    private static void SetClass(Control control, string className, bool enabled)
    {
        var has = control.Classes.Contains(className);
        if (enabled && !has) control.Classes.Add(className);
        else if (!enabled && has) control.Classes.Remove(className);
    }
}