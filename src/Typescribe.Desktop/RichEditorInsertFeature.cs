using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Application.Abstractions;
using Typescribe.Desktop.Editing;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop;

/// <summary>
/// Adds lightweight, manuscript-first insertion tools without changing canonical text storage.
/// Tables are still stored as Markdown, but can be created and edited through a visual grid.
/// </summary>
internal sealed class RichEditorInsertFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly IDocumentParser _parser;
    private readonly HashSet<ManuscriptEditor> _editors = [];
    private ManuscriptEditor? _lastEditor;
    private bool _menuInjected;
    private bool _discoverScheduled;
    private bool _slashMenuOpen;
    private bool _disposed;
    private int _suppressSlashDetection;

    private RichEditorInsertFeature(StudioWorkspaceWindow window, WorkspaceViewModel viewModel, IDocumentParser parser)
    {
        _window = window;
        _viewModel = viewModel;
        _parser = parser;
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel, IDocumentParser parser)
    {
        var feature = new RichEditorInsertFeature(window, viewModel, parser);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        feature.ScheduleDiscover();
    }

    private void OnOpened(object? sender, EventArgs e) => ScheduleDiscover();
    private void OnLayoutUpdated(object? sender, EventArgs e) => ScheduleDiscover();

    private void ScheduleDiscover()
    {
        if (_disposed || _discoverScheduled) return;
        _discoverScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _discoverScheduled = false;
            if (!_disposed) Discover();
        }, DispatcherPriority.Background);
    }

    private void Discover()
    {
        InjectInsertMenu();
        foreach (var editor in _window.GetVisualDescendants().OfType<ManuscriptEditor>())
        {
            if (!_editors.Add(editor)) continue;
            editor.GotFocus += EditorGotFocus;
            editor.TextChanged += EditorTextChanged;
            _lastEditor ??= editor;
        }
    }

    private void EditorGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is ManuscriptEditor editor) _lastEditor = editor;
    }

    private void EditorTextChanged(object? sender, EventArgs e)
    {
        if (_disposed || _slashMenuOpen || _suppressSlashDetection > 0 || sender is not ManuscriptEditor editor) return;
        Dispatcher.UIThread.Post(() => TryOpenSlashMenu(editor), DispatcherPriority.Input);
    }

    private async void TryOpenSlashMenu(ManuscriptEditor editor)
    {
        if (_disposed || _slashMenuOpen || !_viewModel.HasDocument || !TryGetSlashOffset(editor, out var slashOffset)) return;
        _slashMenuOpen = true;
        try
        {
            await ShowSlashMenuAsync(editor, slashOffset);
        }
        finally
        {
            _slashMenuOpen = false;
        }
    }

    private static bool TryGetSlashOffset(ManuscriptEditor editor, out int slashOffset)
    {
        slashOffset = -1;
        if (editor.CaretIndex <= 0 || editor.Document.TextLength == 0) return false;
        var caret = Math.Clamp(editor.CaretIndex, 0, editor.Document.TextLength);
        var line = editor.Document.GetLineByOffset(Math.Max(0, caret - 1));
        var prefix = editor.Document.GetText(line.Offset, caret - line.Offset);
        if (!string.Equals(prefix.TrimStart(), "/", StringComparison.Ordinal)) return false;
        var relative = prefix.LastIndexOf('/');
        if (relative < 0) return false;
        slashOffset = line.Offset + relative;
        return true;
    }

    private void InjectInsertMenu()
    {
        if (_menuInjected) return;
        var menu = _window.GetVisualDescendants().OfType<Menu>().FirstOrDefault();
        if (menu?.ItemsSource is not IEnumerable source) return;
        var insert = source.Cast<object?>().OfType<MenuItem>()
            .FirstOrDefault(item => string.Equals(NormalizeHeader(item.Header?.ToString()), "Insert", StringComparison.OrdinalIgnoreCase));
        if (insert is null) return;

        var items = MenuItems(insert.ItemsSource);
        if (items.OfType<MenuItem>().Any(item => string.Equals(NormalizeHeader(item.Header?.ToString()), "Quick Table…", StringComparison.OrdinalIgnoreCase)))
        {
            _menuInjected = true;
            return;
        }

        items.Add(new Separator());
        items.Add(Command("Quick _Table…", async () => await ShowQuickTableAsync(ResolveEditor())));
        items.Add(Command("Edit Current Ta_ble…", async () => await EditCurrentTableAsync(ResolveEditor())));
        items.Add(Command("Table Row / _Column…", async () => await ShowTableActionsAsync(ResolveEditor())));
        items.Add(Command("_UML (Mermaid)…", async () => await InsertUmlAsync(ResolveEditor())));
        items.Add(Command("_ABC Music…", async () => await InsertAbcAsync(ResolveEditor())));
        items.Add(Command("_Emoji…", async () => await ShowEmojiPickerAsync(ResolveEditor())));
        insert.ItemsSource = items.ToArray();
        _menuInjected = true;
    }

    private async Task ShowSlashMenuAsync(ManuscriptEditor editor, int slashOffset)
    {
        _lastEditor = editor;
        var commands = new[]
        {
            new SlashCommand("Table", "Choose rows × columns, then edit visually", () => ShowQuickTableAsync(editor)),
            new SlashCommand("Edit table", "Open the table at the caret in the visual editor", () => EditCurrentTableAsync(editor)),
            new SlashCommand("Table row / column", "Insert or remove rows and columns around the caret", () => ShowTableActionsAsync(editor)),
            new SlashCommand("UML", "Insert a Mermaid UML fenced block", () => InsertUmlAsync(editor)),
            new SlashCommand("ABC music", "Insert an ABC notation fenced block", () => InsertAbcAsync(editor)),
            new SlashCommand("Emoji", "Insert Unicode emoji or Markdown :shortcode: markup", () => ShowEmojiPickerAsync(editor))
        };

        var search = new TextBox { Watermark = "Type a command…", Margin = new Thickness(10) };
        var list = new ListBox { ItemsSource = commands, SelectedIndex = 0, Margin = new Thickness(10, 0, 10, 0), MinHeight = 210 };
        var cancel = new Button { Content = "Cancel", MinWidth = 88 };
        var run = new Button { Content = "Insert", MinWidth = 88 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(10),
            Children = { cancel, run }
        };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(search);
        Grid.SetRow(list, 1); root.Children.Add(list);
        Grid.SetRow(buttons, 2); root.Children.Add(buttons);
        var dialog = new Window
        {
            Title = "Insert /",
            Width = 520,
            Height = 370,
            MinWidth = 420,
            MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };

        void Filter()
        {
            var query = search.Text?.Trim() ?? string.Empty;
            list.ItemsSource = commands.Where(command => query.Length == 0 || command.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            list.SelectedIndex = 0;
        }

        search.TextChanged += (_, _) => Filter();
        run.Click += (_, _) => dialog.Close(list.SelectedItem as SlashCommand);
        cancel.Click += (_, _) => dialog.Close(null);
        list.DoubleTapped += (_, _) => dialog.Close(list.SelectedItem as SlashCommand);
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; dialog.Close(null); }
            else if (e.Key == Key.Enter && list.SelectedItem is SlashCommand command) { e.Handled = true; dialog.Close(command); }
        };

        var selected = await dialog.ShowDialog<SlashCommand?>(_window);
        if (selected is null) return;
        RemoveSlash(editor, slashOffset);
        await selected.Action();
    }

    private void RemoveSlash(ManuscriptEditor editor, int slashOffset)
    {
        if (slashOffset < 0 || slashOffset >= editor.Document.TextLength || editor.Document.GetCharAt(slashOffset) != '/') return;
        _suppressSlashDetection++;
        try
        {
            editor.Document.Remove(slashOffset, 1);
            editor.CaretIndex = Math.Min(slashOffset, editor.Document.TextLength);
        }
        finally
        {
            _suppressSlashDetection--;
        }
    }

    private async Task ShowQuickTableAsync(ManuscriptEditor? editor)
    {
        if (editor is null || !_viewModel.HasDocument) return;
        var size = await PickTableSizeAsync();
        if (size is null) return;

        var rows = new List<IReadOnlyList<string>>();
        rows.Add(Enumerable.Range(1, size.Columns).Select(index => $"Heading {index}").ToArray());
        for (var row = 1; row < size.Rows; row++) rows.Add(Enumerable.Repeat(string.Empty, size.Columns).ToArray());
        var seed = new TableEditResult(rows, HeaderRow: true, Enumerable.Repeat(TableAlignment.Default, size.Columns).ToArray(), string.Empty, string.Empty);
        var result = await TableEditorWindow.ShowAsync(_window, seed);
        if (result is null) return;
        InsertBlock(editor, SerializeTable(result));
    }

    private async Task<TableSize?> PickTableSizeAsync()
    {
        const int maxRows = 8;
        const int maxColumns = 8;
        var status = new TextBlock { Text = "2 × 2 table", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        for (var column = 0; column < maxColumns; column++) grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(30)));
        for (var row = 0; row < maxRows; row++) grid.RowDefinitions.Add(new RowDefinition(new GridLength(30)));

        var buttons = new Button[maxRows, maxColumns];
        void Highlight(int row, int column)
        {
            status.Text = $"{row + 1} × {column + 1} table";
            for (var r = 0; r < maxRows; r++)
                for (var c = 0; c < maxColumns; c++)
                    buttons[r, c].Opacity = r <= row && c <= column ? 1.0 : 0.48;
        }

        var dialog = new Window
        {
            Title = "Insert Table",
            Width = 360,
            Height = 365,
            MinWidth = 340,
            MinHeight = 340,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        for (var row = 0; row < maxRows; row++)
        {
            for (var column = 0; column < maxColumns; column++)
            {
                var r = row;
                var c = column;
                var button = new Button { Width = 26, Height = 26, Margin = new Thickness(1), Padding = new Thickness(0), Content = string.Empty };
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
                new TextBlock { Text = "Drag over the grid and click a size", FontWeight = Avalonia.Media.FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                status,
                grid,
                new TextBlock { Text = "The selected table opens in the visual editor before insertion.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.72, HorizontalAlignment = HorizontalAlignment.Center }
            }
        };
        return await dialog.ShowDialog<TableSize?>(_window);
    }

    private async Task EditCurrentTableAsync(ManuscriptEditor? editor)
    {
        if (editor is null || !_viewModel.HasDocument) return;
        var span = FindCurrentTableSpan(editor);
        if (span is null) return;
        var seed = SeedFromTable(span.Text);
        if (seed is null) return;
        var result = await TableEditorWindow.ShowAsync(_window, seed);
        if (result is null) return;
        ReplaceTable(editor, span, result);
    }

    private async Task ShowTableActionsAsync(ManuscriptEditor? editor)
    {
        if (editor is null || !_viewModel.HasDocument) return;
        var span = FindCurrentTableSpan(editor);
        if (span is null) return;
        var seed = SeedFromTable(span.Text);
        if (seed is null) return;
        var coordinate = GetTableCoordinate(editor, span, seed);

        var actions = new[]
        {
            new TableAction("Insert row above", "row-above"),
            new TableAction("Insert row below", "row-below"),
            new TableAction("Delete current row", "row-delete"),
            new TableAction("Insert column left", "column-left"),
            new TableAction("Insert column right", "column-right"),
            new TableAction("Delete current column", "column-delete"),
            new TableAction("Open visual table editor", "edit")
        };
        var list = new ListBox { ItemsSource = actions, SelectedIndex = 0, Margin = new Thickness(12), MinHeight = 220 };
        var apply = new Button { Content = "Apply", MinWidth = 88 };
        var cancel = new Button { Content = "Cancel", MinWidth = 88 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(12), Children = { cancel, apply } };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(new TextBlock { Text = $"Cell {coordinate.Row + 1}, {coordinate.Column + 1}", Margin = new Thickness(12, 12, 12, 0), FontWeight = Avalonia.Media.FontWeight.SemiBold });
        Grid.SetRow(list, 1); root.Children.Add(list);
        Grid.SetRow(buttons, 2); root.Children.Add(buttons);
        var dialog = new Window { Title = "Table row / column", Width = 430, Height = 390, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root };
        apply.Click += (_, _) => dialog.Close(list.SelectedItem as TableAction);
        cancel.Click += (_, _) => dialog.Close(null);
        list.DoubleTapped += (_, _) => dialog.Close(list.SelectedItem as TableAction);
        var action = await dialog.ShowDialog<TableAction?>(_window);
        if (action is null) return;
        if (action.Id == "edit") { await EditCurrentTableAsync(editor); return; }

        var cells = seed.Cells.Select(row => row.ToList()).ToList();
        var alignments = seed.Alignments.ToList();
        var row = Math.Clamp(coordinate.Row, 0, Math.Max(0, cells.Count - 1));
        var column = Math.Clamp(coordinate.Column, 0, Math.Max(0, cells[0].Count - 1));
        switch (action.Id)
        {
            case "row-above": cells.Insert(row, Enumerable.Repeat(string.Empty, cells[0].Count).ToList()); break;
            case "row-below": cells.Insert(Math.Min(row + 1, cells.Count), Enumerable.Repeat(string.Empty, cells[0].Count).ToList()); break;
            case "row-delete": if (cells.Count > 1) cells.RemoveAt(row); break;
            case "column-left":
                foreach (var current in cells) current.Insert(column, string.Empty);
                alignments.Insert(column, TableAlignment.Default);
                break;
            case "column-right":
                foreach (var current in cells) current.Insert(Math.Min(column + 1, current.Count), string.Empty);
                alignments.Insert(Math.Min(column + 1, alignments.Count), TableAlignment.Default);
                break;
            case "column-delete":
                if (cells[0].Count > 1)
                {
                    foreach (var current in cells) current.RemoveAt(column);
                    if (column < alignments.Count) alignments.RemoveAt(column);
                }
                break;
        }
        ReplaceTable(editor, span, new TableEditResult(cells.Select(rowCells => (IReadOnlyList<string>)rowCells.ToArray()).ToArray(), seed.HeaderRow, alignments, seed.Caption, seed.Identifier));
    }

    private async Task InsertUmlAsync(ManuscriptEditor? editor)
    {
        if (editor is null || !_viewModel.HasDocument) return;
        var source = new TextBox
        {
            Text = "classDiagram\n    class Example {\n        +string Name\n        +Run()\n    }",
            AcceptsReturn = true,
            AcceptsTab = true,
            MinHeight = 260,
            FontFamily = new Avalonia.Media.FontFamily("monospace")
        };
        if (!await SourceDialogAsync("Insert UML (Mermaid)", "Mermaid UML source", source)) return;
        InsertBlock(editor, $"```mermaid\n{(source.Text ?? string.Empty).TrimEnd()}\n```");
    }

    private async Task InsertAbcAsync(ManuscriptEditor? editor)
    {
        if (editor is null || !_viewModel.HasDocument) return;
        var source = new TextBox
        {
            Text = "X:1\nT:Untitled\nM:4/4\nL:1/8\nK:C\nCDEF GABc|",
            AcceptsReturn = true,
            AcceptsTab = true,
            MinHeight = 260,
            FontFamily = new Avalonia.Media.FontFamily("monospace")
        };
        if (!await SourceDialogAsync("Insert ABC Music", "ABC notation", source)) return;
        InsertBlock(editor, $"```abc\n{(source.Text ?? string.Empty).TrimEnd()}\n```");
    }

    private async Task<bool> SourceDialogAsync(string title, string label, TextBox source)
    {
        var insert = new Button { Content = "Insert", MinWidth = 88 };
        var cancel = new Button { Content = "Cancel", MinWidth = 88 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, insert } };
        var dialog = new Window
        {
            Title = title,
            Width = 680,
            Height = 500,
            MinWidth = 520,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(14), Spacing = 8, Children = { new TextBlock { Text = label, FontWeight = Avalonia.Media.FontWeight.SemiBold }, source, buttons } }
        };
        insert.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(_window);
    }

    private async Task ShowEmojiPickerAsync(ManuscriptEditor? editor)
    {
        if (editor is null || !_viewModel.HasDocument) return;
        var choices = EmojiChoices;
        var search = new TextBox { Watermark = "Search emoji…", Margin = new Thickness(10) };
        var list = new ListBox { ItemsSource = choices, SelectedIndex = 0, Margin = new Thickness(10, 0, 10, 0), MinHeight = 260 };
        var shortcode = new CheckBox { Content = "Insert Markdown :shortcode: instead of Unicode", IsChecked = false, Margin = new Thickness(10, 6) };
        var insert = new Button { Content = "Insert", MinWidth = 88 };
        var cancel = new Button { Content = "Cancel", MinWidth = 88 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(10), Children = { cancel, insert } };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };
        root.Children.Add(search);
        Grid.SetRow(list, 1); root.Children.Add(list);
        Grid.SetRow(shortcode, 2); root.Children.Add(shortcode);
        Grid.SetRow(buttons, 3); root.Children.Add(buttons);
        var dialog = new Window { Title = "Emoji", Width = 480, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root };
        search.TextChanged += (_, _) =>
        {
            var query = search.Text?.Trim() ?? string.Empty;
            list.ItemsSource = choices.Where(choice => query.Length == 0 || choice.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            list.SelectedIndex = 0;
        };
        insert.Click += (_, _) => dialog.Close(list.SelectedItem as EmojiChoice);
        cancel.Click += (_, _) => dialog.Close(null);
        list.DoubleTapped += (_, _) => dialog.Close(list.SelectedItem as EmojiChoice);
        var selected = await dialog.ShowDialog<EmojiChoice?>(_window);
        if (selected is null) return;
        InsertText(editor, shortcode.IsChecked == true ? $":{selected.Shortcode}:" : selected.Emoji);
    }

    private TableEditResult? SeedFromTable(string text)
    {
        var table = _parser.Parse(text).Blocks.OfType<TableBlock>().FirstOrDefault();
        if (table is null) return null;
        var rows = new List<IReadOnlyList<string>> { table.Header.Select(cell => cell.Inlines.ToPlainText()).ToArray() };
        rows.AddRange(table.Rows.Select(row => (IReadOnlyList<string>)row.Select(cell => cell.Inlines.ToPlainText()).ToArray()));
        return new TableEditResult(rows, HeaderRow: true, table.Alignments ?? [], table.Caption ?? string.Empty, table.Identifier ?? string.Empty);
    }

    private TableSpan? FindCurrentTableSpan(ManuscriptEditor editor)
    {
        if (editor.Document.TextLength == 0) return null;
        var line = editor.Document.GetLineByOffset(Math.Clamp(editor.CaretIndex, 0, editor.Document.TextLength));
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
            ? new TableSpan(first.Offset, length, text, start, firstTableLine, end)
            : null;
    }

    private static TableCoordinate GetTableCoordinate(ManuscriptEditor editor, TableSpan span, TableEditResult seed)
    {
        var line = editor.Document.GetLineByOffset(Math.Clamp(editor.CaretIndex, 0, editor.Document.TextLength));
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
        var rows = table.Cells.Select(row => row.Select(EscapeCell).ToArray()).ToArray();
        var columns = rows.Length == 0 ? 1 : Math.Max(1, rows.Max(row => row.Length));
        var header = table.HeaderRow && rows.Length > 0 ? rows[0] : Enumerable.Repeat(string.Empty, columns).ToArray();
        var body = table.HeaderRow ? rows.Skip(1) : rows;
        string Row(IEnumerable<string> cells) => "| " + string.Join(" | ", cells.Concat(Enumerable.Repeat(string.Empty, Math.Max(0, columns - cells.Count()))).Take(columns)) + " |";
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
        editor.Select(span.Offset, markup.Length);
        editor.Focus();
    }

    private static void InsertBlock(ManuscriptEditor editor, string text)
    {
        var offset = editor.SelectionLength > 0 ? editor.SelectionStart : editor.CaretIndex;
        if (editor.SelectionLength > 0) editor.Document.Remove(editor.SelectionStart, editor.SelectionLength);
        var prefix = offset > 0 && editor.Document.GetCharAt(offset - 1) != '\n' ? Environment.NewLine + Environment.NewLine : string.Empty;
        var suffix = offset < editor.Document.TextLength && editor.Document.GetCharAt(offset) != '\n' ? Environment.NewLine + Environment.NewLine : Environment.NewLine;
        var insertion = prefix + text + suffix;
        editor.Document.Insert(offset, insertion);
        editor.CaretIndex = offset + prefix.Length + text.Length;
        editor.Focus();
    }

    private static void InsertText(ManuscriptEditor editor, string text)
    {
        var offset = editor.SelectionLength > 0 ? editor.SelectionStart : editor.CaretIndex;
        if (editor.SelectionLength > 0) editor.Document.Replace(offset, editor.SelectionLength, text);
        else editor.Document.Insert(offset, text);
        editor.CaretIndex = offset + text.Length;
        editor.Focus();
    }

    private ManuscriptEditor? ResolveEditor()
        => _lastEditor?.IsVisible == true
            ? _lastEditor
            : _window.GetVisualDescendants().OfType<ManuscriptEditor>().FirstOrDefault(candidate => candidate.DocumentIdentity is not null)
              ?? _window.GetVisualDescendants().OfType<ManuscriptEditor>().FirstOrDefault();

    private static MenuItem Command(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    private static List<object?> MenuItems(object? source)
        => source is IEnumerable enumerable ? enumerable.Cast<object?>().ToList() : [];

    private static string NormalizeHeader(string? value)
        => (value ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal);

    private static string EscapeCell(string value)
        => (value ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        foreach (var editor in _editors)
        {
            editor.GotFocus -= EditorGotFocus;
            editor.TextChanged -= EditorTextChanged;
        }
        _editors.Clear();
    }

    private sealed record SlashCommand(string Label, string Description, Func<Task> Action)
    {
        public string SearchText => Label + " " + Description;
        public override string ToString() => $"{Label} — {Description}";
    }

    private sealed record TableAction(string Label, string Id)
    {
        public override string ToString() => Label;
    }

    private sealed record TableSize(int Rows, int Columns);
    private sealed record TableCoordinate(int Row, int Column);
    private sealed record TableSpan(int Offset, int Length, string Text, int StartLine, int FirstTableLine, int EndLine);
    private sealed record EmojiChoice(string Emoji, string Shortcode, string Name)
    {
        public string SearchText => Shortcode + " " + Name;
        public override string ToString() => $"{Emoji}  :{Shortcode}:  {Name}";
    }

    private static readonly EmojiChoice[] EmojiChoices =
    [
        new("😀", "grinning", "grinning face"), new("😃", "smiley", "smiley face"), new("😄", "smile", "smile"),
        new("😂", "joy", "tears of joy"), new("😊", "blush", "blush"), new("😍", "heart_eyes", "heart eyes"),
        new("🤔", "thinking", "thinking"), new("👍", "thumbsup", "thumbs up"), new("👎", "thumbsdown", "thumbs down"),
        new("👏", "clap", "clapping"), new("🙏", "pray", "folded hands"), new("❤️", "heart", "heart"),
        new("💡", "bulb", "idea"), new("✅", "white_check_mark", "check mark"), new("❌", "x", "cross mark"),
        new("⚠️", "warning", "warning"), new("⭐", "star", "star"), new("🔥", "fire", "fire"),
        new("🎵", "musical_note", "musical note"), new("🎉", "tada", "celebration"), new("🚀", "rocket", "rocket")
    ];
}
