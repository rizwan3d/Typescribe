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

namespace Typescribe.Desktop;

/// <summary>
/// Adds a persistent Table button to the manuscript toolbar and a reliable table context menu.
/// The commands operate on canonical Markdown tables, so they remain compatible with publishing.
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
        var panels = _window.GetVisualDescendants().OfType<StackPanel>()
            .Where(static panel => panel.Orientation == Orientation.Horizontal)
            .ToArray();
        foreach (var panel in panels)
        {
            var buttonLabels = panel.Children.OfType<Button>()
                .Select(static button => button.Content?.ToString() ?? string.Empty)
                .ToHashSet(StringComparer.Ordinal);
            if (!buttonLabels.Contains("B") || !buttonLabels.Contains("I") || !buttonLabels.Contains("H1") || !buttonLabels.Contains("H2"))
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
            ToolTip.SetTip(table, "Quick Table — choose rows × columns and edit visually");
            table.Click += async (_, _) => await ShowQuickTableAsync(ResolveEditor());

            var h2 = panel.Children.OfType<Button>().FirstOrDefault(static button => string.Equals(button.Content?.ToString(), "H2", StringComparison.Ordinal));
            var insertAt = panel.Children.Count;
            if (h2 is not null)
            {
                for (var index = 0; index < panel.Children.Count; index++)
                {
                    if (!ReferenceEquals(panel.Children[index], h2)) continue;
                    insertAt = Math.Min(index + 1, panel.Children.Count);
                    break;
                }
            }
            panel.Children.Insert(insertAt, table);
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
        if (existing.OfType<MenuItem>().Any(static item => string.Equals(item.Header?.ToString()?.Replace("_", string.Empty, StringComparison.Ordinal), "Table", StringComparison.OrdinalIgnoreCase)))
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
            Command("Insert Row _Above", () => MutateCurrentTableAsync(editor, TableMutation.RowAbove)),
            Command("Insert Row _Below", () => MutateCurrentTableAsync(editor, TableMutation.RowBelow)),
            Command("_Delete Current Row", () => MutateCurrentTableAsync(editor, TableMutation.DeleteRow)),
            new Separator(),
            Command("Insert Column _Left", () => MutateCurrentTableAsync(editor, TableMutation.ColumnLeft)),
            Command("Insert Column _Right", () => MutateCurrentTableAsync(editor, TableMutation.ColumnRight)),
            Command("Delete Current _Column", () => MutateCurrentTableAsync(editor, TableMutation.DeleteColumn)),
            new Separator(),
            Command("Delete _Table", () => MutateCurrentTableAsync(editor, TableMutation.DeleteTable))
        };
        existing.Add(new Separator());
        existing.Add(tableMenu);
        editor.ContextMenu.ItemsSource = existing;
        _contextMenusInstalled.Add(editor);
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
        InsertBlock(editor, SerializeTable(result));
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
                    Padding = new Thickness(0),
                    Content = string.Empty
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
                new TextBlock
                {
                    Text = "Choose rows × columns",
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                status,
                grid,
                new TextBlock
                {
                    Text = "The table opens in the visual editor before insertion.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Opacity = 0.72,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            }
        };
        return await dialog.ShowDialog<TableSize?>(_window);
    }

    private async Task EditCurrentTableAsync(ManuscriptEditor editor)
    {
        if (!_viewModel.HasDocument) return;
        var span = FindCurrentTableSpan(editor);
        if (span is null) return;
        var seed = SeedFromTable(span.Text);
        if (seed is null) return;
        var result = await TableEditorWindow.ShowAsync(_window, seed);
        if (result is null) return;
        ReplaceTable(editor, span, result);
    }

    private Task MutateCurrentTableAsync(ManuscriptEditor editor, TableMutation mutation)
    {
        if (!_viewModel.HasDocument) return Task.CompletedTask;
        var span = FindCurrentTableSpan(editor);
        if (span is null) return Task.CompletedTask;
        if (mutation == TableMutation.DeleteTable)
        {
            DeleteTable(editor, span);
            return Task.CompletedTask;
        }

        var seed = SeedFromTable(span.Text);
        if (seed is null || seed.Cells.Count == 0 || seed.Cells[0].Count == 0) return Task.CompletedTask;
        var coordinate = GetTableCoordinate(editor, span, seed);
        var cells = seed.Cells.Select(static row => row.ToList()).ToList();
        var alignments = seed.Alignments.ToList();
        while (alignments.Count < cells[0].Count) alignments.Add(TableAlignment.Default);
        var row = Math.Clamp(coordinate.Row, 0, cells.Count - 1);
        var column = Math.Clamp(coordinate.Column, 0, cells[0].Count - 1);

        switch (mutation)
        {
            case TableMutation.RowAbove:
                cells.Insert(row, Enumerable.Repeat(string.Empty, cells[0].Count).ToList());
                break;
            case TableMutation.RowBelow:
                cells.Insert(Math.Min(row + 1, cells.Count), Enumerable.Repeat(string.Empty, cells[0].Count).ToList());
                break;
            case TableMutation.DeleteRow:
                if (cells.Count == 1)
                {
                    DeleteTable(editor, span);
                    return Task.CompletedTask;
                }
                cells.RemoveAt(row);
                break;
            case TableMutation.ColumnLeft:
                foreach (var current in cells) current.Insert(column, string.Empty);
                alignments.Insert(column, TableAlignment.Default);
                break;
            case TableMutation.ColumnRight:
                foreach (var current in cells) current.Insert(column + 1, string.Empty);
                alignments.Insert(column + 1, TableAlignment.Default);
                break;
            case TableMutation.DeleteColumn:
                if (cells[0].Count == 1)
                {
                    DeleteTable(editor, span);
                    return Task.CompletedTask;
                }
                foreach (var current in cells) current.RemoveAt(column);
                alignments.RemoveAt(column);
                break;
        }

        ReplaceTable(
            editor,
            span,
            new TableEditResult(
                cells.Select(static rowCells => (IReadOnlyList<string>)rowCells.ToArray()).ToArray(),
                seed.HeaderRow,
                alignments,
                seed.Caption,
                seed.Identifier));
        return Task.CompletedTask;
    }

    private TableEditResult? SeedFromTable(string text)
    {
        var table = _parser.Parse(text).Blocks.OfType<TableBlock>().FirstOrDefault();
        if (table is null) return null;
        var rows = new List<IReadOnlyList<string>>
        {
            table.Header.Select(static cell => cell.Inlines.ToPlainText()).ToArray()
        };
        rows.AddRange(table.Rows.Select(static row => (IReadOnlyList<string>)row.Select(static cell => cell.Inlines.ToPlainText()).ToArray()));
        return new TableEditResult(rows, HeaderRow: true, table.Alignments ?? [], table.Caption ?? string.Empty, table.Identifier ?? string.Empty);
    }

    private TableSpan? FindCurrentTableSpan(ManuscriptEditor editor)
    {
        if (editor.Document.TextLength == 0) return null;
        var caret = Math.Clamp(editor.CaretIndex, 0, editor.Document.TextLength);
        if (caret == editor.Document.TextLength && caret > 0) caret--;
        var line = editor.Document.GetLineByOffset(caret);
        var lineNumber = line.LineNumber;

        bool TableLine(int number)
        {
            if (number < 1 || number > editor.Document.LineCount) return false;
            var candidate = editor.Document.GetLineByNumber(number);
            return editor.Document.GetText(candidate.Offset, candidate.Length).Contains('|');
        }

        if (!TableLine(lineNumber))
        {
            var current = editor.Document.GetText(line.Offset, line.Length).Trim();
            if (current.StartsWith("<!-- typescribe:table ", StringComparison.Ordinal) && TableLine(lineNumber + 1)) lineNumber++;
            else return null;
        }

        var start = lineNumber;
        var end = lineNumber;
        while (TableLine(start - 1)) start--;
        while (TableLine(end + 1)) end++;
        var firstTableLine = start;
        if (start > 1)
        {
            var metadata = editor.Document.GetLineByNumber(start - 1);
            var metadataText = editor.Document.GetText(metadata.Offset, metadata.Length).Trim();
            if (metadataText.StartsWith("<!-- typescribe:table ", StringComparison.Ordinal)) start--;
        }

        var first = editor.Document.GetLineByNumber(start);
        var last = editor.Document.GetLineByNumber(end);
        var length = last.EndOffset - first.Offset;
        var text = editor.Document.GetText(first.Offset, length);
        return _parser.Parse(text).Blocks.OfType<TableBlock>().Any()
            ? new TableSpan(first.Offset, length, text, firstTableLine)
            : null;
    }

    private static TableCoordinate GetTableCoordinate(ManuscriptEditor editor, TableSpan span, TableEditResult seed)
    {
        var caret = Math.Clamp(editor.CaretIndex, 0, editor.Document.TextLength);
        if (caret == editor.Document.TextLength && caret > 0) caret--;
        var line = editor.Document.GetLineByOffset(caret);
        var separatorLine = span.FirstTableLine + 1;
        var row = line.LineNumber <= separatorLine ? 0 : line.LineNumber - separatorLine;
        row = Math.Clamp(row, 0, Math.Max(0, seed.Cells.Count - 1));

        var lineText = editor.Document.GetText(line.Offset, line.Length);
        var relativeCaret = Math.Clamp(editor.CaretIndex - line.Offset, 0, lineText.Length);
        var pipes = 0;
        for (var index = 0; index < relativeCaret; index++)
        {
            if (lineText[index] == '|' && (index == 0 || lineText[index - 1] != '\\')) pipes++;
        }
        var column = Math.Clamp(Math.Max(0, pipes - 1), 0, Math.Max(0, seed.Cells[0].Count - 1));
        return new TableCoordinate(row, column);
    }

    private static string SerializeTable(TableEditResult table)
    {
        var rows = table.Cells.Select(static row => row.Select(EscapeCell).ToArray()).ToArray();
        var columns = rows.Length == 0 ? 1 : Math.Max(1, rows.Max(static row => row.Length));
        var header = table.HeaderRow && rows.Length > 0 ? rows[0] : Enumerable.Repeat(string.Empty, columns).ToArray();
        var body = table.HeaderRow ? rows.Skip(1) : rows;
        string Row(IEnumerable<string> cells)
            => "| " + string.Join(" | ", cells.Concat(Enumerable.Repeat(string.Empty, Math.Max(0, columns - cells.Count()))).Take(columns)) + " |";
        var separator = Enumerable.Range(0, columns).Select(index => index < table.Alignments.Count ? table.Alignments[index] switch
        {
            TableAlignment.Left => ":---",
            TableAlignment.Center => ":---:",
            TableAlignment.Right => "---:",
            _ => "---"
        } : "---");
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(table.Caption) || !string.IsNullOrWhiteSpace(table.Identifier))
            lines.Add(Typescribe.Application.Services.AdvancedDocumentParser.CreateTableMetadata(table.Identifier, table.Caption));
        lines.Add(Row(header));
        lines.Add(Row(separator));
        lines.AddRange(body.Select(Row));
        return string.Join(Environment.NewLine, lines);
    }

    private static void ReplaceTable(ManuscriptEditor editor, TableSpan span, TableEditResult table)
    {
        var markup = SerializeTable(table);
        editor.Document.Replace(span.Offset, span.Length, markup);
        editor.CaretIndex = Math.Clamp(span.Offset + Math.Min(2, markup.Length), 0, editor.Document.TextLength);
        editor.Focus();
    }

    private static void DeleteTable(ManuscriptEditor editor, TableSpan span)
    {
        var start = span.Offset;
        var end = span.Offset + span.Length;
        if (end < editor.Document.TextLength && editor.Document.GetCharAt(end) == '\r') end++;
        if (end < editor.Document.TextLength && editor.Document.GetCharAt(end) == '\n') end++;
        editor.Document.Remove(start, Math.Max(0, end - start));
        editor.CaretIndex = Math.Min(start, editor.Document.TextLength);
        editor.Focus();
    }

    private static void InsertBlock(ManuscriptEditor editor, string text)
    {
        var offset = editor.SelectionLength > 0 ? editor.SelectionStart : editor.CaretIndex;
        if (editor.SelectionLength > 0) editor.Document.Remove(editor.SelectionStart, editor.SelectionLength);
        var prefix = offset > 0 && editor.Document.GetCharAt(offset - 1) != '\n'
            ? Environment.NewLine + Environment.NewLine
            : string.Empty;
        var suffix = offset < editor.Document.TextLength && editor.Document.GetCharAt(offset) != '\n'
            ? Environment.NewLine + Environment.NewLine
            : Environment.NewLine;
        var insertion = prefix + text + suffix;
        editor.Document.Insert(offset, insertion);
        editor.CaretIndex = offset + prefix.Length + text.Length;
        editor.Focus();
    }

    private ManuscriptEditor? ResolveEditor()
        => _lastEditor?.IsVisible == true
            ? _lastEditor
            : _window.GetVisualDescendants().OfType<ManuscriptEditor>().FirstOrDefault(static candidate => candidate.DocumentIdentity is not null)
              ?? _window.GetVisualDescendants().OfType<ManuscriptEditor>().FirstOrDefault();

    private static MenuItem Command(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    private static string EscapeCell(string value)
        => (value ?? string.Empty)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

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

    private enum TableMutation
    {
        RowAbove,
        RowBelow,
        DeleteRow,
        ColumnLeft,
        ColumnRight,
        DeleteColumn,
        DeleteTable
    }

    private sealed record TableSize(int Rows, int Columns);
    private sealed record TableCoordinate(int Row, int Column);
    private sealed record TableSpan(int Offset, int Length, string Text, int FirstTableLine);
}
