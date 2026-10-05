using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
/// Word-like table surface for reading and editing the active Markdown table. Canonical Markdown
/// remains in the manuscript, but the source rows are visually masked while the table is active.
/// Structural commands all route through TableEditingEngine.
/// </summary>
internal sealed class InlineTableEditingV2Feature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly IDocumentParser _parser = new EmojiDocumentParser(new AdvancedDocumentParser());
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<(int Row, int Column), TextBox> _cellEditors = [];

    private ManuscriptEditor? _editor;
    private Grid? _host;
    private Border? _surface;
    private InlineTableMaskTransformer? _mask;
    private Popup? _toolbarPopup;
    private TextBlock? _positionLabel;
    private TextBlock? _alignmentLabel;
    private TableEditingContext? _context;
    private TableEditResult? _edit;
    private int _activeRow;
    private int _activeColumn;
    private bool _installed;
    private bool _committing;
    private bool _disposed;

    private InlineTableEditingV2Feature(StudioWorkspaceWindow window)
    {
        _window = window;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RefreshFromCaret();
        };
    }

    public static void Apply(StudioWorkspaceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var feature = new InlineTableEditingV2Feature(window);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
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
            Margin = new Thickness(10, 7),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxHeight = 540
        };
        _surface.Classes.Add("inline-table-editor");
        Grid.SetRow(_surface, 2);
        host.Children.Add(_surface);

        BuildToolbarPopup();
        _mask = new InlineTableMaskTransformer();
        editor.TextArea.TextView.LineTransformers.Add(_mask);
        editor.TextArea.Caret.PositionChanged += CaretPositionChanged;
        editor.TextChanged += EditorTextChanged;
        _installed = true;
        QueueRefresh();
    }

    private void BuildToolbarPopup()
    {
        if (_host is null) return;
        _positionLabel = new TextBlock
        {
            Text = "A1",
            MinWidth = 34,
            FontSize = 10.5,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        _alignmentLabel = new TextBlock
        {
            Text = "Default",
            MinWidth = 44,
            FontSize = 10,
            Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center
        };

        var row = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_positionLabel);
        row.Children.Add(CommandButton("Row ↑", "Insert row above", () => InsertRow(_activeRow)));
        row.Children.Add(CommandButton("Row ↓", "Insert row below", () => InsertRow(_activeRow + 1)));
        row.Children.Add(CommandButton("Col ←", "Insert column left", () => InsertColumn(_activeColumn)));
        row.Children.Add(CommandButton("Col →", "Insert column right", () => InsertColumn(_activeColumn + 1)));
        row.Children.Add(CommandButton("− Row", "Delete active row", DeleteRow));
        row.Children.Add(CommandButton("− Col", "Delete active column", DeleteColumn));
        row.Children.Add(CommandButton("Merge →", "Merge with the cell on the right", () => Merge(1, 2)));
        row.Children.Add(CommandButton("Merge ↓", "Merge with the cell below", () => Merge(2, 1)));
        row.Children.Add(CommandButton("Unmerge", "Split the active merged region", Unmerge));
        row.Children.Add(CommandButton("L", "Align column left", () => SetAlignment(TableAlignment.Left)));
        row.Children.Add(CommandButton("C", "Align column center", () => SetAlignment(TableAlignment.Center)));
        row.Children.Add(CommandButton("R", "Align column right", () => SetAlignment(TableAlignment.Right)));
        row.Children.Add(_alignmentLabel);
        row.Children.Add(CommandButton("Options…", "Caption, identifier and full table editor", OpenFullEditor));

        var chrome = new Border
        {
            Child = row,
            Padding = new Thickness(6, 4),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1)
        };
        chrome.Classes.Add("table-cell-toolbar");
        _toolbarPopup = new Popup
        {
            Child = chrome,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 5,
            IsLightDismissEnabled = false,
            IsOpen = false
        };
        _host.Children.Add(_toolbarPopup);
    }

    private static Button CommandButton(string text, string tip, Action action)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 25,
            Padding = new Thickness(6, 1),
            Margin = new Thickness(1, 0)
        };
        button.Classes.Add("table-toolbar-button");
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        return button;
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
        var context = TableEditingEngine.FindCurrent(_editor, _parser);
        if (context is null)
        {
            if (_surface.IsKeyboardFocusWithin || _toolbarPopup?.IsOpen == true) return;
            HideSurface();
            return;
        }

        if (_context is not null && _edit is not null &&
            _context.Offset == context.Offset && string.Equals(_context.Text, context.Text, StringComparison.Ordinal))
        {
            _surface.IsVisible = true;
            _mask.SetRange(context.Offset, context.Length);
            _editor.TextArea.TextView.Redraw();
            return;
        }

        _context = context;
        _edit = context.Edit;
        _activeRow = context.Row;
        _activeColumn = context.Column;
        RebuildSurface();
        _surface.IsVisible = true;
        _mask.SetRange(context.Offset, context.Length);
        _editor.TextArea.TextView.Redraw();
    }

    private void RebuildSurface()
    {
        if (_surface is null || _edit is null) return;
        _edit = TableEditingEngine.Normalize(_edit);
        var cells = TableEditingEngine.NormalizeCells(_edit.Cells);
        var merges = _edit.Merges ?? [];
        _activeRow = Math.Clamp(_activeRow, 0, cells.Count - 1);
        _activeColumn = Math.Clamp(_activeColumn, 0, cells[0].Count - 1);
        _cellEditors.Clear();

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var title = new TextBlock
        {
            Text = "TABLE",
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.58,
            VerticalAlignment = VerticalAlignment.Center
        };
        var dimensions = new TextBlock
        {
            Text = $"{cells.Count} rows × {cells[0].Count} columns",
            FontSize = 10,
            Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0)
        };
        var hint = new TextBlock
        {
            Text = "Edit directly · Tab next cell · Enter next row",
            FontSize = 10,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0)
        };
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        bar.Classes.Add("table-top-bar");
        bar.Children.Add(title);
        bar.Children.Add(dimensions);
        bar.Children.Add(hint);
        bar.Children.Add(CommandButton("+ Row", "Add row below active cell", () => InsertRow(_activeRow + 1)));
        bar.Children.Add(CommandButton("+ Column", "Add column right of active cell", () => InsertColumn(_activeColumn + 1)));
        bar.Children.Add(CommandButton("Options…", "Open full table options", OpenFullEditor));
        root.Children.Add(bar);

        var grid = BuildGrid(cells, merges);
        var scroll = new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 450,
            Margin = new Thickness(0, 8, 0, 0)
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        _surface.Child = root;
        RefreshCellChrome();
    }

    private Grid BuildGrid(List<List<string>> cells, IReadOnlyList<TableMergeSpan> merges)
    {
        var grid = new Grid { ColumnSpacing = 1, RowSpacing = 1 };
        grid.Classes.Add("word-table-grid");
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(36)));
        for (var column = 0; column < cells[0].Count; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)) { MinWidth = 120 });
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var row = 0; row < cells.Count; row++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var corner = new Border { MinHeight = 27 };
        corner.Classes.Add("table-coordinate-header");
        grid.Children.Add(corner);

        for (var column = 0; column < cells[0].Count; column++)
        {
            var header = new Border { MinHeight = 27 };
            header.Classes.Add("table-coordinate-header");
            header.Child = new TextBlock
            {
                Text = ColumnName(column),
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Opacity = 0.68,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(header, 0);
            Grid.SetColumn(header, column + 1);
            grid.Children.Add(header);
        }

        for (var row = 0; row < cells.Count; row++)
        {
            var header = new Border { MinHeight = 34 };
            header.Classes.Add("table-coordinate-header");
            header.Child = new TextBlock
            {
                Text = (row + 1).ToString(),
                FontSize = 10,
                Opacity = 0.65,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(header, row + 1);
            Grid.SetColumn(header, 0);
            grid.Children.Add(header);
        }

        for (var row = 0; row < cells.Count; row++)
        {
            for (var column = 0; column < cells[row].Count; column++)
            {
                if (TableEditCodec.IsContinuation(merges, row, column)) continue;
                var region = TableEditCodec.CoveringSpan(merges, row, column);
                var alignment = column < _edit!.Alignments.Count ? _edit.Alignments[column] : TableAlignment.Default;
                var cell = new TextBox
                {
                    Text = cells[row][column],
                    AcceptsReturn = false,
                    AcceptsTab = false,
                    MinHeight = region is { RowSpan: > 1 } ? 68 : 36,
                    Padding = new Thickness(8, 5),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    FontWeight = row == 0 ? FontWeight.SemiBold : FontWeight.Normal,
                    TextAlignment = alignment switch
                    {
                        TableAlignment.Center => TextAlignment.Center,
                        TableAlignment.Right => TextAlignment.Right,
                        _ => TextAlignment.Left
                    }
                };
                cell.Classes.Add("table-cell");
                if (row == 0) cell.Classes.Add("table-header-cell");
                var capturedRow = row;
                var capturedColumn = column;
                cell.GotFocus += (_, _) => ActivateCell(capturedRow, capturedColumn, cell);
                cell.TextChanged += (_, _) => UpdateCellState(capturedRow, capturedColumn, cell.Text ?? string.Empty);
                cell.LostFocus += (_, _) => Commit();
                cell.KeyDown += (_, e) => CellKeyDown(e, capturedRow, capturedColumn);
                _cellEditors[(row, column)] = cell;

                Grid.SetRow(cell, row + 1);
                Grid.SetColumn(cell, column + 1);
                if (region is not null)
                {
                    Grid.SetRowSpan(cell, region.RowSpan);
                    Grid.SetColumnSpan(cell, region.ColumnSpan);
                }
                grid.Children.Add(cell);
            }
        }
        return grid;
    }

    private void ActivateCell(int row, int column, TextBox cell)
    {
        _activeRow = row;
        _activeColumn = column;
        RefreshCellChrome();
        if (_toolbarPopup is null) return;
        _toolbarPopup.PlacementTarget = cell;
        _toolbarPopup.IsOpen = true;
    }

    private void RefreshCellChrome()
    {
        foreach (var pair in _cellEditors)
            SetClass(pair.Value, "active-table-cell", pair.Key.Row == _activeRow && pair.Key.Column == _activeColumn);
        if (_positionLabel is not null) _positionLabel.Text = $"{ColumnName(_activeColumn)}{_activeRow + 1}";
        if (_alignmentLabel is not null && _edit is not null)
        {
            var alignment = _activeColumn < _edit.Alignments.Count ? _edit.Alignments[_activeColumn] : TableAlignment.Default;
            _alignmentLabel.Text = alignment.ToString();
        }
    }

    private static void SetClass(Control control, string className, bool enabled)
    {
        var has = control.Classes.Contains(className);
        if (enabled && !has) control.Classes.Add(className);
        else if (!enabled && has) control.Classes.Remove(className);
    }

    private void CellKeyDown(KeyEventArgs e, int row, int column)
    {
        if (e.Key is not (Key.Tab or Key.Enter)) return;
        e.Handled = true;
        _activeRow = row;
        _activeColumn = column;
        Commit();

        if (e.Key == Key.Tab)
        {
            MoveCellFocus(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            return;
        }

        MoveVertical(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
    }

    private void MoveCellFocus(int delta)
    {
        var keys = _cellEditors.Keys.OrderBy(static key => key.Row).ThenBy(static key => key.Column).ToArray();
        var index = Array.IndexOf(keys, (_activeRow, _activeColumn));
        if (index < 0) return;
        var next = index + delta;
        if (next >= 0 && next < keys.Length)
        {
            FocusCell(keys[next]);
            return;
        }
        if (delta > 0 && _edit is not null)
        {
            var cells = TableEditingEngine.NormalizeCells(_edit.Cells);
            _activeRow = cells.Count;
            _activeColumn = 0;
            InsertRow(cells.Count);
        }
    }

    private void MoveVertical(int delta)
    {
        if (_edit is null) return;
        var cells = TableEditingEngine.NormalizeCells(_edit.Cells);
        var nextRow = _activeRow + delta;
        if (nextRow >= cells.Count)
        {
            _activeRow = cells.Count;
            InsertRow(cells.Count);
            return;
        }
        nextRow = Math.Clamp(nextRow, 0, cells.Count - 1);
        var key = ResolveFocusableCell(nextRow, _activeColumn);
        FocusCell(key);
    }

    private (int Row, int Column) ResolveFocusableCell(int row, int column)
    {
        if (_cellEditors.ContainsKey((row, column))) return (row, column);
        var covering = _edit is null ? null : TableEditCodec.CoveringSpan(_edit.Merges ?? [], row, column);
        return covering is null ? (row, column) : (covering.Row, covering.Column);
    }

    private void FocusCell((int Row, int Column) key)
    {
        _activeRow = key.Row;
        _activeColumn = key.Column;
        if (!_cellEditors.TryGetValue(key, out var cell)) return;
        cell.Focus();
        cell.SelectAll();
    }

    private void UpdateCellState(int row, int column, string value)
    {
        if (_edit is null) return;
        var cells = TableEditingEngine.NormalizeCells(_edit.Cells);
        if (row < 0 || row >= cells.Count || column < 0 || column >= cells[row].Count) return;
        cells[row][column] = value;
        _edit = _edit with { Cells = cells.Select(static item => (IReadOnlyList<string>)item.ToArray()).ToArray() };
    }

    private void InsertRow(int index)
    {
        if (_edit is null) return;
        _edit = TableEditingEngine.InsertRow(_edit, index);
        _activeRow = Math.Clamp(index, 0, _edit.Cells.Count - 1);
        _activeColumn = Math.Clamp(_activeColumn, 0, _edit.Cells[0].Count - 1);
        Commit(rebuild: true, focus: true);
    }

    private void DeleteRow()
    {
        if (_edit is null) return;
        var before = _edit.Cells.Count;
        _edit = TableEditingEngine.DeleteRow(_edit, _activeRow);
        if (_edit.Cells.Count == before && before <= 1) return;
        _activeRow = Math.Clamp(_activeRow, 0, _edit.Cells.Count - 1);
        Commit(rebuild: true, focus: true);
    }

    private void InsertColumn(int index)
    {
        if (_edit is null) return;
        _edit = TableEditingEngine.InsertColumn(_edit, index);
        _activeColumn = Math.Clamp(index, 0, _edit.Cells[0].Count - 1);
        _activeRow = Math.Clamp(_activeRow, 0, _edit.Cells.Count - 1);
        Commit(rebuild: true, focus: true);
    }

    private void DeleteColumn()
    {
        if (_edit is null) return;
        var before = _edit.Cells[0].Count;
        _edit = TableEditingEngine.DeleteColumn(_edit, _activeColumn);
        if (_edit.Cells[0].Count == before && before <= 1) return;
        _activeColumn = Math.Clamp(_activeColumn, 0, _edit.Cells[0].Count - 1);
        Commit(rebuild: true, focus: true);
    }

    private void Merge(int rowSpan, int columnSpan)
    {
        if (_edit is null) return;
        _edit = TableEditingEngine.Merge(_edit, _activeRow, _activeColumn, rowSpan, columnSpan);
        Commit(rebuild: true, focus: true);
    }

    private void Unmerge()
    {
        if (_edit is null) return;
        var covering = TableEditCodec.CoveringSpan(_edit.Merges ?? [], _activeRow, _activeColumn);
        if (covering is not null)
        {
            _activeRow = covering.Row;
            _activeColumn = covering.Column;
        }
        _edit = TableEditingEngine.Unmerge(_edit, _activeRow, _activeColumn);
        Commit(rebuild: true, focus: true);
    }

    private void SetAlignment(TableAlignment alignment)
    {
        if (_edit is null) return;
        _edit = TableEditingEngine.SetAlignment(_edit, _activeColumn, alignment);
        Commit(rebuild: true, focus: true);
    }

    private async void OpenFullEditor()
    {
        if (_edit is null) return;
        Commit();
        var result = await TableEditorWindow.ShowAsync(_window, TableEditingEngine.Normalize(_edit));
        if (result is null) return;
        _edit = TableEditingEngine.Normalize(result);
        _activeRow = Math.Clamp(_activeRow, 0, _edit.Cells.Count - 1);
        _activeColumn = Math.Clamp(_activeColumn, 0, _edit.Cells[0].Count - 1);
        Commit(rebuild: true, focus: true);
    }

    private void Commit(bool rebuild = false, bool focus = false)
    {
        if (_editor is null || _context is null || _edit is null || _committing) return;
        var markup = TableEditingEngine.Serialize(_edit);
        if (!string.Equals(markup, _context.Text, StringComparison.Ordinal))
        {
            _committing = true;
            try
            {
                _editor.Document.Replace(_context.Offset, _context.Length, markup);
                _context = _context with { Length = markup.Length, Text = markup, Edit = _edit };
                _mask?.SetRange(_context.Offset, _context.Length);
            }
            finally { _committing = false; }
        }
        if (rebuild) RebuildSurface();
        _editor.TextArea.TextView.Redraw();
        if (focus) QueueFocusActiveCell();
    }

    private void QueueFocusActiveCell()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var key = ResolveFocusableCell(_activeRow, _activeColumn);
            if (_cellEditors.TryGetValue(key, out var cell))
            {
                _activeRow = key.Row;
                _activeColumn = key.Column;
                cell.Focus();
                if (_toolbarPopup is not null)
                {
                    _toolbarPopup.PlacementTarget = cell;
                    _toolbarPopup.IsOpen = true;
                }
            }
            RefreshCellChrome();
        }, DispatcherPriority.Background);
    }

    private void HideSurface()
    {
        if (_surface is not null) _surface.IsVisible = false;
        if (_toolbarPopup is not null) _toolbarPopup.IsOpen = false;
        _cellEditors.Clear();
        _context = null;
        _edit = null;
        _mask?.Clear();
        _editor?.TextArea.TextView.Redraw();
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

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _refreshTimer.Stop();
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        if (_editor is not null)
        {
            _editor.TextArea.Caret.PositionChanged -= CaretPositionChanged;
            _editor.TextChanged -= EditorTextChanged;
            if (_mask is not null) _editor.TextArea.TextView.LineTransformers.Remove(_mask);
        }
        if (_toolbarPopup is not null) _toolbarPopup.IsOpen = false;
    }

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
            ChangeLinePart(start, end, static element => element.TextRunProperties.SetForegroundBrush(Brushes.Transparent));
        }
    }
}