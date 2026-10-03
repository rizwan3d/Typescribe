using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
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
        var cells = seed.Cells.Select(static row => row.ToList()).ToList();
        if (cells.Count == 0) cells.Add([string.Empty, string.Empty]);
        var columns = Math.Max(1, cells.Max(static row => row.Count));
        foreach (var row in cells) while (row.Count < columns) row.Add(string.Empty);
        var alignments = seed.Alignments.ToList();
        while (alignments.Count < columns) alignments.Add(TableAlignment.Default);
        var merges = TableEditCodec.NormalizeMerges(seed.Merges ?? [], cells.Count, columns).ToList();
        var activeRow = 0;
        var activeColumn = 0;

        var tableGrid = new Grid();
        var alignmentPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var scroll = new ScrollViewer
        {
            Content = tableGrid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 260
        };
        var headerRow = new CheckBox { Content = "First row is a header", IsChecked = seed.HeaderRow, VerticalAlignment = VerticalAlignment.Center };
        var caption = new TextBox { Text = seed.Caption, Watermark = "Caption (optional)" };
        var identifier = new TextBox { Text = seed.Identifier, Watermark = "Identifier, e.g. table-data" };
        var rowsBox = new TextBox { Text = cells.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 52 };
        var colsBox = new TextBox { Text = columns.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 52 };
        var resize = new Button { Content = "Resize" };
        var addRow = new Button { Content = "+ Row" };
        var removeRow = new Button { Content = "− Row" };
        var addColumn = new Button { Content = "+ Column" };
        var removeColumn = new Button { Content = "− Column" };
        var mergeRight = new Button { Content = "Merge →" };
        var mergeDown = new Button { Content = "Merge ↓" };
        var unmerge = new Button { Content = "Unmerge" };
        var ok = new Button { Content = "Apply", MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(6, 0, 0, 0) };
        var cellEditors = new Dictionary<(int Row, int Column), TextBox>();
        var alignmentEditors = new List<ComboBox>();

        void Capture()
        {
            foreach (var pair in cellEditors)
                cells[pair.Key.Row][pair.Key.Column] = pair.Value.Text ?? string.Empty;
            for (var column = 0; column < Math.Min(alignments.Count, alignmentEditors.Count); column++)
                if (alignmentEditors[column].SelectedItem is TableAlignment value) alignments[column] = value;
        }

        void Normalize(int requestedRows, int requestedColumns)
        {
            requestedRows = Math.Clamp(requestedRows, 1, 100);
            requestedColumns = Math.Clamp(requestedColumns, 1, 30);
            while (cells.Count < requestedRows) cells.Add(Enumerable.Repeat(string.Empty, requestedColumns).ToList());
            while (cells.Count > requestedRows) cells.RemoveAt(cells.Count - 1);
            foreach (var row in cells)
            {
                while (row.Count < requestedColumns) row.Add(string.Empty);
                while (row.Count > requestedColumns) row.RemoveAt(row.Count - 1);
            }
            while (alignments.Count < requestedColumns) alignments.Add(TableAlignment.Default);
            while (alignments.Count > requestedColumns) alignments.RemoveAt(alignments.Count - 1);
            merges = TableEditCodec.NormalizeMerges(merges, requestedRows, requestedColumns).ToList();
            activeRow = Math.Clamp(activeRow, 0, requestedRows - 1);
            activeColumn = Math.Clamp(activeColumn, 0, requestedColumns - 1);
            rowsBox.Text = requestedRows.ToString(System.Globalization.CultureInfo.InvariantCulture);
            colsBox.Text = requestedColumns.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        void Rebuild()
        {
            cellEditors.Clear();
            alignmentEditors.Clear();
            tableGrid.Children.Clear();
            tableGrid.RowDefinitions.Clear();
            tableGrid.ColumnDefinitions.Clear();
            alignmentPanel.Children.Clear();
            for (var column = 0; column < cells[0].Count; column++) tableGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(180)));
            for (var row = 0; row < cells.Count; row++) tableGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            for (var row = 0; row < cells.Count; row++)
            {
                for (var column = 0; column < cells[row].Count; column++)
                {
                    if (TableEditCodec.IsContinuation(merges, row, column)) continue;
                    var region = TableEditCodec.CoveringSpan(merges, row, column);
                    var box = new TextBox
                    {
                        Text = cells[row][column],
                        MinWidth = 150,
                        MinHeight = region is { RowSpan: > 1 } ? 56 : 30,
                        Margin = new Thickness(2),
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Watermark = region is not null ? "Merged cell" : row == 0 && headerRow.IsChecked == true ? $"Heading {column + 1}" : "Cell"
                    };
                    var capturedRow = row;
                    var capturedColumn = column;
                    box.GotFocus += (_, _) => { activeRow = capturedRow; activeColumn = capturedColumn; };
                    Grid.SetRow(box, row);
                    Grid.SetColumn(box, column);
                    if (region is not null)
                    {
                        Grid.SetRowSpan(box, region.RowSpan);
                        Grid.SetColumnSpan(box, region.ColumnSpan);
                    }
                    tableGrid.Children.Add(box);
                    cellEditors[(row, column)] = box;
                }
            }

            for (var column = 0; column < cells[0].Count; column++)
            {
                var combo = new ComboBox { ItemsSource = Enum.GetValues<TableAlignment>(), SelectedItem = alignments[column], Width = 112 };
                alignmentPanel.Children.Add(new StackPanel { Children = { new TextBlock { Text = $"Column {column + 1}" }, combo } });
                alignmentEditors.Add(combo);
            }
        }

        bool TryMerge(int rowSpan, int columnSpan)
        {
            Capture();
            if (activeRow + rowSpan > cells.Count || activeColumn + columnSpan > cells[0].Count) return false;
            if (TableEditCodec.CoveringSpan(merges, activeRow, activeColumn) is not null) return false;
            for (var row = activeRow; row < activeRow + rowSpan; row++)
                for (var column = activeColumn; column < activeColumn + columnSpan; column++)
                    if (TableEditCodec.CoveringSpan(merges, row, column) is not null) return false;
            merges.Add(new TableMergeSpan(activeRow, activeColumn, rowSpan, columnSpan));
            merges = TableEditCodec.NormalizeMerges(merges, cells.Count, cells[0].Count).ToList();
            Rebuild();
            return true;
        }

        void UnmergeActive()
        {
            Capture();
            var covering = TableEditCodec.CoveringSpan(merges, activeRow, activeColumn);
            if (covering is null) return;
            merges.Remove(covering);
            activeRow = covering.Row;
            activeColumn = covering.Column;
            Rebuild();
        }

        headerRow.IsCheckedChanged += (_, _) => { Capture(); Rebuild(); };
        resize.Click += (_, _) =>
        {
            Capture();
            var rows = int.TryParse(rowsBox.Text, out var r) ? r : cells.Count;
            var cols = int.TryParse(colsBox.Text, out var c) ? c : cells[0].Count;
            Normalize(rows, cols);
            Rebuild();
        };
        addRow.Click += (_, _) => { Capture(); Normalize(cells.Count + 1, cells[0].Count); Rebuild(); };
        removeRow.Click += (_, _) => { Capture(); Normalize(cells.Count - 1, cells[0].Count); Rebuild(); };
        addColumn.Click += (_, _) => { Capture(); Normalize(cells.Count, cells[0].Count + 1); Rebuild(); };
        removeColumn.Click += (_, _) => { Capture(); Normalize(cells.Count, cells[0].Count - 1); Rebuild(); };
        mergeRight.Click += (_, _) => TryMerge(1, 2);
        mergeDown.Click += (_, _) => TryMerge(2, 1);
        unmerge.Click += (_, _) => UnmergeActive();

        var sizing = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = "Rows", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) }, rowsBox,
                new TextBlock { Text = "Columns", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) }, colsBox,
                resize, addRow, removeRow, addColumn, removeColumn, mergeRight, mergeDown, unmerge
            }
        };
        var metadata = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 8, 0, 0) };
        metadata.Children.Add(caption);
        Grid.SetColumn(identifier, 1);
        identifier.Margin = new Thickness(6, 0, 0, 0);
        metadata.Children.Add(identifier);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto,Auto"), Margin = new Thickness(12) };
        root.Children.Add(sizing);
        Grid.SetRow(headerRow, 1); headerRow.Margin = new Thickness(0, 8, 0, 6); root.Children.Add(headerRow);
        Grid.SetRow(scroll, 2); root.Children.Add(scroll);
        Grid.SetRow(alignmentPanel, 3); alignmentPanel.Margin = new Thickness(0, 8, 0, 0); root.Children.Add(alignmentPanel);
        Grid.SetRow(metadata, 4); root.Children.Add(metadata);
        Grid.SetRow(buttons, 5); buttons.Margin = new Thickness(0, 10, 0, 0); root.Children.Add(buttons);
        var dialog = new Window { Title = "Table Editor", Width = 960, Height = 700, MinWidth = 660, MinHeight = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root };

        ok.Click += (_, _) =>
        {
            Capture();
            dialog.Close(new TableEditResult(
                cells.Select(static row => (IReadOnlyList<string>)row.ToArray()).ToArray(),
                headerRow.IsChecked == true,
                alignments.ToArray(),
                caption.Text?.Trim() ?? string.Empty,
                identifier.Text?.Trim() ?? string.Empty,
                TableEditCodec.NormalizeMerges(merges, cells.Count, cells[0].Count)));
        };
        cancel.Click += (_, _) => dialog.Close(null);
        Rebuild();
        return await dialog.ShowDialog<TableEditResult?>(owner);
    }
}
