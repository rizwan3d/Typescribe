using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Typescribe.Application.Abstractions;
using Typescribe.Application.Services;
using Typescribe.Domain.Models;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Replaces the active Markdown table's visual presentation with a compact Word-style editing
/// surface while keeping the underlying document as canonical text. Direct cell edits and
/// structural commands serialize back to the pipe table immediately when focus leaves a cell.
/// </summary>
internal sealed class InlineTableEditingFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly IDocumentParser _parser = new EmojiDocumentParser(new AdvancedDocumentParser());
    private readonly DispatcherTimer _refreshTimer;
    private ManuscriptEditor? _editor;
    private Grid? _host;
    private Border? _surface;
    private InlineTableMaskTransformer? _mask;
    private TableSpan? _span;
    private TableEditResult? _edit;
    private int _activeRow;
    private int _activeColumn;
    private bool _installed;
    private bool _committing;
    private bool _disposed;

    private InlineTableEditingFeature(StudioWorkspaceWindow window)
    {
        _window = window;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RefreshFromCaret();
        };
    }

    public static void Apply(StudioWorkspaceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var feature = new InlineTableEditingFeature(window);
        window.Opened += feature.WindowOpened;
        window.LayoutUpdated += feature.WindowLayoutUpdated;
        window.Closed += feature.WindowClosed;
        feature.TryInstall();
    }

    private void WindowOpened(object? sender, EventArgs e) => TryInstall();

    private void WindowLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_installed) TryInstall();
    }

    private void TryInstall()
    {
        if (_disposed || _installed) return;
        var host = _window.GetVisualDescendants().OfType<Grid>()
            .FirstOrDefault(static grid => grid.Classes.Contains("long-form-editor-host"));
        if (host is null) return;
        var editor = host.Children.OfType<ManuscriptEditor>().FirstOrDefault();
        if (editor is null) return;

        _host = host;
        _editor = editor;
        if (host.RowDefinitions.Count >= 4)
        {
            host.RowDefinitions.Insert(2, new RowDefinition(GridLength.Auto));
            foreach (var child in host.Children.OfType<Control>().ToArray())
            {
                var row = Grid.GetRow(child);
                if (row >= 2) Grid.SetRow(child, row + 1);
            }
        }

        _surface = new Border
        {
            IsVisible = false,
            Margin = new Thickness(8, 5, 8, 5),
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(64, 128, 128, 128)),
            Background = new SolidColorBrush(Color.FromArgb(16, 128, 128, 128)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxHeight = 420
        };
        _surface.Classes.Add("inline-table-editor");
        Grid.SetRow(_surface, 2);
        host.Children.Add(_surface);

        _mask = new InlineTableMaskTransformer();
        editor.TextArea.TextView.LineTransformers.Add(_mask);
        editor.TextArea.Caret.PositionChanged += CaretPositionChanged;
        editor.TextChanged += EditorTextChanged;
        _installed = true;
        QueueRefresh();
    }

    private void CaretPositionChanged(object? sender, EventArgs e) => QueueRefresh();

    private void EditorTextChanged(object? sender, EventArgs e)
    {
        if (!_committing) QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (_disposed || !_installed) return;
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void RefreshFromCaret()
    {
        if (_editor is null || _surface is null || _mask is null || _committing) return;
        var span = FindCurrentTableSpan(_editor);
        if (span is null)
        {
            if (_surface.IsKeyboardFocusWithin) return;
            HideSurface();
            return;
        }

        if (_span is not null && _edit is not null &&
            _span.Offset == span.Offset && string.Equals(_span.Text, span.Text, StringComparison.Ordinal))
        {
            _surface.IsVisible = true;
            _mask.SetRange(span.Offset, span.Length);
            _editor.TextArea.TextView.Redraw();
            return;
        }

        var table = _parser.Parse(span.Text).Blocks.OfType<TableBlock>().FirstOrDefault();
        if (table is null)
        {
            HideSurface();
            return;
        }

        _span = span;
        _edit = TableEditCodec.FromTable(table);
        var coordinate = GetTableCoordinate(_editor, span, _edit);
        _activeRow = coordinate.Row;
        _activeColumn = coordinate.Column;
        RebuildSurface();
        _surface.IsVisible = true;
        _mask.SetRange(span.Offset, span.Length);
        _editor.TextArea.TextView.Redraw();
    }

    private void RebuildSurface()
    {
        if (_surface is null || _edit is null) return;
        var cells = NormalizeCells(_edit.Cells);
        var merges = TableEditCodec.NormalizeMerges(_edit.Merges ?? [], cells.Count, cells[0].Count);
        _edit = _edit with
        {
            Cells = cells.Select(static row => (IReadOnlyList<string>)row.ToArray()).ToArray(),
            Merges = merges
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var title = new TextBlock
        {
            Text = "TABLE",
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center
        };
        var mergeRight = SmallButton("Merge →", "Merge the active cell with the adjacent cell on the right", () => MergeActive(1, 2));
        var mergeDown = SmallButton("Merge ↓", "Merge the active cell with the cell below", () => MergeActive(2, 1));
        var unmerge = SmallButton("Unmerge", "Split the active merged cell back into grid cells", UnmergeActive);
        var openDialog = SmallButton("More…", "Open the full table editor", OpenFullEditor);
        var status = new TextBlock
        {
            Text = "Direct cell editing · canonical Markdown is preserved",
            FontSize = 10,
            Opacity = 0.58,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0)
        };
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(title);
        bar.Children.Add(status);
        bar.Children.Add(mergeRight);
        bar.Children.Add(mergeDown);
        bar.Children.Add(unmerge);
        bar.Children.Add(openDialog);
        root.Children.Add(bar);

        var grid = BuildInteractiveGrid(cells, merges);
        var scroll = new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 350,
            Margin = new Thickness(0, 7, 0, 0)
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        _surface.Child = root;
    }

    private Grid BuildInteractiveGrid(List<List<string>> cells, IReadOnlyList<TableMergeSpan> merges)
    {
        var grid = new Grid { ColumnSpacing = 2, RowSpacing = 2 };
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(40)));
        for (var column = 0; column < cells[0].Count; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)) { MinWidth = 110 });
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var row = 0; row < cells.Count; row++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (var column = 0; column < cells[0].Count; column++)
        {
            var captured = column;
            var affordance = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = 0.28
            };
            affordance.PointerEntered += (_, _) => affordance.Opacity = 1;
            affordance.PointerExited += (_, _) => affordance.Opacity = 0.28;
            affordance.Children.Add(MicroButton("←+", $"Insert column left of {column + 1}", () => InsertColumn(captured)));
            affordance.Children.Add(MicroButton("+→", $"Insert column right of {column + 1}", () => InsertColumn(captured + 1)));
            Grid.SetRow(affordance, 0);
            Grid.SetColumn(affordance, column + 1);
            grid.Children.Add(affordance);
        }

        for (var row = 0; row < cells.Count; row++)
        {
            var captured = row;
            var affordance = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.28
            };
            affordance.PointerEntered += (_, _) => affordance.Opacity = 1;
            affordance.PointerExited += (_, _) => affordance.Opacity = 0.28;
            affordance.Children.Add(MicroButton("↑+", $"Insert row above {row + 1}", () => InsertRow(captured)));
            affordance.Children.Add(MicroButton("+↓", $"Insert row below {row + 1}", () => InsertRow(captured + 1)));
            Grid.SetRow(affordance, row + 1);
            Grid.SetColumn(affordance, 0);
            grid.Children.Add(affordance);
        }

        for (var row = 0; row < cells.Count; row++)
        {
            for (var column = 0; column < cells[row].Count; column++)
            {
                if (TableEditCodec.IsContinuation(merges, row, column)) continue;
                var region = TableEditCodec.CoveringSpan(merges, row, column);
                var editor = new TextBox
                {
                    Text = cells[row][column],
                    MinHeight = region is { RowSpan: > 1 } ? 58 : 31,
                    Padding = new Thickness(6, 3),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    FontWeight = row == 0 && _edit?.HeaderRow == true ? FontWeight.SemiBold : FontWeight.Normal,
                    Watermark = region is not null ? "Merged cell" : "Cell"
                };
                var capturedRow = row;
                var capturedColumn = column;
                editor.GotFocus += (_, _) => { _activeRow = capturedRow; _activeColumn = capturedColumn; };
                editor.TextChanged += (_, _) => UpdateCellState(capturedRow, capturedColumn, editor.Text ?? string.Empty);
                editor.LostFocus += (_, _) => CommitCurrentState();
                Grid.SetRow(editor, row + 1);
                Grid.SetColumn(editor, column + 1);
                if (region is not null)
                {
                    Grid.SetRowSpan(editor, region.RowSpan);
                    Grid.SetColumnSpan(editor, region.ColumnSpan);
                }
                grid.Children.Add(editor);
            }
        }
        return grid;
    }

    private void UpdateCellState(int row, int column, string value)
    {
        if (_edit is null) return;
        var cells = NormalizeCells(_edit.Cells);
        if (row < 0 || row >= cells.Count || column < 0 || column >= cells[row].Count) return;
        cells[row][column] = value;
        _edit = _edit with { Cells = cells.Select(static item => (IReadOnlyList<string>)item.ToArray()).ToArray() };
    }

    private void InsertRow(int index)
    {
        if (_edit is null) return;
        var cells = NormalizeCells(_edit.Cells);
        index = Math.Clamp(index, 0, cells.Count);
        cells.Insert(index, Enumerable.Repeat(string.Empty, cells[0].Count).ToList());
        var merges = (_edit.Merges ?? []).Select(span =>
        {
            if (span.Row >= index) return span with { Row = span.Row + 1 };
            if (span.Row < index && span.Row + span.RowSpan > index) return span with { RowSpan = span.RowSpan + 1 };
            return span;
        }).ToArray();
        _edit = _edit with
        {
            Cells = cells.Select(static row => (IReadOnlyList<string>)row.ToArray()).ToArray(),
            Merges = TableEditCodec.NormalizeMerges(merges, cells.Count, cells[0].Count)
        };
        if (_activeRow >= index) _activeRow++;
        CommitCurrentState(rebuild: true);
    }

    private void InsertColumn(int index)
    {
        if (_edit is null) return;
        var cells = NormalizeCells(_edit.Cells);
        index = Math.Clamp(index, 0, cells[0].Count);
        foreach (var row in cells) row.Insert(index, string.Empty);
        var merges = (_edit.Merges ?? []).Select(span =>
        {
            if (span.Column >= index) return span with { Column = span.Column + 1 };
            if (span.Column < index && span.Column + span.ColumnSpan > index) return span with { ColumnSpan = span.ColumnSpan + 1 };
            return span;
        }).ToArray();
        var alignments = _edit.Alignments.ToList();
        alignments.Insert(Math.Min(index, alignments.Count), TableAlignment.Default);
        _edit = _edit with
        {
            Cells = cells.Select(static row => (IReadOnlyList<string>)row.ToArray()).ToArray(),
            Alignments = alignments,
            Merges = TableEditCodec.NormalizeMerges(merges, cells.Count, cells[0].Count)
        };
        if (_activeColumn >= index) _activeColumn++;
        CommitCurrentState(rebuild: true);
    }

    private void MergeActive(int rowSpan, int columnSpan)
    {
        if (_edit is null) return;
        var cells = NormalizeCells(_edit.Cells);
        var merges = (_edit.Merges ?? []).ToList();
        if (_activeRow < 0 || _activeColumn < 0 ||
            _activeRow + rowSpan > cells.Count || _activeColumn + columnSpan > cells[0].Count)
            return;
        if (TableEditCodec.CoveringSpan(merges, _activeRow, _activeColumn) is not null) return;
        for (var row = _activeRow; row < _activeRow + rowSpan; row++)
            for (var column = _activeColumn; column < _activeColumn + columnSpan; column++)
                if (TableEditCodec.CoveringSpan(merges, row, column) is not null) return;
        merges.Add(new TableMergeSpan(_activeRow, _activeColumn, rowSpan, columnSpan));
        _edit = _edit with { Merges = TableEditCodec.NormalizeMerges(merges, cells.Count, cells[0].Count) };
        CommitCurrentState(rebuild: true);
    }

    private void UnmergeActive()
    {
        if (_edit is null) return;
        var merges = (_edit.Merges ?? []).ToList();
        var covering = TableEditCodec.CoveringSpan(merges, _activeRow, _activeColumn);
        if (covering is null) return;
        merges.Remove(covering);
        _activeRow = covering.Row;
        _activeColumn = covering.Column;
        _edit = _edit with { Merges = merges };
        CommitCurrentState(rebuild: true);
    }

    private async void OpenFullEditor()
    {
        if (_edit is null || _span is null) return;
        var result = await TableEditorWindow.ShowAsync(_window, _edit);
        if (result is null) return;
        _edit = result;
        CommitCurrentState(rebuild: true);
    }

    private void CommitCurrentState(bool rebuild = false)
    {
        if (_editor is null || _span is null || _edit is null || _committing) return;
        var markup = TableEditCodec.Serialize(_edit);
        if (!string.Equals(markup, _span.Text, StringComparison.Ordinal))
        {
            _committing = true;
            try
            {
                _editor.Document.Replace(_span.Offset, _span.Length, markup);
                _span = _span with { Length = markup.Length, Text = markup };
                _mask?.SetRange(_span.Offset, _span.Length);
            }
            finally
            {
                _committing = false;
            }
        }
        if (rebuild) RebuildSurface();
        _editor.TextArea.TextView.Redraw();
    }

    private void HideSurface()
    {
        if (_surface is not null) _surface.IsVisible = false;
        _span = null;
        _edit = null;
        _mask?.Clear();
        _editor?.TextArea.TextView.Redraw();
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
            if (current.StartsWith(TableMarkupCodec.MetadataPrefix, StringComparison.Ordinal) && TableLine(lineNumber + 1)) lineNumber++;
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
            if (metadataText.StartsWith(TableMarkupCodec.MetadataPrefix, StringComparison.Ordinal)) start--;
        }
        var first = editor.Document.GetLineByNumber(start);
        var last = editor.Document.GetLineByNumber(end);
        var length = last.EndOffset - first.Offset;
        var text = editor.Document.GetText(first.Offset, length);
        return _parser.Parse(text).Blocks.OfType<TableBlock>().Any()
            ? new TableSpan(first.Offset, length, text, firstTableLine)
            : null;
    }

    private static TableCoordinate GetTableCoordinate(ManuscriptEditor editor, TableSpan span, TableEditResult edit)
    {
        var cells = NormalizeCells(edit.Cells);
        var caret = Math.Clamp(editor.CaretIndex, 0, editor.Document.TextLength);
        if (caret == editor.Document.TextLength && caret > 0) caret--;
        var line = editor.Document.GetLineByOffset(caret);
        var separatorLine = span.FirstTableLine + 1;
        var row = line.LineNumber <= separatorLine ? 0 : line.LineNumber - separatorLine;
        row = Math.Clamp(row, 0, cells.Count - 1);
        var lineText = editor.Document.GetText(line.Offset, line.Length);
        var relativeCaret = Math.Clamp(editor.CaretIndex - line.Offset, 0, lineText.Length);
        var pipes = 0;
        for (var index = 0; index < relativeCaret; index++)
            if (lineText[index] == '|' && (index == 0 || lineText[index - 1] != '\\')) pipes++;
        var column = Math.Clamp(Math.Max(0, pipes - 1), 0, cells[0].Count - 1);
        var covering = TableEditCodec.CoveringSpan(edit.Merges ?? [], row, column);
        return covering is null ? new TableCoordinate(row, column) : new TableCoordinate(covering.Row, covering.Column);
    }

    private static List<List<string>> NormalizeCells(IReadOnlyList<IReadOnlyList<string>> source)
    {
        var rows = source.Select(static row => row.ToList()).ToList();
        if (rows.Count == 0) rows.Add([string.Empty]);
        var columns = Math.Max(1, rows.Max(static row => row.Count));
        foreach (var row in rows) while (row.Count < columns) row.Add(string.Empty);
        return rows;
    }

    private static Button SmallButton(string text, string tip, Action action)
    {
        var button = new Button { Content = text, MinHeight = 24, Padding = new Thickness(7, 1), Margin = new Thickness(2, 0) };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    private static Button MicroButton(string text, string tip, Action action)
    {
        var button = new Button { Content = text, MinWidth = 28, Height = 21, MinHeight = 21, FontSize = 9, Padding = new Thickness(2, 0), Margin = new Thickness(1) };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _refreshTimer.Stop();
        _window.Opened -= WindowOpened;
        _window.LayoutUpdated -= WindowLayoutUpdated;
        _window.Closed -= WindowClosed;
        if (_editor is not null)
        {
            _editor.TextArea.Caret.PositionChanged -= CaretPositionChanged;
            _editor.TextChanged -= EditorTextChanged;
            if (_mask is not null) _editor.TextArea.TextView.LineTransformers.Remove(_mask);
        }
    }

    private sealed record TableCoordinate(int Row, int Column);
    private sealed record TableSpan(int Offset, int Length, string Text, int FirstTableLine);

    private sealed class InlineTableMaskTransformer : DocumentColorizingTransformer
    {
        private int _start = -1;
        private int _end = -1;

        public void SetRange(int offset, int length)
        {
            _start = offset;
            _end = offset + Math.Max(0, length);
        }

        public void Clear() => _start = _end = -1;

        protected override void ColorizeLine(DocumentLine line)
        {
            if (_start < 0 || _end <= _start || line.EndOffset <= _start || line.Offset >= _end) return;
            var start = Math.Max(line.Offset, _start);
            var end = Math.Min(line.EndOffset, _end);
            if (end <= start) return;
            ChangeLinePart(start, end, static element =>
            {
                element.TextRunProperties.SetForegroundBrush(Brushes.Transparent);
            });
        }
    }
}
