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
/// Renders canonical Markdown pipe tables as editable inline objects inside AvaloniaEdit.
/// Unlike the legacy table surface, this feature does not mask source lines and does not place
/// a second editor above/below the manuscript. Each Markdown table line is replaced by an
/// <see cref="InlineObjectElement"/> that occupies the same position in the text flow.
///
/// The Markdown text remains the source of truth. Cell edits and structural commands are routed
/// through <see cref="TableEditingEngine"/>, so captions, identifiers, alignments and merge
/// metadata continue to round-trip through the existing Markdown table codec.
/// </summary>
internal sealed class InlineMarkdownTableEditorFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly IDocumentParser _parser = new EmojiDocumentParser(new AdvancedDocumentParser());
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<int, (int Row, int Column)> _activeCells = [];

    private ManuscriptEditor? _editor;
    private InlineTableElementGenerator? _generator;
    private bool _installed;
    private bool _disposed;
    private bool _committing;

    private InlineMarkdownTableEditorFeature(StudioWorkspaceWindow window)
    {
        _window = window;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RefreshIndex();
        };
    }

    public static void Apply(StudioWorkspaceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var feature = new InlineMarkdownTableEditorFeature(window);
        window.Opened += feature.WindowOpened;
        window.LayoutUpdated += feature.WindowLayoutUpdated;
        window.Closed += feature.WindowClosed;
        feature.TryInstall();
    }

    private void WindowOpened(object? sender, EventArgs e) => TryInstall();

    private void WindowLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_installed)
        {
            TryInstall();
            return;
        }

        if (_editor is null || _generator is null) return;
        var width = Math.Max(320, _editor.TextArea.TextView.Bounds.Width - 28);
        if (Math.Abs(width - _generator.ViewportWidth) < 1) return;
        _generator.ViewportWidth = width;
        _editor.TextArea.TextView.Redraw();
    }

    private void TryInstall()
    {
        if (_installed || _disposed) return;
        var host = _window.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(static grid => grid.Classes.Contains("long-form-editor-host"));
        var editor = host?.Children.OfType<ManuscriptEditor>().FirstOrDefault();
        if (editor is null) return;

        _editor = editor;
        _generator = new InlineTableElementGenerator(this)
        {
            ViewportWidth = Math.Max(320, editor.TextArea.TextView.Bounds.Width - 28)
        };
        editor.TextArea.TextView.ElementGenerators.Add(_generator);
        editor.TextChanged += EditorTextChanged;
        _installed = true;
        RefreshIndex();
    }

    private void EditorTextChanged(object? sender, EventArgs e)
    {
        if (_committing) return;
        _generator?.SetRows([]);
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (_disposed || !_installed) return;
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void RefreshIndex()
    {
        if (_editor is null || _generator is null || _committing) return;
        var rows = BuildRows(TableEditingEngine.FindAll(_editor, _parser));
        _generator.SetRows(rows);
        _editor.TextArea.TextView.Redraw();
    }

    private IReadOnlyList<InlineTableRow> BuildRows(IReadOnlyList<TableEditingContext> tables)
    {
        if (_editor is null) return [];
        var result = new List<InlineTableRow>();
        foreach (var context in tables)
        {
            var edit = TableEditingEngine.Normalize(context.Edit);
            var cells = TableEditingEngine.NormalizeCells(edit.Cells);
            if (cells.Count == 0) continue;

            AddRow(context, edit, context.FirstTableLine, InlineTableRowKind.Header, 0);
            AddRow(context, edit, context.FirstTableLine + 1, InlineTableRowKind.Toolbar, 0);
            for (var row = 1; row < cells.Count; row++)
                AddRow(context, edit, context.FirstTableLine + row + 1, InlineTableRowKind.Body, row);
        }
        result.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        return result;

        void AddRow(TableEditingContext context, TableEditResult edit, int lineNumber, InlineTableRowKind kind, int row)
        {
            if (lineNumber < 1 || lineNumber > _editor.Document.LineCount) return;
            var line = _editor.Document.GetLineByNumber(lineNumber);
            if (line.Length <= 0) return;
            result.Add(new InlineTableRow(line.Offset, line.Length, context.Offset, edit, kind, row));
        }
    }

    private Control BuildControl(InlineTableRow row)
        => row.Kind == InlineTableRowKind.Toolbar
            ? BuildToolbar(row)
            : BuildEditableRow(row);

    private Control BuildEditableRow(InlineTableRow row)
    {
        var edit = TableEditingEngine.Normalize(row.Edit);
        var cells = TableEditingEngine.NormalizeCells(edit.Cells);
        var rowIndex = Math.Clamp(row.Row, 0, cells.Count - 1);
        var columns = cells[rowIndex].Count;
        var grid = new Grid
        {
            Width = Math.Max(320, _generator?.ViewportWidth ?? 720),
            ColumnSpacing = 0,
            RowSpacing = 0,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        for (var column = 0; column < columns; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)) { MinWidth = 72 });

        var merges = edit.Merges ?? [];
        for (var column = 0; column < columns; column++)
        {
            if (TableEditCodec.IsContinuation(merges, rowIndex, column))
            {
                var covering = TableEditCodec.CoveringSpan(merges, rowIndex, column);
                if (covering is { Row: var anchorRow } && anchorRow != rowIndex)
                {
                    // Row-spanning cells cannot physically span separate AvaloniaEdit visual lines.
                    // Keep their continuation positions empty while the anchor remains editable.
                    continue;
                }
                if (covering is not null && covering.Column != column) continue;
            }

            var capturedColumn = column;
            var initial = cells[rowIndex][column];
            var editor = new TextBox
            {
                Text = initial,
                AcceptsReturn = false,
                AcceptsTab = false,
                MinHeight = row.Kind == InlineTableRowKind.Header ? 38 : 36,
                Padding = new Thickness(8, 5),
                TextWrapping = TextWrapping.Wrap,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                FontWeight = row.Kind == InlineTableRowKind.Header ? FontWeight.SemiBold : FontWeight.Normal,
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromArgb(74, 128, 128, 128)),
                BorderThickness = new Thickness(.5),
                TextAlignment = AlignmentFor(edit, column)
            };
            editor.GotFocus += (_, _) =>
            {
                _activeCells[row.TableOffset] = (rowIndex, capturedColumn);
                editor.SelectAll();
            };
            editor.LostFocus += (_, _) =>
            {
                var current = editor.Text ?? string.Empty;
                if (!string.Equals(current, initial, StringComparison.Ordinal))
                    UpdateCell(row.TableOffset, rowIndex, capturedColumn, current);
            };
            editor.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                var current = editor.Text ?? string.Empty;
                if (!string.Equals(current, initial, StringComparison.Ordinal))
                    UpdateCell(row.TableOffset, rowIndex, capturedColumn, current);
                _editor?.Focus();
            };

            Grid.SetColumn(editor, column);
            var span = TableEditCodec.CoveringSpan(merges, rowIndex, column);
            if (span is { Row: var spanAnchorRow, Column: var spanAnchorColumn } &&
                spanAnchorRow == rowIndex && spanAnchorColumn == column && span.ColumnSpan > 1)
            {
                Grid.SetColumnSpan(editor, Math.Min(span.ColumnSpan, columns - column));
            }
            grid.Children.Add(editor);
        }

        var border = new Border
        {
            Child = grid,
            Width = grid.Width,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = row.Kind == InlineTableRowKind.Header
                ? new SolidColorBrush(Color.FromArgb(18, 128, 128, 128))
                : Brushes.Transparent
        };
        return border;
    }

    private Control BuildToolbar(InlineTableRow row)
    {
        var edit = TableEditingEngine.Normalize(row.Edit);
        var cells = TableEditingEngine.NormalizeCells(edit.Cells);
        var active = _activeCells.TryGetValue(row.TableOffset, out var value) ? value : (0, 0);
        var activeRow = Math.Clamp(active.Item1, 0, cells.Count - 1);
        var activeColumn = Math.Clamp(active.Item2, 0, cells[0].Count - 1);

        var panel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(new TextBlock
        {
            Text = $"TABLE  {ColumnName(activeColumn)}{activeRow + 1}",
            Margin = new Thickness(4, 0, 8, 0),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Opacity = .64,
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(Command("+ Row", () => Mutate(row.TableOffset,
            current => TableEditingEngine.InsertRow(current, activeRow + 1))));
        panel.Children.Add(Command("− Row", () => Mutate(row.TableOffset,
            current => TableEditingEngine.DeleteRow(current, activeRow))));
        panel.Children.Add(Command("+ Col", () => Mutate(row.TableOffset,
            current => TableEditingEngine.InsertColumn(current, activeColumn + 1))));
        panel.Children.Add(Command("− Col", () => Mutate(row.TableOffset,
            current => TableEditingEngine.DeleteColumn(current, activeColumn))));
        panel.Children.Add(Command("Merge →", () => Mutate(row.TableOffset,
            current => TableEditingEngine.Merge(current, activeRow, activeColumn, 1, 2))));
        panel.Children.Add(Command("Merge ↓", () => Mutate(row.TableOffset,
            current => TableEditingEngine.Merge(current, activeRow, activeColumn, 2, 1))));
        panel.Children.Add(Command("Unmerge", () => Mutate(row.TableOffset,
            current => TableEditingEngine.Unmerge(current, activeRow, activeColumn))));
        panel.Children.Add(Command("L", () => Mutate(row.TableOffset,
            current => TableEditingEngine.SetAlignment(current, activeColumn, TableAlignment.Left))));
        panel.Children.Add(Command("C", () => Mutate(row.TableOffset,
            current => TableEditingEngine.SetAlignment(current, activeColumn, TableAlignment.Center))));
        panel.Children.Add(Command("R", () => Mutate(row.TableOffset,
            current => TableEditingEngine.SetAlignment(current, activeColumn, TableAlignment.Right))));

        return new Border
        {
            Child = panel,
            Width = Math.Max(320, _generator?.ViewportWidth ?? 720),
            MinHeight = 31,
            Padding = new Thickness(3, 3),
            Margin = new Thickness(0),
            BorderThickness = new Thickness(.5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(62, 128, 128, 128)),
            Background = new SolidColorBrush(Color.FromArgb(12, 128, 128, 128))
        };
    }

    private static Button Command(string label, Action action)
    {
        var button = new Button
        {
            Content = label,
            MinHeight = 24,
            Padding = new Thickness(6, 1),
            Margin = new Thickness(1, 0),
            FontSize = 10.5
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void UpdateCell(int tableOffset, int row, int column, string value)
    {
        Mutate(tableOffset, edit =>
        {
            var cells = TableEditingEngine.NormalizeCells(edit.Cells);
            row = Math.Clamp(row, 0, cells.Count - 1);
            column = Math.Clamp(column, 0, cells[0].Count - 1);
            cells[row][column] = value;
            return edit with
            {
                Cells = cells.Select(static current => (IReadOnlyList<string>)current.ToArray()).ToArray()
            };
        });
    }

    private void Mutate(int tableOffset, Func<TableEditResult, TableEditResult> mutation)
    {
        if (_editor is null || _committing) return;
        var context = TableEditingEngine.FindAll(_editor, _parser)
            .FirstOrDefault(candidate => candidate.Offset == tableOffset);
        if (context is null) return;

        _committing = true;
        try
        {
            var next = mutation(TableEditingEngine.Normalize(context.Edit));
            var replacement = TableEditingEngine.Replace(_editor, context, next);
            _activeCells[replacement.Offset] = _activeCells.GetValueOrDefault(tableOffset, (0, 0));
        }
        finally
        {
            _committing = false;
        }
        QueueRefresh();
    }

    private static TextAlignment AlignmentFor(TableEditResult edit, int column)
    {
        var alignment = column < edit.Alignments.Count ? edit.Alignments[column] : TableAlignment.Default;
        return alignment switch
        {
            TableAlignment.Center => TextAlignment.Center,
            TableAlignment.Right => TextAlignment.Right,
            _ => TextAlignment.Left
        };
    }

    private static string ColumnName(int column)
    {
        column = Math.Max(0, column);
        var name = string.Empty;
        do
        {
            name = (char)('A' + column % 26) + name;
            column = column / 26 - 1;
        } while (column >= 0);
        return name;
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _refreshTimer.Stop();
        if (_editor is not null)
        {
            _editor.TextChanged -= EditorTextChanged;
            if (_generator is not null)
                _editor.TextArea.TextView.ElementGenerators.Remove(_generator);
        }
        _window.Opened -= WindowOpened;
        _window.LayoutUpdated -= WindowLayoutUpdated;
        _window.Closed -= WindowClosed;
    }

    private enum InlineTableRowKind
    {
        Header,
        Toolbar,
        Body
    }

    private sealed record InlineTableRow(
        int Offset,
        int Length,
        int TableOffset,
        TableEditResult Edit,
        InlineTableRowKind Kind,
        int Row);

    private sealed class InlineTableElementGenerator(InlineMarkdownTableEditorFeature owner) : VisualLineElementGenerator
    {
        private IReadOnlyList<InlineTableRow> _rows = [];
        private readonly Dictionary<int, Control> _controls = [];

        public double ViewportWidth { get; set; } = 720;

        public void SetRows(IReadOnlyList<InlineTableRow> rows)
        {
            _rows = rows;
            _controls.Clear();
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            var document = CurrentContext.Document;
            for (var index = 0; index < _rows.Count; index++)
            {
                var row = _rows[index];
                if (row.Offset >= startOffset && IsCurrentLineSpan(document, row.Offset, row.Length))
                    return row.Offset;
            }
            return -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var row = _rows.FirstOrDefault(candidate => candidate.Offset == offset);
            if (row is null || row.Length <= 0) return null;
            var document = CurrentContext.Document;
            var line = document.GetLineByOffset(offset);
            if (line.Offset != offset || offset + row.Length > line.EndOffset) return null;

            if (!_controls.TryGetValue(offset, out var control))
            {
                control = owner.BuildControl(row);
                control.VerticalAlignment = VerticalAlignment.Center;
                _controls[offset] = control;
            }
            return new InlineObjectElement(row.Length, control);
        }

        private static bool IsCurrentLineSpan(TextDocument document, int offset, int length)
        {
            if (length <= 0 || offset < 0 || offset >= document.TextLength) return false;
            var line = document.GetLineByOffset(offset);
            return line.Offset == offset && offset + length <= line.EndOffset;
        }
    }
}
