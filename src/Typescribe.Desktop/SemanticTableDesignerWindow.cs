using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Typescribe.Application.Abstractions;
using Typescribe.Application.Services;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

internal static class SemanticTableDesignerWindow
{
    public static async Task ShowAsync(Window owner, WorkspaceViewModel viewModel, IDocumentParser parser)
    {
        if (!viewModel.HasDocument) return;

        var tableList = new ListBox { MinWidth = 250 };
        var caption = new TextBox { Watermark = "Caption" };
        var identifier = new TextBox { Watermark = "table-id" };
        var style = new ComboBox { MinWidth = 190 };
        var repeatHeader = new TextBox { Text = "1", Width = 80 };
        var width = new TextBox { Watermark = "100", Width = 90 };
        var layout = new ComboBox { ItemsSource = Enum.GetValues<TableLayoutMode>(), SelectedItem = TableLayoutMode.Auto };
        var captionPosition = new ComboBox { ItemsSource = Enum.GetValues<CaptionPosition>(), SelectedItem = CaptionPosition.Top };
        var keepTogether = new CheckBox { Content = "Keep table together when possible" };
        var allowRowBreak = new CheckBox { Content = "Allow rows to break across pages", IsChecked = true };

        var rowList = new ListBox { MinHeight = 150 };
        var rowStyle = new ComboBox { MinWidth = 180 };
        var rowRepeat = new CheckBox { Content = "Repeat this row as a header" };
        var rowKeep = new CheckBox { Content = "Keep row together" };
        var rowBreak = new CheckBox { Content = "Allow row to break across pages", IsChecked = true };
        var rowMinHeight = new TextBox { Watermark = "Minimum height (pt)", Width = 150 };
        var applyRow = new Button { Content = "Apply Row Properties" };

        var cellList = new ListBox { MinHeight = 170 };
        var cellStyle = new ComboBox { MinWidth = 180 };
        var cellHorizontal = new ComboBox { ItemsSource = Enum.GetValues<TableCellHorizontalAlignment>() };
        var cellVertical = new ComboBox { ItemsSource = Enum.GetValues<TableCellVerticalAlignment>() };
        var cellBackground = new TextBox { Watermark = "Background #RRGGBB" };
        var cellTextColor = new TextBox { Watermark = "Text #RRGGBB" };
        var cellBold = new CheckBox { Content = "Bold" };
        var applyCell = new Button { Content = "Apply Cell Properties" };

        var preview = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = "monospace",
            MinHeight = 150,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap
        };
        var error = new TextBlock { Foreground = Avalonia.Media.Brushes.IndianRed, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var save = new Button { Content = "Save Table Design", MinWidth = 120 };
        var close = new Button { Content = "Close", MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };

        var tableFields = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("150,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 8,
            RowSpacing = 6
        };
        AddField(tableFields, 0, "Caption", caption);
        AddField(tableFields, 1, "Identifier", identifier);
        AddField(tableFields, 2, "Table style", style);
        AddField(tableFields, 3, "Repeat header rows", repeatHeader);
        AddField(tableFields, 4, "Width (%)", width);
        AddField(tableFields, 5, "Layout", layout);
        AddField(tableFields, 6, "Caption position", captionPosition);

        var rowsPanel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Selected row", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                rowList,
                rowStyle,
                rowRepeat,
                rowKeep,
                rowBreak,
                rowMinHeight,
                applyRow
            }
        };
        var cellsPanel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Selected cell", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                cellList,
                cellStyle,
                cellHorizontal,
                cellVertical,
                cellBackground,
                cellTextColor,
                cellBold,
                applyCell
            }
        };
        var semanticGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        semanticGrid.Children.Add(rowsPanel);
        Grid.SetColumn(cellsPanel, 1);
        semanticGrid.Children.Add(cellsPanel);

        var detail = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Table design", FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                tableFields,
                keepTogether,
                allowRowBreak,
                semanticGrid,
                new TextBlock { Text = "Canonical text preview", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                preview,
                error
            }
        };
        var rightScroll = new ScrollViewer { Content = detail, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("270,*"), ColumnSpacing = 8 };
        body.Children.Add(tableList);
        Grid.SetColumn(rightScroll, 1);
        body.Children.Add(rightScroll);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10),
            Children = { save, close }
        };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(new TextBlock
        {
            Text = "Better Table Designer — semantic styles, repeated headers and row/cell properties remain embedded in canonical Markdown.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(12)
        });
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "Table Designer",
            Width = 1040,
            Height = 760,
            MinWidth = 820,
            MinHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };

        TableBlock? working = null;
        IReadOnlyList<TableChoice> choices = [];

        void LoadStyleChoices()
        {
            style.ItemsSource = new[] { string.Empty }.Concat(viewModel.CurrentStyle.NamedStyles.TableStyles.Select(static item => item.Id)).ToArray();
            rowStyle.ItemsSource = new[] { string.Empty }.Concat(viewModel.CurrentStyle.NamedStyles.TableStyles.Select(static item => item.Id)).ToArray();
            cellStyle.ItemsSource = new[] { string.Empty }.Concat(viewModel.CurrentStyle.NamedStyles.CellStyles.Select(static item => item.Id)).ToArray();
        }

        void RefreshTables(int? selectLine = null)
        {
            var ast = parser.Parse(viewModel.EditorText);
            choices = ast.Blocks.OfType<TableBlock>().Select(static table => new TableChoice(table)).ToArray();
            tableList.ItemsSource = choices;
            tableList.SelectedItem = selectLine is int line
                ? choices.FirstOrDefault(choice => choice.Table.SourceLine == line) ?? choices.FirstOrDefault()
                : choices.FirstOrDefault();
        }

        void LoadTable(TableBlock? table)
        {
            working = table;
            if (table is null)
            {
                preview.Text = "No table in the current document.";
                rowList.ItemsSource = null;
                cellList.ItemsSource = null;
                return;
            }
            caption.Text = table.Caption ?? string.Empty;
            identifier.Text = table.Identifier ?? string.Empty;
            style.SelectedItem = table.StyleId ?? string.Empty;
            repeatHeader.Text = Math.Max(0, table.RepeatHeaderRows).ToString(System.Globalization.CultureInfo.InvariantCulture);
            width.Text = table.Properties?.WidthPercent?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            layout.SelectedItem = table.Properties?.Layout ?? TableLayoutMode.Auto;
            captionPosition.SelectedItem = table.Properties?.CaptionPosition ?? CaptionPosition.Top;
            keepTogether.IsChecked = table.Properties?.KeepTogether ?? false;
            allowRowBreak.IsChecked = table.Properties?.AllowRowBreakAcrossPages ?? true;

            var rowChoices = Enumerable.Range(0, table.Rows.Count + 1).Select(index => new RowChoice(index)).ToArray();
            rowList.ItemsSource = rowChoices;
            rowList.SelectedItem = rowChoices.FirstOrDefault();
            RefreshCellChoices();
            RefreshPreview();
        }

        void RefreshCellChoices()
        {
            if (working is null) return;
            var cells = new List<CellChoice>();
            for (var column = 0; column < working.Header.Count; column++)
                cells.Add(new CellChoice(0, column, working.Header[column]));
            for (var row = 0; row < working.Rows.Count; row++)
                for (var column = 0; column < working.Rows[row].Count; column++)
                    cells.Add(new CellChoice(row + 1, column, working.Rows[row][column]));
            cellList.ItemsSource = cells;
            cellList.SelectedItem = cells.FirstOrDefault();
        }

        void RefreshPreview()
        {
            if (working is not null) preview.Text = TableMarkupCodec.Serialize(working, "\n");
        }

        void LoadRow(int index)
        {
            if (working is null) return;
            var props = working.RowProperties is { Count: > 0 } && index < working.RowProperties.Count
                ? working.RowProperties[index]
                : new TableRowProperties(RepeatAsHeader: index < working.RepeatHeaderRows);
            rowStyle.SelectedItem = props.StyleId ?? string.Empty;
            rowRepeat.IsChecked = props.RepeatAsHeader || index < working.RepeatHeaderRows;
            rowKeep.IsChecked = props.KeepTogether;
            rowBreak.IsChecked = props.AllowBreakAcrossPages;
            rowMinHeight.Text = props.MinimumHeightPoints?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }

        void LoadCell(CellChoice? choice)
        {
            if (choice is null) return;
            var props = choice.Cell.Properties ?? new TableCellProperties();
            cellStyle.SelectedItem = choice.Cell.StyleId ?? string.Empty;
            cellHorizontal.SelectedItem = props.HorizontalAlignment;
            cellVertical.SelectedItem = props.VerticalAlignment;
            cellBackground.Text = props.BackgroundColorHex ?? string.Empty;
            cellTextColor.Text = props.TextColorHex ?? string.Empty;
            cellBold.IsChecked = props.Bold ?? false;
        }

        tableList.SelectionChanged += (_, _) => LoadTable((tableList.SelectedItem as TableChoice)?.Table);
        rowList.SelectionChanged += (_, _) =>
        {
            if (rowList.SelectedItem is RowChoice selected) LoadRow(selected.Index);
        };
        cellList.SelectionChanged += (_, _) => LoadCell(cellList.SelectedItem as CellChoice);

        applyRow.Click += (_, _) =>
        {
            if (working is null || rowList.SelectedItem is not RowChoice selected) return;
            var count = working.Rows.Count + 1;
            var rows = Enumerable.Range(0, count)
                .Select(index => working.RowProperties is { Count: > 0 } && index < working.RowProperties.Count
                    ? working.RowProperties[index]
                    : new TableRowProperties(RepeatAsHeader: index < working.RepeatHeaderRows))
                .ToArray();
            rows[selected.Index] = new TableRowProperties(
                Clean(rowStyle.SelectedItem?.ToString()),
                rowKeep.IsChecked == true,
                rowBreak.IsChecked != false,
                rowRepeat.IsChecked == true,
                ParseOptionalDouble(rowMinHeight.Text));
            working = working with { RowProperties = rows };
            RefreshPreview();
        };

        applyCell.Click += (_, _) =>
        {
            if (working is null || cellList.SelectedItem is not CellChoice selected) return;
            var replacement = selected.Cell with
            {
                StyleId = Clean(cellStyle.SelectedItem?.ToString()),
                Properties = new TableCellProperties(
                    cellHorizontal.SelectedItem as TableCellHorizontalAlignment?,
                    cellVertical.SelectedItem as TableCellVerticalAlignment?,
                    Clean(cellBackground.Text),
                    Clean(cellTextColor.Text),
                    selected.Cell.Properties?.PaddingPoints,
                    selected.Cell.Properties?.BorderWidthPoints,
                    selected.Cell.Properties?.BorderColorHex,
                    cellBold.IsChecked,
                    selected.Cell.Properties?.NumberFormat)
            };
            working = ReplaceCell(working, selected.Row, selected.Column, replacement);
            RefreshCellChoices();
            RefreshPreview();
        };

        save.Click += (_, _) =>
        {
            try
            {
                if (working is null) return;
                error.Text = string.Empty;
                var repeat = int.TryParse(repeatHeader.Text?.Trim(), out var parsedRepeat) ? Math.Max(0, parsedRepeat) : 1;
                var properties = new TableProperties(
                    ParseOptionalDouble(width.Text),
                    layout.SelectedItem is TableLayoutMode layoutValue ? layoutValue : null,
                    captionPosition.SelectedItem is CaptionPosition captionValue ? captionValue : null,
                    keepTogether.IsChecked,
                    allowRowBreak.IsChecked != false);
                var updated = working with
                {
                    Caption = Clean(caption.Text),
                    Identifier = Clean(identifier.Text),
                    StyleId = Clean(style.SelectedItem?.ToString()),
                    RepeatHeaderRows = repeat,
                    Properties = properties
                };
                var replacement = TableMarkupCodec.Serialize(updated, DetectNewline(viewModel.EditorText));
                viewModel.UpdateEditorText(ReplaceTable(viewModel.EditorText, working, replacement));
                working = updated;
                RefreshTables(updated.SourceLine);
            }
            catch (Exception ex)
            {
                error.Text = ex.Message;
            }
        };
        close.Click += (_, _) => dialog.Close();

        LoadStyleChoices();
        RefreshTables();
        await dialog.ShowDialog(owner);
    }

    private static void AddField(Grid grid, int row, string label, Control control)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(text, row);
        grid.Children.Add(text);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
    }

    private static TableBlock ReplaceCell(TableBlock table, int row, int column, TableCell replacement)
    {
        if (row == 0)
        {
            var header = table.Header.ToArray();
            if (column >= 0 && column < header.Length) header[column] = replacement;
            return table with { Header = header };
        }
        var rows = table.Rows.Select(static cells => cells.ToArray()).ToArray();
        var bodyRow = row - 1;
        if (bodyRow >= 0 && bodyRow < rows.Length && column >= 0 && column < rows[bodyRow].Length)
            rows[bodyRow][column] = replacement;
        return table with { Rows = rows };
    }

    private static string ReplaceTable(string source, TableBlock table, string replacement)
    {
        var newline = DetectNewline(source);
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        var header = Math.Clamp(table.SourceLine - 1, 0, Math.Max(0, lines.Count - 1));
        var start = header > 0 && lines[header - 1].TrimStart().StartsWith(TableMarkupCodec.MetadataPrefix, StringComparison.Ordinal)
            ? header - 1
            : header;
        var end = Math.Min(lines.Count - 1, header + 1);
        while (end + 1 < lines.Count)
        {
            var candidate = lines[end + 1].Trim();
            if (candidate.Length == 0 || !candidate.Contains('|')) break;
            end++;
        }
        lines.RemoveRange(start, end - start + 1);
        lines.InsertRange(start, replacement.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'));
        return string.Join(newline, lines);
    }

    private static string DetectNewline(string source) => source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static double? ParseOptionalDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var local)) return local;
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var invariant) ? invariant : null;
    }

    private sealed record TableChoice(TableBlock Table)
    {
        public override string ToString() => $"Line {Table.SourceLine}: {(string.IsNullOrWhiteSpace(Table.Caption) ? Table.Identifier ?? "Table" : Table.Caption)}";
    }
    private sealed record RowChoice(int Index)
    {
        public override string ToString() => Index == 0 ? "Header row" : $"Body row {Index}";
    }
    private sealed record CellChoice(int Row, int Column, TableCell Cell)
    {
        public override string ToString()
            => $"{(Row == 0 ? "H" : $"R{Row}")}C{Column + 1}: {Cell.Inlines.ToPlainText()}";
    }
}
