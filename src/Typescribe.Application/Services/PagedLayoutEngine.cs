using System.Globalization;
using System.Text;
using Typescribe.Domain.Models;

namespace Typescribe.Application.Services;

public enum PageLayoutFragmentKind
{
    Paragraph,
    Heading,
    Quote,
    ListItem,
    Code,
    DisplayMath,
    ThematicBreak,
    TableCaption,
    TableHeader,
    TableRow,
    Figure,
    Footnote,
    TextFrame,
    FloatingObject,
    MarginNote,
    OversetIndicator
}

public enum PageLayoutGuideOrientation
{
    Horizontal,
    Vertical
}

public sealed record PageLayoutRect(double XPoints, double YPoints, double WidthPoints, double HeightPoints)
{
    public double RightPoints => XPoints + WidthPoints;
    public double BottomPoints => YPoints + HeightPoints;
}

public sealed record PageLayoutGuide(PageLayoutGuideOrientation Orientation, double PositionPoints, string Kind);

public sealed record PageLayoutFragment(
    string Id,
    PageLayoutFragmentKind Kind,
    int SourceLine,
    int SourceBlockIndex,
    PageLayoutRect Bounds,
    string Text,
    int FragmentIndex = 0,
    bool IsContinuation = false,
    bool IsRepeatedHeader = false,
    string? FrameId = null,
    string? AnchorId = null,
    TextWrapMode Wrap = TextWrapMode.None,
    FloatPlacementMode Placement = FloatPlacementMode.Inline,
    int? TableRowIndex = null,
    int FrameColumns = 1,
    int SourceTextStart = 0,
    int SourceTextLength = 0);

public sealed record PageLayoutColumn(int Index, PageLayoutRect Bounds, IReadOnlyList<PageLayoutFragment> Fragments);

public sealed record PageLayoutPage(
    int Index,
    int PhysicalNumber,
    int DisplayNumber,
    string DisplayNumberText,
    PageNumberStyle NumberStyle,
    bool IsLeftPage,
    bool IsBlank,
    bool FacingPages,
    double WidthPoints,
    double HeightPoints,
    PageLayoutRect ContentBounds,
    IReadOnlyList<PageLayoutColumn> Columns,
    IReadOnlyList<PageLayoutFragment> FloatingObjects,
    IReadOnlyList<PageLayoutFragment> Footnotes,
    IReadOnlyList<PageLayoutGuide> Guides,
    BaselineGridFormatting? BaselineGrid,
    string? PageStyleId,
    bool CropMarks,
    double BleedTopPoints,
    double BleedBottomPoints,
    double BleedInsidePoints,
    double BleedOutsidePoints,
    double SlugPoints);

public sealed record PageLayoutFrameThread(
    string RootFrameId,
    IReadOnlyList<string> FrameIds,
    IReadOnlyList<int> SourceLines,
    bool Overset);

public sealed record PageLayoutWarning(string Code, string Message, int? SourceLine = null);

public sealed record PagedLayoutResult(
    IReadOnlyList<PageLayoutPage> Pages,
    IReadOnlyList<PageLayoutFrameThread> FrameThreads,
    IReadOnlyList<PageLayoutWarning> Warnings)
{
    public bool HasOverset => Warnings.Any(static warning => warning.Code == "overset");
}

public sealed record PagedLayoutOptions(
    double AverageGlyphWidthFactor = .52,
    int DefaultWidowLines = 2,
    int DefaultOrphanLines = 2,
    double FootnoteGapPoints = 6,
    double MinimumFigureHeightPoints = 72,
    double DefaultFigureAspectRatio = .62,
    double OversetIndicatorHeightPoints = 12,
    double MinimumLineHeightPoints = 8);

/// <summary>
/// Deterministic page-flow projection over the Markdown-first semantic AST. Coordinates are derived
/// from source + styles and are never persisted as a second document representation.
/// </summary>
public sealed class PagedLayoutEngine
{
    private readonly PagedLayoutOptions _options;
    private readonly List<PageBuilder> _pages = [];
    private readonly List<PageLayoutWarning> _warnings = [];
    private readonly Dictionary<string, FootnoteDefinitionBlock> _footnotes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextFrameFormatting> _frames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _frameLines = new(StringComparer.Ordinal);
    private readonly List<PageLayoutFrameThread> _frameThreads = [];
    private readonly List<WrapExclusion> _wrapExclusions = [];

    private DocumentAst _document = new([]);
    private BookStyle _style = BookStyle.Default;
    private SectionFormatting _section = new();
    private int _columnIndex;
    private double _cursorY;
    private int _nextDisplayNumber = 1;
    private int _fragmentSerial;

    public PagedLayoutEngine(PagedLayoutOptions? options = null)
        => _options = options ?? new PagedLayoutOptions();

    public PagedLayoutResult Paginate(DocumentAst document, BookStyle style)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(style);
        Reset(document, style);
        ScanDefinitions();

        for (var index = 0; index < document.Blocks.Count; index++)
        {
            var block = document.Blocks[index];
            if (block is FootnoteDefinitionBlock or BibliographyEntryBlock) continue;

            if (block.Formatting?.Section is { } section)
                ApplySection(section);

            EnsurePage();
            ReserveFootnotes(block);

            if (block.Formatting?.TextFrame is { } frame)
            {
                LayoutText(block, index, PageLayoutFragmentKind.TextFrame, frame);
                continue;
            }

            switch (block)
            {
                case TableBlock table:
                    LayoutTable(table, index);
                    break;
                case FigureBlock figure:
                    LayoutFigure(figure, index);
                    break;
                case ThematicBreakBlock:
                    LayoutRule(block, index);
                    break;
                default:
                    LayoutText(block, index, KindFor(block), null);
                    break;
            }
        }

        BuildFrameThreads();
        return new PagedLayoutResult(
            _pages.Select(static page => page.Build()).ToArray(),
            _frameThreads.ToArray(),
            _warnings.ToArray());
    }

    public static string FormatPageNumber(int number, PageNumberStyle style)
    {
        number = Math.Max(1, number);
        return style switch
        {
            PageNumberStyle.LowerRoman => ToRoman(number).ToLowerInvariant(),
            PageNumberStyle.UpperRoman => ToRoman(number),
            PageNumberStyle.LowerLetters => ToLetters(number).ToLowerInvariant(),
            PageNumberStyle.UpperLetters => ToLetters(number),
            _ => number.ToString(CultureInfo.InvariantCulture)
        };
    }

    private void Reset(DocumentAst document, BookStyle style)
    {
        _document = document;
        _style = style;
        _section = new SectionFormatting(PageStyleId: style.DefaultPageStyleId);
        _pages.Clear();
        _warnings.Clear();
        _footnotes.Clear();
        _frames.Clear();
        _frameLines.Clear();
        _frameThreads.Clear();
        _wrapExclusions.Clear();
        _columnIndex = 0;
        _cursorY = 0;
        _nextDisplayNumber = 1;
        _fragmentSerial = 0;
    }

    private void ScanDefinitions()
    {
        foreach (var block in _document.Blocks)
        {
            if (block is FootnoteDefinitionBlock footnote)
                _footnotes[footnote.Identifier] = footnote;

            if (block.Formatting?.TextFrame is not { } frame) continue;
            if (!_frames.TryAdd(frame.Id, frame))
                _warnings.Add(new PageLayoutWarning("duplicate-frame", $"Text frame '{frame.Id}' is declared more than once.", block.SourceLine));

            if (!_frameLines.TryGetValue(frame.Id, out var lines))
            {
                lines = [];
                _frameLines[frame.Id] = lines;
            }
            lines.Add(block.SourceLine);
        }
    }

    private void ApplySection(SectionFormatting next)
    {
        var previous = _section;
        var geometryChanged = next.Columns != previous.Columns ||
                              Math.Abs(next.ColumnGapPoints - previous.ColumnGapPoints) > .001 ||
                              next.FacingPages != previous.FacingPages ||
                              !string.Equals(next.PageStyleId ?? _style.DefaultPageStyleId,
                                  previous.PageStyleId ?? _style.DefaultPageStyleId,
                                  StringComparison.OrdinalIgnoreCase);

        _section = next;
        if (next.PageNumberStart is { } restart)
            _nextDisplayNumber = Math.Max(1, restart);

        if (_pages.Count == 0) return;
        if (next.Start == SectionStartMode.Continuous && !geometryChanged && next.PageNumberStart is null) return;

        StartPage();
        if (next.Start is not (SectionStartMode.NextOddPage or SectionStartMode.NextEvenPage)) return;

        var wantOdd = next.Start == SectionStartMode.NextOddPage;
        while (((CurrentPage.PhysicalNumber & 1) == 1) != wantOdd)
        {
            CurrentPage.IsBlank = true;
            StartPage();
        }
    }

    private void EnsurePage()
    {
        if (_pages.Count == 0) StartPage();
    }

    private void StartPage()
    {
        var physical = _pages.Count + 1;
        var geometry = ResolveGeometry(_section, physical);
        var page = new PageBuilder(
            _pages.Count,
            physical,
            _nextDisplayNumber,
            _section.PageNumberStyle,
            geometry,
            _section);
        _pages.Add(page);
        _nextDisplayNumber++;
        _columnIndex = 0;
        _cursorY = page.Columns[0].Bounds.YPoints;
    }

    private PageBuilder CurrentPage => _pages[^1];
    private ColumnBuilder CurrentColumn => CurrentPage.Columns[_columnIndex];
    private double BodyBottom => CurrentPage.BodyBottomPoints;
    private double AvailableHeight => Math.Max(0, BodyBottom - _cursorY);

    private void AdvanceColumn()
    {
        if (_columnIndex + 1 < CurrentPage.Columns.Count)
        {
            _columnIndex++;
            _cursorY = CurrentColumn.Bounds.YPoints;
        }
        else
        {
            StartPage();
        }
    }

    private void ReserveFootnotes(AstBlock block)
    {
        foreach (var id in FootnoteIds(block).Distinct(StringComparer.Ordinal))
        {
            if (!_footnotes.TryGetValue(id, out var definition))
            {
                _warnings.Add(new PageLayoutWarning("missing-footnote", $"Footnote '{id}' has no definition.", block.SourceLine));
                continue;
            }
            if (CurrentPage.HasFootnote(id)) continue;

            var text = definition.Inlines.ToPlainText();
            var metrics = MeasureText(text, CurrentColumn.Bounds.WidthPoints, _style.FootnoteFontSizePoints, 1.05);
            var height = metrics.Lines.Count * metrics.LineHeightPoints + _options.FootnoteGapPoints;
            if (AvailableHeight < height + _options.MinimumLineHeightPoints && CurrentColumn.Fragments.Count > 0)
                AdvanceColumn();
            CurrentPage.ReserveFootnote(id, definition.SourceLine, text, height);
        }
    }

    private void LayoutText(AstBlock block, int blockIndex, PageLayoutFragmentKind kind, TextFrameFormatting? frame)
    {
        var text = TextFor(block);
        var flowBounds = frame is null ? ResolveTextFlowBounds() : CurrentColumn.Bounds;
        var metrics = MeasureBlock(block, text, flowBounds.WidthPoints);
        var paragraph = block.Formatting?.Paragraph;
        var keepTogether = KeepTogether(paragraph, block) || kind == PageLayoutFragmentKind.Heading;

        if (KeepWithNext(paragraph, block) && blockIndex + 1 < _document.Blocks.Count)
        {
            var next = _document.Blocks.Skip(blockIndex + 1)
                .FirstOrDefault(static candidate => candidate is not FootnoteDefinitionBlock and not BibliographyEntryBlock);
            if (next is not null)
            {
                var pairHeight = metrics.TotalHeightPoints + EstimateHeight(next, flowBounds.WidthPoints);
                if (pairHeight <= CurrentColumn.Bounds.HeightPoints && pairHeight > AvailableHeight)
                    AdvanceColumn();
            }
        }

        if (keepTogether && metrics.TotalHeightPoints <= CurrentColumn.Bounds.HeightPoints && metrics.TotalHeightPoints > AvailableHeight)
            AdvanceColumn();

        var frameChain = frame is null ? Array.Empty<string>() : FollowFrameChain(frame.Id).ToArray();
        var frameSlot = 0;
        var lineOffset = 0;
        var fragmentIndex = 0;

        while (lineOffset < metrics.Lines.Count)
        {
            if (frame is not null && frameSlot >= frameChain.Length)
            {
                AddOverset(block, blockIndex, frame.Id, metrics.Lines.Skip(lineOffset));
                return;
            }

            var before = fragmentIndex == 0 ? metrics.SpaceBeforePoints : 0;
            var available = AvailableHeight - before;
            if (available < metrics.LineHeightPoints)
            {
                AdvanceColumn();
                if (frame is not null) frameSlot++;
                continue;
            }

            var fit = Math.Min(
                metrics.Lines.Count - lineOffset,
                Math.Max(1, (int)Math.Floor(available / metrics.LineHeightPoints)));
            var remaining = metrics.Lines.Count - lineOffset;
            if (fit < remaining)
            {
                if (keepTogether && metrics.TotalHeightPoints > CurrentColumn.Bounds.HeightPoints)
                    _warnings.Add(new PageLayoutWarning("forced-split", "A keep-lines-together block exceeded a full column and had to split.", block.SourceLine));

                var firstMinimum = KeepFirstLines(paragraph);
                var lastMinimum = KeepLastLines(paragraph);
                if (lineOffset == 0 && fit < firstMinimum)
                {
                    AdvanceColumn();
                    if (frame is not null) frameSlot++;
                    continue;
                }
                if (remaining - fit < lastMinimum)
                {
                    var adjusted = remaining - lastMinimum;
                    if (adjusted >= (lineOffset == 0 ? firstMinimum : 1)) fit = adjusted;
                }
            }

            var last = lineOffset + fit >= metrics.Lines.Count;
            var after = last ? metrics.SpaceAfterPoints : 0;
            var height = before + fit * metrics.LineHeightPoints + after;
            var bounds = new PageLayoutRect(flowBounds.XPoints, _cursorY, flowBounds.WidthPoints, height);
            var frameId = frame is null ? null : frameChain[frameSlot];
            var firstWrappedLine = metrics.Lines[lineOffset];
            var lastWrappedLine = metrics.Lines[lineOffset + fit - 1];
            var sourceTextStart = firstWrappedLine.Start;
            var sourceTextLength = Math.Max(
                0,
                lastWrappedLine.Start + lastWrappedLine.Length - sourceTextStart);
            CurrentColumn.Fragments.Add(new PageLayoutFragment(
                NextId(),
                kind,
                block.SourceLine,
                blockIndex,
                bounds,
                string.Join('\n', metrics.Lines.Skip(lineOffset).Take(fit).Select(static line => line.Text)),
                FragmentIndex: fragmentIndex,
                IsContinuation: fragmentIndex > 0,
                FrameId: frameId,
                FrameColumns: Math.Max(1, frame?.Columns ?? 1),
                SourceTextStart: sourceTextStart,
                SourceTextLength: sourceTextLength));
            _cursorY += height;
            lineOffset += fit;
            fragmentIndex++;

            if (lineOffset < metrics.Lines.Count)
            {
                AdvanceColumn();
                if (frame is not null) frameSlot++;
            }
        }
    }

    private void AddOverset(AstBlock block, int blockIndex, string frameId, IEnumerable<WrappedLine> remaining)
    {
        var size = Math.Min(_options.OversetIndicatorHeightPoints, Math.Max(4, AvailableHeight));
        var bounds = new PageLayoutRect(
            Math.Max(CurrentColumn.Bounds.XPoints, CurrentColumn.Bounds.RightPoints - size),
            Math.Min(_cursorY, Math.Max(CurrentColumn.Bounds.YPoints, BodyBottom - size)),
            size,
            size);
        CurrentColumn.Fragments.Add(new PageLayoutFragment(
            NextId(),
            PageLayoutFragmentKind.OversetIndicator,
            block.SourceLine,
            blockIndex,
            bounds,
            "+",
            IsContinuation: true,
            FrameId: frameId));
        _warnings.Add(new PageLayoutWarning(
            "overset",
            $"Text frame '{frameId}' has overset text ({remaining.Sum(static line => line.Text.Length)} characters remain).",
            block.SourceLine));
    }

    private void LayoutRule(AstBlock block, int blockIndex)
    {
        const double height = 12;
        if (AvailableHeight < height) AdvanceColumn();
        var bounds = new PageLayoutRect(CurrentColumn.Bounds.XPoints, _cursorY, CurrentColumn.Bounds.WidthPoints, height);
        CurrentColumn.Fragments.Add(new PageLayoutFragment(
            NextId(), PageLayoutFragmentKind.ThematicBreak, block.SourceLine, blockIndex, bounds, "—"));
        _cursorY += height;
    }

    private void LayoutFigure(FigureBlock figure, int blockIndex)
    {
        var anchored = figure.Formatting?.AnchoredObject;
        var named = _style.NamedStyles.ResolveFigure(figure.StyleId ?? _style.DefaultFigureStyleId);
        var widthPercent = Math.Clamp(figure.Layout?.WidthPercent ?? named?.MaxWidthPercent ?? 90, 10, 100);
        var width = CurrentColumn.Bounds.WidthPoints * widthPercent / 100d;
        var height = figure.Layout?.HeightInches is { } inches
            ? Math.Max(_options.MinimumFigureHeightPoints, inches * 72d)
            : Math.Max(_options.MinimumFigureHeightPoints, width * _options.DefaultFigureAspectRatio);
        if (!string.IsNullOrWhiteSpace(figure.Caption)) height += _style.CaptionFontSizePoints * 1.3;

        var placement = anchored?.Placement ?? ToFloatPlacement(figure.Layout?.Placement ?? named?.Placement);
        if (placement is FloatPlacementMode.Top or FloatPlacementMode.Page && CurrentColumn.Fragments.Count > 0)
            AdvanceColumn();
        if (placement is not FloatPlacementMode.Margin && AvailableHeight < height && CurrentColumn.Fragments.Count > 0)
            AdvanceColumn();

        height = Math.Min(height, CurrentColumn.Bounds.HeightPoints);
        var alignment = figure.Layout?.Alignment ?? named?.Alignment ?? FigureAlignment.Center;
        var x = alignment switch
        {
            FigureAlignment.Left => CurrentColumn.Bounds.XPoints,
            FigureAlignment.Right => CurrentColumn.Bounds.RightPoints - width,
            _ => CurrentColumn.Bounds.XPoints + (CurrentColumn.Bounds.WidthPoints - width) / 2
        };
        var y = _cursorY;
        if (placement == FloatPlacementMode.Bottom) y = Math.Max(_cursorY, BodyBottom - height);
        if (placement == FloatPlacementMode.Page) y = CurrentColumn.Bounds.YPoints + Math.Max(0, (CurrentColumn.Bounds.HeightPoints - height) / 2);
        if (placement == FloatPlacementMode.Margin)
        {
            var margin = Math.Max(54, (CurrentPage.Geometry.WidthPoints - CurrentPage.Geometry.ContentBounds.WidthPoints) * .8);
            width = margin;
            height = Math.Min(height, CurrentPage.Geometry.ContentBounds.HeightPoints / 2);
            x = CurrentPage.IsLeftPage
                ? Math.Max(0, CurrentPage.Geometry.ContentBounds.XPoints - margin - 6)
                : Math.Min(CurrentPage.Geometry.WidthPoints - margin, CurrentPage.Geometry.ContentBounds.RightPoints + 6);
        }

        var bounds = new PageLayoutRect(
            x + (anchored?.OffsetXPoints ?? 0),
            y + (anchored?.OffsetYPoints ?? 0),
            width,
            height);
        var wrap = anchored?.Wrap ?? TextWrapMode.None;
        var kind = placement switch
        {
            FloatPlacementMode.Margin => PageLayoutFragmentKind.MarginNote,
            _ when wrap is TextWrapMode.BoundingBox or TextWrapMode.Contour or TextWrapMode.JumpObject
                => PageLayoutFragmentKind.FloatingObject,
            FloatPlacementMode.Inline or FloatPlacementMode.Here => PageLayoutFragmentKind.Figure,
            _ => PageLayoutFragmentKind.FloatingObject
        };
        var fragment = new PageLayoutFragment(
            NextId(),
            kind,
            figure.SourceLine,
            blockIndex,
            bounds,
            string.IsNullOrWhiteSpace(figure.Caption) ? figure.Source : figure.Caption,
            AnchorId: anchored?.Id,
            Wrap: wrap,
            Placement: placement);

        if (kind is PageLayoutFragmentKind.FloatingObject or PageLayoutFragmentKind.MarginNote)
            CurrentPage.FloatingObjects.Add(fragment);
        else
            CurrentColumn.Fragments.Add(fragment);

        if (placement == FloatPlacementMode.Margin)
            return;

        if (wrap is TextWrapMode.BoundingBox or TextWrapMode.Contour)
        {
            _wrapExclusions.Add(new WrapExclusion(
                CurrentPage.Index,
                _columnIndex,
                bounds,
                wrap,
                anchored?.WrapTopPoints ?? 0,
                anchored?.WrapRightPoints ?? 0,
                anchored?.WrapBottomPoints ?? 0,
                anchored?.WrapLeftPoints ?? 0));
            return;
        }

        if (wrap == TextWrapMode.JumpObject)
        {
            _cursorY = Math.Max(_cursorY, bounds.BottomPoints + (anchored?.WrapBottomPoints ?? 0));
            return;
        }

        _cursorY = Math.Max(_cursorY, bounds.BottomPoints);
    }

    private PageLayoutRect ResolveTextFlowBounds()
    {
        var column = CurrentColumn.Bounds;
        var minimumSideWidth = Math.Max(72, _style.BodyFontSizePoints * 8);

        var exclusion = _wrapExclusions
            .Where(item =>
                item.PageIndex == CurrentPage.Index &&
                item.ColumnIndex == _columnIndex &&
                item.Bounds.BottomPoints + item.BottomPoints > _cursorY)
            .OrderByDescending(static item => item.Bounds.YPoints)
            .FirstOrDefault();

        if (exclusion is null)
            return column;

        var leftEdge = exclusion.Bounds.XPoints - exclusion.LeftPoints;
        var rightEdge = exclusion.Bounds.RightPoints + exclusion.RightPoints;
        var leftWidth = Math.Max(0, leftEdge - column.XPoints);
        var rightWidth = Math.Max(0, column.RightPoints - rightEdge);

        if (leftWidth < minimumSideWidth && rightWidth < minimumSideWidth)
        {
            _cursorY = Math.Max(_cursorY, exclusion.Bounds.BottomPoints + exclusion.BottomPoints);
            return column;
        }

        if (rightWidth >= leftWidth)
            return new PageLayoutRect(rightEdge, _cursorY, rightWidth, Math.Max(0, BodyBottom - _cursorY));

        return new PageLayoutRect(column.XPoints, _cursorY, leftWidth, Math.Max(0, BodyBottom - _cursorY));
    }

    private void LayoutTable(TableBlock table, int blockIndex)
    {
        var headers = HeaderRows(table);
        var body = BodyRows(table, headers.BodyHeaderCount);
        var allHeight = headers.Rows.Sum(row => MeasureRow(row.Cells, CurrentColumn.Bounds.WidthPoints, row.Properties).HeightPoints) +
                        body.Sum(row => MeasureRow(row.Cells, CurrentColumn.Bounds.WidthPoints, row.Properties).HeightPoints);
        if (table.Properties?.KeepTogether == true && allHeight <= CurrentColumn.Bounds.HeightPoints && allHeight > AvailableHeight)
            AdvanceColumn();

        if (!string.IsNullOrWhiteSpace(table.Caption))
        {
            var captionHeight = _style.CaptionFontSizePoints * 1.4;
            if (AvailableHeight < captionHeight) AdvanceColumn();
            var bounds = new PageLayoutRect(CurrentColumn.Bounds.XPoints, _cursorY, CurrentColumn.Bounds.WidthPoints, captionHeight);
            CurrentColumn.Fragments.Add(new PageLayoutFragment(
                NextId(), PageLayoutFragmentKind.TableCaption, table.SourceLine, blockIndex, bounds, table.Caption!));
            _cursorY += captionHeight;
        }

        foreach (var header in headers.Rows)
            RenderRow(table, blockIndex, header, repeated: false);

        foreach (var row in body)
        {
            var measured = MeasureRow(row.Cells, CurrentColumn.Bounds.WidthPoints, row.Properties);
            if (measured.HeightPoints > AvailableHeight)
            {
                AdvanceColumn();
                foreach (var header in headers.Rows)
                    RenderRow(table, blockIndex, header, repeated: true);
            }
            RenderRow(table, blockIndex, row, repeated: false);
        }
    }

    private void RenderRow(TableBlock table, int blockIndex, TableRowLayout row, bool repeated)
    {
        var measured = MeasureRow(row.Cells, CurrentColumn.Bounds.WidthPoints, row.Properties);
        if (measured.HeightPoints > AvailableHeight && CurrentColumn.Fragments.Count > 0)
            AdvanceColumn();
        var height = Math.Min(measured.HeightPoints, CurrentColumn.Bounds.HeightPoints);
        if (measured.HeightPoints > CurrentColumn.Bounds.HeightPoints)
            _warnings.Add(new PageLayoutWarning("table-row-overset", $"Table row {row.RowIndex + 1} exceeds a full column.", table.SourceLine));
        var bounds = new PageLayoutRect(CurrentColumn.Bounds.XPoints, _cursorY, CurrentColumn.Bounds.WidthPoints, height);
        CurrentColumn.Fragments.Add(new PageLayoutFragment(
            NextId(),
            row.IsHeader ? PageLayoutFragmentKind.TableHeader : PageLayoutFragmentKind.TableRow,
            table.SourceLine,
            blockIndex,
            bounds,
            measured.Text,
            IsRepeatedHeader: repeated,
            TableRowIndex: row.RowIndex));
        _cursorY += height;
    }

    private (IReadOnlyList<TableRowLayout> Rows, int BodyHeaderCount) HeaderRows(TableBlock table)
    {
        var rows = new List<TableRowLayout>
        {
            new(-1, table.Header, new TableRowProperties(RepeatAsHeader: true), true)
        };
        var bodyHeaderCount = Math.Max(0, table.RepeatHeaderRows - 1);
        for (var index = 0; index < table.Rows.Count; index++)
        {
            var properties = RowProperties(table, index);
            if (index < bodyHeaderCount || properties.RepeatAsHeader)
                rows.Add(new TableRowLayout(index, table.Rows[index], properties, true));
        }
        return (rows, bodyHeaderCount);
    }

    private IReadOnlyList<TableRowLayout> BodyRows(TableBlock table, int bodyHeaderCount)
    {
        var rows = new List<TableRowLayout>();
        for (var index = 0; index < table.Rows.Count; index++)
        {
            var properties = RowProperties(table, index);
            if (index < bodyHeaderCount || properties.RepeatAsHeader) continue;
            rows.Add(new TableRowLayout(index, table.Rows[index], properties, false));
        }
        return rows;
    }

    private static TableRowProperties RowProperties(TableBlock table, int index)
        => table.RowProperties is { Count: > 0 } && index < table.RowProperties.Count
            ? table.RowProperties[index]
            : new TableRowProperties();

    private TableRowMetrics MeasureRow(IReadOnlyList<TableCell> cells, double width, TableRowProperties properties)
    {
        var cellWidth = width / Math.Max(1, cells.Count);
        var lines = 1;
        var values = new List<string>(cells.Count);
        foreach (var cell in cells)
        {
            var text = cell.Inlines.ToPlainText();
            values.Add(text);
            lines = Math.Max(lines, MeasureText(text, Math.Max(12, cellWidth - 8), _style.BodyFontSizePoints, _style.LineSpacing).Lines.Count);
        }
        var height = lines * Math.Max(_options.MinimumLineHeightPoints, _style.BodyFontSizePoints * _style.LineSpacing) + 8;
        if (properties.MinimumHeightPoints is { } minimum) height = Math.Max(height, minimum);
        return new TableRowMetrics(height, string.Join(" | ", values));
    }

    private BlockMetrics MeasureBlock(AstBlock block, string text, double width)
    {
        var paragraph = block.Formatting?.Paragraph;
        var named = !string.IsNullOrWhiteSpace(paragraph?.StyleId)
            ? _style.NamedStyles.ResolveParagraph(paragraph!.StyleId)
            : block is HeadingBlock ? null : _style.NamedStyles.ResolveParagraph(_style.DefaultParagraphStyleId);
        var fontSize = paragraph?.CharacterDefaults?.FontSizePoints ?? named?.FontSizePoints ?? FontSize(block);
        var lineSpacing = paragraph?.LineSpacing ?? named?.LineSpacing ?? _style.LineSpacing;
        var before = paragraph?.SpaceBeforePoints ?? named?.SpaceBeforePoints ?? SpaceBefore(block);
        var after = paragraph?.SpaceAfterPoints ?? named?.SpaceAfterPoints ?? SpaceAfter(block);
        var measured = MeasureText(text, width, fontSize, lineSpacing);
        return new BlockMetrics(measured.Lines, measured.LineHeightPoints, before, after);
    }

    private double EstimateHeight(AstBlock block, double width)
        => block switch
        {
            ThematicBreakBlock => 12,
            FigureBlock figure => Math.Max(_options.MinimumFigureHeightPoints,
                width * Math.Clamp(figure.Layout?.WidthPercent ?? 90, 10, 100) / 100 * _options.DefaultFigureAspectRatio),
            TableBlock table => HeaderRows(table).Rows.Sum(row => MeasureRow(row.Cells, width, row.Properties).HeightPoints) +
                                BodyRows(table, Math.Max(0, table.RepeatHeaderRows - 1)).Sum(row => MeasureRow(row.Cells, width, row.Properties).HeightPoints),
            _ => MeasureBlock(block, TextFor(block), width).TotalHeightPoints
        };

    private TextMetrics MeasureText(string text, double width, double fontSize, double lineSpacing)
    {
        var glyphWidth = Math.Max(2.5, fontSize * _options.AverageGlyphWidthFactor);
        var capacity = Math.Max(4, (int)Math.Floor(width / glyphWidth));
        return new TextMetrics(Wrap(text, capacity), Math.Max(_options.MinimumLineHeightPoints, fontSize * Math.Max(.8, lineSpacing)));
    }

    private static IReadOnlyList<WrappedLine> Wrap(string text, int capacity)
    {
        if (string.IsNullOrEmpty(text)) return [new WrappedLine(string.Empty, 0, 0)];

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var output = new List<WrappedLine>();
        var logicalStart = 0;

        while (logicalStart <= normalized.Length)
        {
            var newline = normalized.IndexOf('\n', logicalStart);
            var logicalEnd = newline >= 0 ? newline : normalized.Length;
            var logical = normalized.AsSpan(logicalStart, logicalEnd - logicalStart);

            var builder = new StringBuilder();
            var used = 0;
            var localOffset = 0;
            var pieceStart = 0;

            while (localOffset < logical.Length)
            {
                var status = Rune.DecodeFromUtf16(logical[localOffset..], out var rune, out var consumed);
                if (status != System.Buffers.OperationStatus.Done || consumed <= 0)
                {
                    rune = new Rune(logical[localOffset]);
                    consumed = 1;
                }

                var width = RuneWidth(rune);
                if (used > 0 && used + width > capacity)
                {
                    output.Add(new WrappedLine(
                        builder.ToString(),
                        logicalStart + pieceStart,
                        localOffset - pieceStart));
                    builder.Clear();
                    used = 0;
                    pieceStart = localOffset;
                }

                builder.Append(rune.ToString());
                used += width;
                localOffset += consumed;
            }

            output.Add(new WrappedLine(
                builder.ToString(),
                logicalStart + pieceStart,
                logical.Length - pieceStart));

            if (newline < 0) break;
            logicalStart = newline + 1;
        }

        return output.Count == 0 ? [new WrappedLine(string.Empty, 0, 0)] : output;
    }

    private static int RuneWidth(Rune rune)
        => rune.Value == '\t' ? 4 : rune.Value is >= 0x1100 and <= 0x11FF or >= 0x2E80 and <= 0xA4CF or >= 0x1F000 ? 2 : 1;

    private double FontSize(AstBlock block)
        => block switch
        {
            HeadingBlock { Level: 1 } => _style.ChapterFontSizePoints,
            HeadingBlock { Level: 2 } => _style.SectionFontSizePoints,
            HeadingBlock { Level: 3 } => _style.SubsectionFontSizePoints,
            HeadingBlock => _style.SubsubsectionFontSizePoints,
            QuoteBlock => _style.QuoteFontSizePoints,
            CodeBlock => _style.CodeFontSizePoints,
            _ => _style.BodyFontSizePoints
        };

    private double SpaceBefore(AstBlock block)
        => block switch
        {
            HeadingBlock { Level: 1 } => _style.ChapterBeforeSpacingPoints,
            HeadingBlock => _style.SectionBeforeSpacingPoints,
            _ => 0
        };

    private double SpaceAfter(AstBlock block)
        => block switch
        {
            HeadingBlock { Level: 1 } => _style.ChapterAfterSpacingPoints,
            HeadingBlock => _style.SectionAfterSpacingPoints,
            ParagraphBlock => _style.ParagraphSpacingPoints,
            _ => 0
        };

    private bool KeepWithNext(ParagraphFormatting? paragraph, AstBlock block)
    {
        if (paragraph?.KeepWithNext is { } direct) return direct;
        var named = !string.IsNullOrWhiteSpace(paragraph?.StyleId) ? _style.NamedStyles.ResolveParagraph(paragraph!.StyleId) : null;
        return named?.KeepWithNext ?? block is HeadingBlock;
    }

    private bool KeepTogether(ParagraphFormatting? paragraph, AstBlock block)
    {
        if (paragraph?.KeepLinesTogether is { } direct) return direct;
        var named = !string.IsNullOrWhiteSpace(paragraph?.StyleId) ? _style.NamedStyles.ResolveParagraph(paragraph!.StyleId) : null;
        return named?.KeepLinesTogether ?? block is HeadingBlock;
    }

    private int KeepFirstLines(ParagraphFormatting? paragraph)
    {
        if (paragraph?.KeepFirstLines is { } direct) return Math.Max(1, direct);
        var named = !string.IsNullOrWhiteSpace(paragraph?.StyleId) ? _style.NamedStyles.ResolveParagraph(paragraph!.StyleId) : null;
        if (named?.KeepFirstLines is { } inherited) return Math.Max(1, inherited);
        return _style.AvoidWidowsAndOrphans ? Math.Max(1, _options.DefaultOrphanLines) : 1;
    }

    private int KeepLastLines(ParagraphFormatting? paragraph)
    {
        if (paragraph?.KeepLastLines is { } direct) return Math.Max(1, direct);
        var named = !string.IsNullOrWhiteSpace(paragraph?.StyleId) ? _style.NamedStyles.ResolveParagraph(paragraph!.StyleId) : null;
        if (named?.KeepLastLines is { } inherited) return Math.Max(1, inherited);
        return _style.AvoidWidowsAndOrphans ? Math.Max(1, _options.DefaultWidowLines) : 1;
    }

    private IEnumerable<string> FollowFrameChain(string root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = root;
        while (!string.IsNullOrWhiteSpace(current) && _frames.TryGetValue(current, out var frame))
        {
            if (!seen.Add(current))
            {
                _warnings.Add(new PageLayoutWarning("frame-cycle", $"Text frame thread contains a cycle at '{current}'."));
                yield break;
            }
            yield return current;
            current = frame.NextFrameId ?? string.Empty;
        }
        if (!string.IsNullOrWhiteSpace(current))
            _warnings.Add(new PageLayoutWarning("missing-frame", $"Text frame thread references missing frame '{current}'."));
    }

    private void BuildFrameThreads()
    {
        var referenced = _frames.Values.Select(static frame => frame.NextFrameId)
            .Where(static id => !string.IsNullOrWhiteSpace(id)).Cast<string>().ToHashSet(StringComparer.Ordinal);
        var candidates = _frames.Keys.Where(id => !referenced.Contains(id))
            .Concat(_frames.Keys).Distinct(StringComparer.Ordinal).OrderBy(static id => id, StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in candidates)
        {
            if (emitted.Contains(root)) continue;
            var ids = new List<string>();
            var lines = new List<int>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = root;
            var overset = false;
            while (!string.IsNullOrWhiteSpace(current) && _frames.TryGetValue(current, out var frame))
            {
                if (!seen.Add(current))
                {
                    overset = true;
                    break;
                }
                emitted.Add(current);
                ids.Add(current);
                if (_frameLines.TryGetValue(current, out var frameSourceLines)) lines.AddRange(frameSourceLines);
                current = frame.NextFrameId ?? string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(current)) overset = true;
            overset |= _warnings.Any(warning => warning.Code == "overset" && warning.SourceLine is { } line && lines.Contains(line));
            _frameThreads.Add(new PageLayoutFrameThread(root, ids, lines.Order().ToArray(), overset));
        }
    }

    private ResolvedPageGeometry ResolveGeometry(SectionFormatting section, int physicalNumber)
    {
        var pageStyleId = section.PageStyleId ?? _style.DefaultPageStyleId;
        var page = _style.NamedStyles.ResolvePage(pageStyleId);
        var width = (page?.WidthInches ?? _style.PageWidthInches) * 72;
        var height = (page?.HeightInches ?? _style.PageHeightInches) * 72;
        if (page?.Landscape == true) (width, height) = (height, width);
        var facing = section.FacingPages || page?.FacingPages == true;
        var leftPage = facing && physicalNumber % 2 == 0;
        var inner = (page?.MarginInnerInches ?? _style.MarginInnerInches) * 72;
        var outer = (page?.MarginOuterInches ?? _style.MarginOuterInches) * 72;
        var left = facing ? (leftPage ? outer : inner) : inner;
        var right = facing ? (leftPage ? inner : outer) : outer;
        var top = (page?.MarginTopInches ?? _style.MarginTopInches) * 72;
        var bottom = (page?.MarginBottomInches ?? _style.MarginBottomInches) * 72;
        var content = new PageLayoutRect(left, top, Math.Max(36, width - left - right), Math.Max(36, height - top - bottom));
        return new ResolvedPageGeometry(
            width,
            height,
            content,
            facing,
            leftPage,
            pageStyleId,
            section.BaselineGrid ?? page?.BaselineGrid,
            section.CropMarks || page?.CropMarks == true,
            section.BleedTopPoints > 0 ? section.BleedTopPoints : (page?.BleedTopInches ?? 0) * 72,
            section.BleedBottomPoints > 0 ? section.BleedBottomPoints : (page?.BleedBottomInches ?? 0) * 72,
            section.BleedInsidePoints > 0 ? section.BleedInsidePoints : (page?.BleedInsideInches ?? 0) * 72,
            section.BleedOutsidePoints > 0 ? section.BleedOutsidePoints : (page?.BleedOutsideInches ?? 0) * 72,
            section.SlugPoints > 0 ? section.SlugPoints : (page?.SlugInches ?? 0) * 72);
    }

    private static PageLayoutFragmentKind KindFor(AstBlock block)
        => block switch
        {
            HeadingBlock => PageLayoutFragmentKind.Heading,
            QuoteBlock => PageLayoutFragmentKind.Quote,
            ListItemBlock => PageLayoutFragmentKind.ListItem,
            CodeBlock => PageLayoutFragmentKind.Code,
            DisplayMathBlock => PageLayoutFragmentKind.DisplayMath,
            _ => PageLayoutFragmentKind.Paragraph
        };

    private static string TextFor(AstBlock block)
        => block switch
        {
            HeadingBlock heading => heading.Inlines.ToPlainText(),
            ParagraphBlock paragraph => paragraph.Inlines.ToPlainText(),
            QuoteBlock quote => quote.Inlines.ToPlainText(),
            ListItemBlock item => (item.Ordered ? $"{item.Number ?? 1}. " : "• ") + item.Inlines.ToPlainText(),
            CodeBlock code => code.Text,
            DisplayMathBlock math => math.Text,
            FigureBlock figure => figure.Caption,
            _ => string.Empty
        };

    private static IEnumerable<string> FootnoteIds(AstBlock block)
    {
        IEnumerable<AstInline> inlines = block switch
        {
            HeadingBlock heading => heading.Inlines,
            ParagraphBlock paragraph => paragraph.Inlines,
            QuoteBlock quote => quote.Inlines,
            ListItemBlock item => item.Inlines,
            TableBlock table => table.Header.SelectMany(static cell => cell.Inlines)
                .Concat(table.Rows.SelectMany(static row => row).SelectMany(static cell => cell.Inlines)),
            _ => []
        };
        return FootnoteIds(inlines);
    }

    private static IEnumerable<string> FootnoteIds(IEnumerable<AstInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case FootnoteReferenceInline reference:
                    yield return reference.Identifier;
                    break;
                case StrongInline strong:
                    foreach (var id in FootnoteIds(strong.Children)) yield return id;
                    break;
                case EmphasisInline emphasis:
                    foreach (var id in FootnoteIds(emphasis.Children)) yield return id;
                    break;
                case LinkInline link:
                    foreach (var id in FootnoteIds(link.Label)) yield return id;
                    break;
                case RichSpanInline rich:
                    foreach (var id in FootnoteIds(rich.Children)) yield return id;
                    break;
            }
        }
    }

    private static FloatPlacementMode ToFloatPlacement(FigurePlacement? placement)
        => placement switch
        {
            FigurePlacement.Inline => FloatPlacementMode.Inline,
            FigurePlacement.Top => FloatPlacementMode.Top,
            FigurePlacement.Bottom => FloatPlacementMode.Bottom,
            FigurePlacement.Page => FloatPlacementMode.Page,
            _ => FloatPlacementMode.Here
        };

    private static string ToRoman(int number)
    {
        var values = new (int Value, string Token)[]
        {
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
            (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
        };
        var result = new StringBuilder();
        foreach (var (value, token) in values)
        {
            while (number >= value)
            {
                result.Append(token);
                number -= value;
            }
        }
        return result.ToString();
    }

    private static string ToLetters(int number)
    {
        var result = new StringBuilder();
        while (number > 0)
        {
            number--;
            result.Insert(0, (char)('A' + number % 26));
            number /= 26;
        }
        return result.ToString();
    }

    private string NextId() => $"layout-{_fragmentSerial++:D6}";

    private sealed record WrapExclusion(
        int PageIndex,
        int ColumnIndex,
        PageLayoutRect Bounds,
        TextWrapMode Mode,
        double TopPoints,
        double RightPoints,
        double BottomPoints,
        double LeftPoints);

    private sealed record WrappedLine(string Text, int Start, int Length);
    private sealed record TextMetrics(IReadOnlyList<WrappedLine> Lines, double LineHeightPoints);

    private sealed record BlockMetrics(
        IReadOnlyList<WrappedLine> Lines,
        double LineHeightPoints,
        double SpaceBeforePoints,
        double SpaceAfterPoints)
    {
        public double TotalHeightPoints => SpaceBeforePoints + Lines.Count * LineHeightPoints + SpaceAfterPoints;
    }

    private sealed record TableRowMetrics(double HeightPoints, string Text);
    private sealed record TableRowLayout(int RowIndex, IReadOnlyList<TableCell> Cells, TableRowProperties Properties, bool IsHeader);

    private sealed record ResolvedPageGeometry(
        double WidthPoints,
        double HeightPoints,
        PageLayoutRect ContentBounds,
        bool FacingPages,
        bool IsLeftPage,
        string? PageStyleId,
        BaselineGridFormatting? BaselineGrid,
        bool CropMarks,
        double BleedTopPoints,
        double BleedBottomPoints,
        double BleedInsidePoints,
        double BleedOutsidePoints,
        double SlugPoints);

    private sealed class ColumnBuilder(int index, PageLayoutRect bounds)
    {
        public int Index { get; } = index;
        public PageLayoutRect Bounds { get; } = bounds;
        public List<PageLayoutFragment> Fragments { get; } = [];
        public PageLayoutColumn Build() => new(Index, Bounds, Fragments.ToArray());
    }

    private sealed class PageBuilder
    {
        private readonly Dictionary<string, FootnoteReservation> _footnotes = new(StringComparer.Ordinal);

        public PageBuilder(
            int index,
            int physicalNumber,
            int displayNumber,
            PageNumberStyle numberStyle,
            ResolvedPageGeometry geometry,
            SectionFormatting section)
        {
            Index = index;
            PhysicalNumber = physicalNumber;
            DisplayNumber = displayNumber;
            NumberStyle = numberStyle;
            Geometry = geometry;
            var count = Math.Max(1, section.Columns);
            var gap = Math.Max(0, section.ColumnGapPoints);
            var width = Math.Max(12, (geometry.ContentBounds.WidthPoints - gap * (count - 1)) / count);
            Columns = Enumerable.Range(0, count)
                .Select(column => new ColumnBuilder(
                    column,
                    new PageLayoutRect(
                        geometry.ContentBounds.XPoints + column * (width + gap),
                        geometry.ContentBounds.YPoints,
                        width,
                        geometry.ContentBounds.HeightPoints)))
                .ToList();
        }

        public int Index { get; }
        public int PhysicalNumber { get; }
        public int DisplayNumber { get; }
        public PageNumberStyle NumberStyle { get; }
        public ResolvedPageGeometry Geometry { get; }
        public bool IsLeftPage => Geometry.IsLeftPage;
        public bool IsBlank { get; set; }
        public List<ColumnBuilder> Columns { get; }
        public List<PageLayoutFragment> FloatingObjects { get; } = [];
        public double ReservedFootnoteHeightPoints { get; private set; }
        public double BodyBottomPoints => Geometry.ContentBounds.BottomPoints - ReservedFootnoteHeightPoints;

        public bool HasFootnote(string id) => _footnotes.ContainsKey(id);

        public void ReserveFootnote(string id, int sourceLine, string text, double height)
        {
            if (_footnotes.ContainsKey(id)) return;
            _footnotes[id] = new FootnoteReservation(sourceLine, text, height);
            ReservedFootnoteHeightPoints += height;
        }

        public PageLayoutPage Build()
        {
            var footnotes = new List<PageLayoutFragment>();
            var y = Geometry.ContentBounds.BottomPoints - ReservedFootnoteHeightPoints;
            var serial = 0;
            foreach (var reservation in _footnotes.Values)
            {
                var bounds = new PageLayoutRect(Geometry.ContentBounds.XPoints, y, Geometry.ContentBounds.WidthPoints, reservation.HeightPoints);
                footnotes.Add(new PageLayoutFragment(
                    $"footnote-{Index:D4}-{serial++:D3}",
                    PageLayoutFragmentKind.Footnote,
                    reservation.SourceLine,
                    -1,
                    bounds,
                    reservation.Text));
                y += reservation.HeightPoints;
            }

            return new PageLayoutPage(
                Index: Index,
                PhysicalNumber: PhysicalNumber,
                DisplayNumber: DisplayNumber,
                DisplayNumberText: FormatPageNumber(DisplayNumber, NumberStyle),
                NumberStyle: NumberStyle,
                IsLeftPage: Geometry.IsLeftPage,
                IsBlank: IsBlank,
                FacingPages: Geometry.FacingPages,
                WidthPoints: Geometry.WidthPoints,
                HeightPoints: Geometry.HeightPoints,
                ContentBounds: Geometry.ContentBounds,
                Columns: Columns.Select(static column => column.Build()).ToArray(),
                FloatingObjects: FloatingObjects.ToArray(),
                Footnotes: footnotes,
                Guides: BuildGuides(),
                BaselineGrid: Geometry.BaselineGrid,
                PageStyleId: Geometry.PageStyleId,
                CropMarks: Geometry.CropMarks,
                BleedTopPoints: Geometry.BleedTopPoints,
                BleedBottomPoints: Geometry.BleedBottomPoints,
                BleedInsidePoints: Geometry.BleedInsidePoints,
                BleedOutsidePoints: Geometry.BleedOutsidePoints,
                SlugPoints: Geometry.SlugPoints);
        }

        private IReadOnlyList<PageLayoutGuide> BuildGuides()
        {
            var guides = new List<PageLayoutGuide>
            {
                new(PageLayoutGuideOrientation.Vertical, Geometry.ContentBounds.XPoints, "margin"),
                new(PageLayoutGuideOrientation.Vertical, Geometry.ContentBounds.RightPoints, "margin"),
                new(PageLayoutGuideOrientation.Horizontal, Geometry.ContentBounds.YPoints, "margin"),
                new(PageLayoutGuideOrientation.Horizontal, Geometry.ContentBounds.BottomPoints, "margin")
            };
            foreach (var column in Columns)
            {
                guides.Add(new PageLayoutGuide(PageLayoutGuideOrientation.Vertical, column.Bounds.XPoints, "column"));
                guides.Add(new PageLayoutGuide(PageLayoutGuideOrientation.Vertical, column.Bounds.RightPoints, "column"));
            }
            return guides;
        }

        private sealed record FootnoteReservation(int SourceLine, string Text, double HeightPoints);
    }
}
