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
/// Replaces Markdown pipe-table source with a real, readable table control at the same document
/// position. Markdown remains the canonical stored source. Clicking the rendered table moves the
/// caret into its source so InlineTableEditingV2Feature can provide the editable grid and commands.
/// </summary>
internal sealed class InlineMarkdownTableRenderFeature
{
    private readonly StudioWorkspaceWindow _window;
    private readonly IDocumentParser _parser = new EmojiDocumentParser(new AdvancedDocumentParser());
    private readonly DispatcherTimer _refreshTimer;
    private readonly List<CollapsedLineSection> _collapsedTableSections = [];

    private ManuscriptEditor? _editor;
    private MarkdownTableElementGenerator? _generator;
    private MarkdownTableSourceTransformer? _sourceTransformer;
    private bool _installed;
    private bool _refreshQueued;
    private bool _disposed;

    private InlineMarkdownTableRenderFeature(StudioWorkspaceWindow window)
    {
        _window = window;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            _refreshQueued = false;
            RefreshTables();
        };
    }

    public static void Apply(StudioWorkspaceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var feature = new InlineMarkdownTableRenderFeature(window);
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
        var host = _window.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(static grid => grid.Classes.Contains("long-form-editor-host"));
        if (host is null) return;

        var editor = host.Children.OfType<ManuscriptEditor>().FirstOrDefault();
        if (editor is null) return;

        _editor = editor;
        _generator = new MarkdownTableElementGenerator(BuildTableControl);
        _sourceTransformer = new MarkdownTableSourceTransformer();
        editor.TextArea.TextView.ElementGenerators.Add(_generator);
        editor.TextArea.TextView.LineTransformers.Add(_sourceTransformer);
        editor.TextChanged += EditorTextChanged;
        editor.SizeChanged += EditorSizeChanged;

        _installed = true;
        _window.LayoutUpdated -= OnLayoutUpdated;
        QueueRefresh();
    }

    private void EditorTextChanged(object? sender, EventArgs e) => QueueRefresh();

    private void EditorSizeChanged(object? sender, SizeChangedEventArgs e)
        => _editor?.TextArea.TextView.Redraw();

    private void QueueRefresh()
    {
        if (_disposed || !_installed || _refreshQueued) return;
        _refreshQueued = true;
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void RefreshTables()
    {
        if (_editor is null || _generator is null || _sourceTransformer is null || _disposed) return;

        ClearCollapsedTableSections();

        var tables = TableEditingEngine.FindAll(_editor, _parser);
        _generator.SetTables(tables);
        _sourceTransformer.SetTables(tables);
        CollapseTableSourceLines(tables);
        _editor.TextArea.TextView.Redraw();
    }

    private void CollapseTableSourceLines(IReadOnlyList<TableEditingContext> tables)
    {
        if (_editor is null || _editor.Document.TextLength == 0) return;

        var document = _editor.Document;
        var textView = _editor.TextArea.TextView;
        foreach (var table in tables)
        {
            if (table.Length <= 0 || table.Offset < 0 || table.Offset >= document.TextLength) continue;

            var anchorOffset = Math.Clamp(table.Offset, 0, document.TextLength - 1);
            var endOffset = Math.Clamp(table.Offset + table.Length - 1, anchorOffset, document.TextLength - 1);
            var anchorLine = document.GetLineByOffset(anchorOffset);
            var endLine = document.GetLineByOffset(endOffset);
            var firstHiddenLine = anchorLine.NextLine;
            if (firstHiddenLine is null || firstHiddenLine.LineNumber > endLine.LineNumber) continue;

            _collapsedTableSections.Add(textView.CollapseLines(firstHiddenLine, endLine));
        }
    }

    private void ClearCollapsedTableSections()
    {
        foreach (var section in _collapsedTableSections)
            section.Uncollapse();
        _collapsedTableSections.Clear();
    }

    private Control BuildTableControl(TableEditingContext context, double availableWidth)
    {
        var edit = TableEditingEngine.Normalize(context.Edit);
        var cells = TableEditingEngine.NormalizeCells(edit.Cells);
        var merges = edit.Merges ?? [];
        var table = new Grid { ColumnSpacing = 0, RowSpacing = 0 };

        for (var column = 0; column < cells[0].Count; column++)
            table.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)) { MinWidth = 84 });
        for (var row = 0; row < cells.Count; row++)
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (var row = 0; row < cells.Count; row++)
        {
            for (var column = 0; column < cells[row].Count; column++)
            {
                if (TableEditCodec.IsContinuation(merges, row, column)) continue;
                var region = TableEditCodec.CoveringSpan(merges, row, column);
                var alignment = column < edit.Alignments.Count ? edit.Alignments[column] : TableAlignment.Default;
                var text = new TextBlock
                {
                    Text = cells[row][column],
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = row == 0 ? FontWeight.SemiBold : FontWeight.Normal,
                    TextAlignment = alignment switch
                    {
                        TableAlignment.Center => TextAlignment.Center,
                        TableAlignment.Right => TextAlignment.Right,
                        _ => TextAlignment.Left
                    }
                };

                var cell = new Border
                {
                    Child = text,
                    MinHeight = region is { RowSpan: > 1 } ? 64 : 36,
                    Padding = new Thickness(9, 7),
                    BorderThickness = new Thickness(0.5)
                };
                cell.Classes.Add("inline-markdown-table-cell");
                if (row == 0) cell.Classes.Add("inline-markdown-table-header");

                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                if (region is not null)
                {
                    Grid.SetRowSpan(cell, region.RowSpan);
                    Grid.SetColumnSpan(cell, region.ColumnSpan);
                }
                table.Children.Add(cell);
            }
        }

        var content = new Grid();
        if (!string.IsNullOrWhiteSpace(edit.Caption))
        {
            content.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            content.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var caption = new TextBlock
            {
                Text = edit.Caption.Trim(),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(1, 0, 1, 7),
                Opacity = 0.72
            };
            caption.Classes.Add("inline-markdown-table-caption");
            content.Children.Add(caption);
            Grid.SetRow(table, 1);
        }
        else
        {
            content.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }
        content.Children.Add(table);

        var width = double.IsFinite(availableWidth) && availableWidth > 160
            ? Math.Max(260, availableWidth - 28)
            : 680;
        var surface = new Border
        {
            Child = content,
            Width = width,
            MaxWidth = 1100,
            MinWidth = Math.Min(260, width),
            Margin = new Thickness(0, 7, 0, 7),
            Padding = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            Cursor = new Avalonia.Input.Cursor(StandardCursorType.Hand)
        };
        surface.Classes.Add("inline-markdown-table");
        ToolTip.SetTip(surface, "Click to edit table");
        surface.PointerPressed += (_, e) =>
        {
            ActivateTable(context);
            e.Handled = true;
        };
        return surface;
    }

    private void ActivateTable(TableEditingContext context)
    {
        if (_editor is null || _editor.Document.TextLength == 0) return;
        var offset = Math.Clamp(context.Offset, 0, Math.Max(0, _editor.Document.TextLength - 1));
        _editor.CaretIndex = offset;
        _editor.Select(offset, 0);
        _editor.Focus();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _refreshTimer.Stop();
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        ClearCollapsedTableSections();
        if (_editor is not null)
        {
            _editor.TextChanged -= EditorTextChanged;
            _editor.SizeChanged -= EditorSizeChanged;
            if (_generator is not null) _editor.TextArea.TextView.ElementGenerators.Remove(_generator);
            if (_sourceTransformer is not null) _editor.TextArea.TextView.LineTransformers.Remove(_sourceTransformer);
        }
    }

    private sealed class MarkdownTableElementGenerator(
        Func<TableEditingContext, double, Control> buildControl) : VisualLineElementGenerator
    {
        private IReadOnlyList<TableEditingContext> _tables = [];

        public void SetTables(IReadOnlyList<TableEditingContext> tables)
            => _tables = tables.OrderBy(static table => table.Offset).ToArray();

        public override int GetFirstInterestedOffset(int startOffset)
        {
            foreach (var table in _tables)
                if (table.Offset >= startOffset) return table.Offset;
            return -1;
        }

        public override VisualLineElement ConstructElement(int offset)
        {
            var table = _tables.FirstOrDefault(candidate => candidate.Offset == offset);
            if (table is null) return null!;

            var line = CurrentContext.Document.GetLineByOffset(offset);
            if (line.Length <= 0 || table.Length <= 0) return null!;
            var width = CurrentContext.TextView.Bounds.Width;
            var control = buildControl(table, width);

            // AvaloniaEdit visual elements may not consume uncollapsed document lines. Keep the
            // inline object on the anchor line and collapse the remaining table source lines via
            // TextView.CollapseLines in the owning feature.
            return new InlineObjectElement(line.Length, control);
        }
    }

    private sealed class MarkdownTableSourceTransformer : DocumentColorizingTransformer
    {
        private IReadOnlyList<TableEditingContext> _tables = [];

        public void SetTables(IReadOnlyList<TableEditingContext> tables)
            => _tables = tables.OrderBy(static table => table.Offset).ToArray();

        protected override void ColorizeLine(DocumentLine line)
        {
            foreach (var table in _tables)
            {
                var tableEnd = table.Offset + table.Length;
                if (line.EndOffset <= table.Offset) continue;
                if (line.Offset >= tableEnd) continue;

                var start = Math.Max(line.Offset, table.Offset);
                var end = Math.Min(line.EndOffset, tableEnd);
                if (end <= start) return;

                ChangeLinePart(start, end, static element =>
                    element.TextRunProperties.SetForegroundBrush(Brushes.Transparent));
                return;
            }
        }
    }
}
