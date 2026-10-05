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

public sealed record PageLayoutGuide(
    PageLayoutGuideOrientation Orientation,
    double PositionPoints,
    string Kind);

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
    int FrameColumns = 1);

public sealed record PageLayoutColumn(
    int Index,
    PageLayoutRect Bounds,
    IReadOnlyList<PageLayoutFragment> Fragments);

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

/// <summary>
/// Stable layout tuning values used by the Markdown-first pagination engine. The core engine does
/// not depend on a UI text formatter; it deliberately uses deterministic point-based estimates.
/// A page view may render the resulting boxes with the platform shaper while canonical ordering,
/// page breaks, keep rules, frame threading and page-number state stay reproducible.
/// </summary>
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
/// Deterministic page-layout engine derived entirely from Markdown AST + styles + TypeScribe block
/// metadata. It never mutates, reorders or duplicates authored text. Physical layout is a projection
/// that can be discarded and recomputed at any time.
/// </summary>
public sealed class PagedLayoutEngine
{
    private readonly PagedLayoutOptions _options;
    private readonly List<PageBuilder> _pages = [];
    private readonly List<PageLayoutWarning> _warnings = [];
    private readonly Dictionary<string, FootnoteDefinitionBlock> _footnotes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextFrameFormatting> _frames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _frameSourceLines = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedFrameIds = new(StringComparer.Ordinal);
    private readonly List<PageLayoutFrameThread> _frameThreads = [];

    private BookStyle _style = BookStyle.Default;
    private DocumentAst _document = new([]);
    private SectionFormatting _section = new();
    private int _columnIndex;
    private double _cursorY;
    private int _nextDisplayPageNumber = 1;
    private int _fragmentSerial;

    public PagedLayoutEngine(PagedLayoutOptions? options = null)
        => _options = options ?? new PagedLayoutOptions();

    public PagedLayoutResult Paginate(DocumentAst document, BookStyle style)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(style);

        Reset(document, style);
        PreScan(document);

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
                LayoutTextBlock(block, index, PageLayoutFragmentKind.TextFrame, frame);
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
                    LayoutTextBlock(block, index, KindFor(block), null);
                    break;
            }
        }

        BuildFrameThreads();
        var pages = _pages.Select(static page => page.Build()).ToArray();
        return new PagedLayoutResult(pages, _frameThreads.ToArray(), _warnings.ToArray());
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
        _frameSourceLines.Clear();
        _usedFrameIds.Clear();
        _frameThreads.Clear();
        _columnIndex = 0;
        _cursorY = 0;
        _nextDisplayPageNumber = 1;
        _fragmentSerial = 0;
    }

    private void PreScan(DocumentAst document)
    {
        foreach (var block in document.Blocks)
        {
            if (block is FootnoteDefinitionBlock footnote)
                _footnotes[footnote.Identifier] = footnote;

            if (block.Formatting?.TextFrame is not { } frame) continue;
            if (!_frames.TryAdd(frame.Id, frame))
                _warnings.Add(new PageLayoutWarning("duplicate-frame", $"Text frame '{frame.Id}' is declared more than once.", block.SourceLine));

            if (!_frameSourceLines.TryGetValue(frame.Id, out var lines))
            {
                lines = [];
                _frameSourceLines[frame.Id] = lines;
            }
            lines.Add(block.SourceLine);
        }
    }

    private void ApplySection(SectionFormatting next)
    {
        var geometryChanges = next.Columns != _section.Columns ||
                              Math.Abs(next.ColumnGapPoints - _section.ColumnGapPoints) > .01 ||
                              !string.Equals(next.PageStyleId ?? _style.DefaultPageStyleId,
                                  _section.PageStyleId ?? _style.DefaultPageStyleId,
                                  StringComparison.OrdinalIgnoreCase) ||
                              next.FacingPages != _section.FacingPages;

        var needsBoundary = next.Start != SectionStartMode.Continuous ||
                            geometryChanges ||
                            next.PageNumberStart is not null;

        _section = next;
        if (next.PageNumberStart is { } restart)
            _nextDisplayPageNumber = Math.Max(1, restart);

        if (_pages.Count == 0) return;
        if (!needsBoundary) return;

        if (next.Start == SectionStartMode.Continuous && !geometryChanges && next.PageNumberStart is null)
            return;

        StartNewPage();

        if (next.Start is SectionStartMode.NextOddPage or SectionStartMode.NextEvenPage)
        {
            var desiredOdd = next.Start == SectionStartMode.NextOddPage;
            while (((CurrentPage.PhysicalNumber & 1) == 1) != desiredOdd)
            {
                CurrentPage.IsBlank = true;
                StartNewPage();
            }
        }
    }

    private void EnsurePage()
    {
        if (_pages.Count == 0) StartNewPage();
    }

    private void StartNewPage()
    {
        var physical = _pages.Count + 1;
        var geometry = ResolveGeometry(_section, physical);
        var page = new PageBuilder(
            index: _pages.Count,
            physicalNumber: physical,
            displayNumber: _nextDisplayPageNumber,
            numberStyle: _section.PageNumberStyle,
            facingPages: geometry.FacingPages,
            geometry: geometry,
            section: _section);

        _pages.Add(page);
        _nextDisplayPageNumber++;
        _columnIndex = 0;
        _cursorY = page.Columns[0].Bounds.YPoints;
    }

    private PageBuilder CurrentPage => _pages[^1];
    private ColumnBuilder CurrentColumn => CurrentPage.Columns[_columnIndex];

    private void AdvanceColumn()
    {
        if (_columnIndex + 1 < CurrentPage.Columns.Count)
        {
            _columnIndex++;
            _cursorY = CurrentColumn.Bounds.YPoints;
            return;
        }

        StartNewPage();
    }

    private double BodyBottom => CurrentPage.BodyBottomPoints;
    private double AvailableHeight => Math.Max(0, BodyBottom - _cursorY);

    private void ReserveFootnotes(AstBlock block)
    {
        var ids = FootnoteIds(block).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return;

        foreach (var id in ids)
        {
            if (!_footnotes.TryGetValue(id, out var definition))
            {
                _warnings.Add(new PageLayoutWarning("missing-footnote", $"Footnote '{id}' has no definition.", block.SourceLine));
                continue;
            }

            if (CurrentPage.HasFootnote(id)) continue;
            var text = definition.Inlines.ToPlainText();
            var metrics = MeasureText(text, CurrentColumn.Bounds.WidthPoints, _style.FootnoteFontSizePoints, 1.05);
            var height = metrics.LineHeightPoints * metrics.Lines.Count + _options.FootnoteGapPoints;

            if (AvailableHeight < height + _options.MinimumLineHeightPoints && CurrentColumn.Fragments.Count > 0)
                AdvanceColumn();

            CurrentPage.ReserveFootnote(id, definition.SourceLine, text, height, _style.FootnoteFontSizePoints);
        }
    }

    private void LayoutTextBlock(
        AstBlock block,
        int blockIndex,
        PageLayoutFragmentKind kind,
        TextFrameFormatting? frame)
    {
        var text = TextFor(block);
        var paragraph = block.Formatting?.Paragraph;
        var metrics = MeasureBlock(block, text, CurrentColumn.Bounds.WidthPoints);
        var keepWithNext = ResolveKeepWithNext(paragraph, block);
        var keepTogether = ResolveKeepLinesTogether(paragraph, block) || kind == PageLayoutFragmentKind.Heading;

        if (keepWithNext && blockIndex + 1 < _document.Blocks.Count)
        {
            var next = _document.Blocks.Skip(blockIndex + 1)
                .FirstOrDefault(static candidate => candidate is not FootnoteDefinitionBlock and not BibliographyEntryBlock);
            if (next is not null)
            {
                var nextHeight = EstimateBlockHeight(next, CurrentColumn.Bounds.WidthPoints);
                var pairHeight = metrics.TotalHeightPoints + nextHeight;
                if (pairHeight <= CurrentColumn.Bounds.HeightPoints && pairHeight > AvailableHeight)
                    AdvanceColumn();
            }
        }

        if (keepTogether && metrics.TotalHeightPoints <= CurrentColumn.Bounds.HeightPoints && metrics.TotalHeightPoints > AvailableHeight)
            AdvanceColumn();

        var lines = metrics.Lines;
        var lineOffset = 0;
        var fragmentIndex = 0;
        var frameId = frame?.Id;
        var frameChain = frame is null ? Array.Empty<string>() : FollowFrameChain(frame.Id).ToArray();
        var frameSlot = 0;

        while (lineOffset < lines.Count)
        {
            EnsurePage();
            var before = fragmentIndex == 0 ? metrics.SpaceBeforePoints : 0;
            var available = AvailableHeight - before;
            if (available < metrics.LineHeightPoints)
            {
                AdvanceColumn();
                continue;
            }

            var fit = Math.Max(1, (int)Math.Floor(available / metrics.LineHeightPoints));
            var remaining = lines.Count - lineOffset;
            fit = Math.Min(fit, remaining);

            if (fit < remaining)
            {
                if (keepTogether && metrics.TotalHeightPoints > CurrentColumn.Bounds.HeightPoints)
                    _warnings.Add(new PageLayoutWarning("forced-split", "A keep-lines-together block is taller than a full column and had to split.", block.SourceLine));

                var firstMinimum = ResolveKeepFirstLines(paragraph);
                var lastMinimum = ResolveKeepLastLines(paragraph);
                if (lineOffset == 0 && fit < firstMinimum)
                {
                    AdvanceColumn();
                    continue;
                }

                var leftover = remaining - fit;
                if (leftover < lastMinimum)
                {
                    var adjusted = remaining - lastMinimum;
                    if (adjusted >= (lineOffset == 0 ? firstMinimum : 1)) fit = adjusted;
                    else
                    {
                        AdvanceColumn();
                        continue;
                    }
                }
            }

            if (frame is not null)
            {
                if (frameSlot >= frameChain.Length)
                {
                    AddOverset(block, blockIndex, frameId ?? frame.Id, lines.Skip(lineOffset));
                    return;
                }
                frameId = frameChain[frameSlot];
                _usedFrameIds.Add(frameId);
            }

            var isLast = lineOffset + fit >= lines.Count;
            var after = isLast ? metrics.SpaceAfterPoints : 0;
            var height = before + fit * metrics.LineHeightPoints + after;
            var bounds = new PageLayoutRect(
                CurrentColumn.Bounds.XPoints,
                _cursorY,
                CurrentColumn.Bounds.WidthPoints,
                height);
            var fragmentText = string.Join('\n', lines.Skip(lineOffset).Take(fit));
            CurrentColumn.Fragments.Add(new PageLayoutFragment(
                NextFragmentId(),
                kind,
                block.SourceLine,
                blockIndex,
                bounds,
                fragmentText,
                fragmentIndex,
                fragmentIndex > 0,
                FrameId: frameId,
                FrameColumns: Math.Max(1, frame?.Columns ?? 1)));
            _cursorY += height;
            lineOffset += fit;
            fragmentIndex++;

            if (lineOffset < lines.Count)
            {
                if (frame is not null) frameSlot++;
                AdvanceColumn();
            }
        }
    }

    private void AddOverset(AstBlock block, int blockIndex, string frameId, IEnumerable<string> remainingLines)
    {
        var markerHeight = Math.Min(_options.OversetIndicatorHeightPoints, Math.Max(4, AvailableHeight));
        var y = Math.Min(_cursorY, Math.Max(CurrentColumn.Bounds.YPoints, BodyBottom - markerHeight));
        var bounds = new PageLayoutRect(
            CurrentColumn.Bounds.RightPoints - markerHeight,
            y,
            markerHeight,
            markerHeight);
        CurrentColumn.Fragments.Add(new PageLayoutFragment(
            NextFragmentId(),
            PageLayoutFragmentKind.OversetIndicator,
            block.SourceLine,
            blockIndex,
            bounds,
            "+",
            IsContinuation: true,
            FrameId: frameId));
        _warnings.Add(new PageLayoutWarning(
            "overset",
            $"Text frame '{frameId}' has overset text ({remainingLines.Sum(static line => line.Length)} characters remain).",
            block.SourceLine));
    }

    private void LayoutRule(AstBlock block, int blockIndex)
    {
        const double height = 12;
        if (AvailableHeight < height) AdvanceColumn();
        var bounds = new PageLayoutRect(CurrentColumn.Bounds.XPoints, _cursorY, CurrentColumn.Bounds.WidthPoints, height);
        CurrentColumn.Fragments.Add(new PageLayoutFragment(
            NextFragmentId(), PageLayoutFragmentKind.ThematicBreak, block.SourceLine, blockIndex, bounds, "—"));
        _cursorY += height;
    }

    private void LayoutFigure(FigureBlock figure, int blockIndex)
    {
        var anchored = figure.Formatting?.AnchoredObject;
        var figureStyle = _style.NamedStyles.ResolveFigure(figure.StyleId ?? _style.DefaultFigureStyleId);
        var widthPercent = figure.Layout?.WidthPercent ?? figureStyle?.MaxWidthPercent ?? 90;
        widthPercent = Math.Clamp(widthPercent, 10, 100);
        var width = CurrentColumn.Bounds.WidthPoints * widthPercent / 100d;
        var height = figure.Layout?.HeightInches is { } inches
            ? Math.Max(_options.MinimumFigureHeightPoints, inches * 72d)
            : Math.Max(_options.MinimumFigureHeightPoints, width * _options.DefaultFigureAspectRatio);
        if (!string.IsNullOrWhiteSpace(figure.Caption)) height += _style.CaptionFontSizePoints * 1.3;

        var placement = anchored?.Placement ?? ToFloatPlacement(figure.Layout?.Placement ?? figureStyle?.Placement);
        var wrap = anchored?.Wrap ?? TextWrapMode.None;
        var x = CurrentColumn.Bounds.XPoints + (CurrentColumn.Bounds.WidthPoints - width) / 2d;
        var y = _cursorY;

        if (placement == FloatPlacementMode.Page)
        {
            if (CurrentColumn.Fragments.Count > 0) AdvanceColumn();
            x = CurrentColumn.Bounds.XPoints + (CurrentColumn.Bounds.WidthPoints - width) / 2d;
            y = CurrentColumn.Bounds.YPoints + Math.Max(0, (CurrentColumn.Bounds.HeightPoints - height) / 2d);
        }
        else if (placement == FloatPlacementMode.Top)
        {
            if (CurrentColumn.Fragments.Count > 0) AdvanceColumn();
            y = CurrentColumn.Bounds.YPoints;
        }
        else if (placement == FloatPlacementMode.Bottom)
        {
            if (AvailableHeight < height && CurrentColumn.Fragments.Count > 0) AdvanceColumn();
            y = Math.Max(_cursorY, BodyBottom - height);
        }
        else if (placement == FloatPlacementMode.Margin)
        {
            var marginWidth = Math.Max(54, (CurrentPage.Geometry.WidthPoints - CurrentPage.Geometry.ContentBounds.WidthPoints) * .8);
            var onLeft = CurrentPage.IsLeftPage;
            x = onLeft
                ? Math.Max(0, CurrentPage.Geometry.ContentBounds.XPoints - marginWidth - 6)
                : Math.Min(CurrentPage.Geometry.WidthPoints - marginWidth, CurrentPage.Geometry.ContentBounds.RightPoints + 6);
            width = marginWidth;
            height = Math.Min(height, CurrentPage.Geometry.ContentBounds.HeightPoints / 2d);
        }
        else if (AvailableHeight < height && CurrentColumn.Fragments.Count > 0)
        {
            AdvanceColumn();
            x = CurrentColumn.Bounds.XPoints + (CurrentColumn.Bounds.WidthPoints - width) / 2d;
            y = _cursorY;
        }

        if (height > CurrentColumn.Bounds.HeightPoints)
        {
            height = CurrentColumn.Bounds.HeightPoints;
            _warnings.Add(new PageLayoutWarning("figure-clipped", "Figure height exceeded the page body and was clipped in page layout.", figure.SourceLine));
        }

        var bounds = new PageLayoutRect(
            x + (anchored?.OffsetXPoints ?? 0),
            y + (anchored?.OffsetYPoints ?? 0),
            width,
            height);
        var kind = placement == FloatPlacementMode.Margin
            ? PageLayoutFragmentKind.MarginNote
            : placement == FloatPlacementMode.Inline
                ? PageLayoutFragmentKind.Figure
                : PageLayoutFragmentKind.FloatingObject;
        var fragment = new PageLayoutFragment(
            NextFragmentId(),
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

        if (placement is FloatPlacementMode.Inline or FloatPlacementMode.Here or FloatPlacementMode.Top or FloatPlacementMode.Bottom or FloatPlacementMode.Page)
            _cursorY = Math.Max(_cursorY, bounds.BottomPoints + (wrap == TextWrapMode.None ? 0 : Math.Max(anchored?.WrapBottomPoints ?? 0, 4)));
    }

    private void LayoutTable(TableBlock table, int blockIndex)
    {
        var tableStyle = _style.NamedStyles.ResolveTable(table.StyleId ?? _style.DefaultTableStyleId);
        var keepTable = table.Properties?.KeepTogether ?? tableStyle?.KeepTogether ?? false;
        var headerRows = BuildHeaderRows(table);
        var bodyRows = BuildBodyRows(table, headerRows.BodyHeaderCount);
        var totalHeight = headerRows.Rows.Sum(row => MeasureTableRow(row.Cells, CurrentColumn.Bounds.WidthPoints, row.Properties).HeightPoints) +
                          bodyRows.Sum(row => MeasureTableRow(row.Cells, CurrentColumn.Bounds.WidthPoints, row.Properties).HeightPoints) +
                          (string.IsNullOrWhiteSpace(table.Caption) ? 0 : _style.CaptionFontSizePoints * 1.4);

        if (keepTable && totalHeight <= CurrentColumn.Bounds.HeightPoints && totalHeight > AvailableHeight)
            AdvanceColumn();

        if (!string.IsNullOrWhiteSpace(table.Caption))
        {
            var height = _style.CaptionFontSizePoints * 1.4;
            if (AvailableHeight < height) AdvanceColumn();
            var captionBounds = new PageLayoutRect(CurrentColumn.Bounds.XPoints, _cursorY, CurrentColumn.Bounds.WidthPoints, height);
            CurrentColumn.Fragments.Add(new PageLayoutFragment(
                NextFragmentId(), PageLayoutFragmentKind.TableCaption, table.SourceLine, blockIndex, captionBounds, table.Caption!));
            _cursorY += height;
        }

        var headerRenderedOnColumn = false;
        foreach (var row in bodyRows.PrependRange(headerRows.Rows))
        {
            var isHeader = row.IsHeader;
            if (!isHeader && !headerRenderedOnColumn)
            {
                RenderRepeatedHeaders(table, blockIndex, headerRows.Rows);
                headerRenderedOnColumn = true;
            }

            var rowMetrics = MeasureTableRow(row.Cells, CurrentColumn.Bounds.WidthPoints, row.Properties);
            if (rowMetrics.HeightPoints > AvailableHeight)
            {
                AdvanceColumn();
                headerRenderedOnColumn = false;
                if (!isHeader)
                {
                    RenderRepeatedHeaders(table, blockIndex, headerRows.Rows);
                    headerRenderedOnColumn = true;
                }
            }

            if (rowMetrics.HeightPoints > CurrentColumn.Bounds.HeightPoints)
            {
                _warnings.Add(new PageLayoutWarning(
                    "table-row-overset",
                    $"Table row {row.RowIndex + 1} is taller than a full column.",
                    table.SourceLine));
            }

            var rowHeight = Math.Min(rowMetrics.HeightPoints, CurrentColumn.Bounds.HeightPoints);
            var bounds = new PageLayoutRect(CurrentColumn.Bounds.XPoints, _cursorY, CurrentColumn.Bounds.WidthPoints, rowHeight);
            CurrentColumn.Fragments.Add(new PageLayoutFragment(
                NextFragmentId(),
                isHeader ? PageLayoutFragmentKind.TableHeader : PageLayoutFragmentKind.TableRow,
                table.SourceLine,
                blockIndex,
                bounds,
                rowMetrics.Text,
                IsRepeatedHeader: false,
                TableRowIndex: row.RowIndex));
            _cursorY += rowHeight;
            if (isHeader) headerRenderedOnColumn = true;
        }
    }

    private void RenderRepeatedHeaders(TableBlock table, int blockIndex, IReadOnlyList<TableRowLayout> headers)
    {
        if (headers.Count == 0) return;
        if (CurrentColumn.Fragments.Count == 0 && Math.Abs(_cursorY - CurrentColumn.Bounds.YPoints) < .01)
        {
            foreach (var header in headers)
            {
                var metrics = MeasureTableRow(header.Cells, CurrentColumn.Bounds.WidthPoints, header.Properties);
                if (metrics.HeightPoints > AvailableHeight) break;
                var bounds = new PageLayoutRect(CurrentColumn.Bounds.XPoints, _cursorY, CurrentColumn.Bounds.WidthPoints, metrics.HeightPoints);
                CurrentColumn.Fragments.Add(new PageLayoutFragment(
                    NextFragmentId(),
                    PageLayoutFragmentKind.TableHeader,
                    table.SourceLine,
                    blockIndex,
                    bounds,
                    metrics.Text,
                    IsRepeatedHeader: true,
                    TableRowIndex: header.RowIndex));
                _cursorY += metrics.HeightPoints;
            }
        }
    }

    private (IReadOnlyList<TableRowLayout> Rows, int BodyHeaderCount) BuildHeaderRows(TableBlock table)
    {
        var rows = new List<TableRowLayout>
        {
            new(-1, table.Header, new TableRowProperties(RepeatAsHeader: true), true)
        };
        var bodyHeaderCount = Math.Max(0, table.RepeatHeaderRows - 1);
        for (var index = 0; index < table.Rows.Count; index++)
        {
            var properties = GetRowProperties(table, index);
            if (index < bodyHeaderCount || properties.RepeatAsHeader)
                rows.Add(new TableRowLayout(index, table.Rows[index], properties, true));
        }
        return (rows, bodyHeaderCount);
    }

    private IReadOnlyList<TableRowLayout> BuildBodyRows(TableBlock table, int bodyHeaderCount)
    {
        var rows = new List<TableRowLayout>();
        for (var index = 0; index < table.Rows.Count; index++)
        {
            var properties = GetRowProperties(table, index);
            if (index < bodyHeaderCount || properties.RepeatAsHeader) continue;
            rows.Add(new TableRowLayout(index, table.Rows[index], properties, false));
        }
        return rows;
    }

    private static TableRowProperties GetRowProperties(TableBlock table, int index)
        => table.RowProperties is { Count: > 0 } && index < table.RowProperties.Count
            ? table.RowProperties[index]
            : new TableRowProperties();

    private TableRowMetrics MeasureTableRow(
        IReadOnlyList<TableCell> cells,
        double widthPoints,
        TableRowProperties properties)
    {
        var count = Math.Max(1, cells.Count);
        var cellWidth = widthPoints / count;
        var maxLines = 1;
        var values = new List<string>(cells.Count);
        foreach (var cell in cells)
        {
            var text = cell.Inlines.ToPlainText();
            values.Add(text);
            var metrics = MeasureText(text, Math.Max(12, cellWidth - 8), _style.BodyFontSizePoints, _style.LineSpacing);
            maxLines = Math.Max(maxLines, metrics.Lines.Count);
        }

        var height = maxLines * Math.Max(_options.MinimumLineHeightPoints, _style.BodyFontSizePoints * _style.LineSpacing) + 8;
        if (properties.MinimumHeightPoints is { } minimum) height = Math.Max(height, minimum);
        return new TableRowMetrics(height, string.Join(" | ", values));
    }

    private BlockMetrics MeasureBlock(AstBlock block, string text, double widthPoints)
    {
        var paragraph = block.Formatting?.Paragraph;
        var explicitStyle = !string.IsNullOrWhiteSpace(paragraph?.StyleId)
            ? _style.NamedStyles.ResolveParagraph(paragraph!.StyleId)
            : block is HeadingBlock
                ? null
                : _style.NamedStyles.ResolveParagraph(_style.DefaultParagraphStyleId);

        var fontSize = paragraph?.CharacterDefaults?.FontSizePoints ?? explicitStyle?.FontSizePoints ?? FontSizeFor(block);
        var lineSpacing = paragraph?.LineSpacing ?? explicitStyle?.LineSpacing ?? _style.LineSpacing;
        var before = paragraph?.SpaceBeforePoints ?? explicitStyle?.SpaceBeforePoints ?? SpaceBeforeFor(block);
        var after = paragraph?.SpaceAfterPoints ?? explicitStyle?.SpaceAfterPoints ?? SpaceAfterFor(block);
        var textMetrics = MeasureText(text, widthPoints, fontSize, lineSpacing);
        if (paragraph?.Rules is { Count: > 0 })
        {
            before += paragraph.Rules.Count(rule => rule.Position == ParagraphRulePosition.Above) * 2;
            after += paragraph.Rules.Count(rule => rule.Position == ParagraphRulePosition.Below) * 2;
        }
        return new BlockMetrics(textMetrics.Lines, textMetrics.LineHeightPoints, before, after);
    }

    private double EstimateBlockHeight(AstBlock block, double widthPoints)
    {
        if (block is TableBlock table)
        {
            var headers = BuildHeaderRows(table);
            var rows = BuildBodyRows(table, headers.BodyHeaderCount);
            return headers.Rows.Sum(row => MeasureTableRow(row.Cells, widthPoints, row.Properties).HeightPoints) +
                   rows.Sum(row => MeasureTableRow(row.Cells, widthPoints, row.Properties).HeightPoints);
        }
        if (block is FigureBlock figure)
        {
            var width = widthPoints * Math.Clamp(figure.Layout?.WidthPercent ?? 90, 10, 100) / 100d;
            return figure.Layout?.HeightInches is { } inches
                ? inches * 72d
                : Math.Max(_options.MinimumFigureHeightPoints, width * _options.DefaultFigureAspectRatio);
        }
        if (block is ThematicBreakBlock) return 12;
        return MeasureBlock(block, TextFor(block), widthPoints).TotalHeightPoints;
    }

    private TextMetrics MeasureText(string text, double widthPoints, double fontSizePoints, double lineSpacing)
    {
        var glyph = Math.Max(2.5, fontSizePoints * _options.AverageGlyphWidthFactor);
        var capacity = Math.Max(4, (int)Math.Floor(widthPoints / glyph));
        var lines = WrapText(text, capacity);
        var lineHeight = Math.Max(_options.MinimumLineHeightPoints, fontSizePoints * Math.Max(.8, lineSpacing));
        return new TextMetrics(lines, lineHeight);
    }

    private static IReadOnlyList<string> WrapText(string text, int capacity)
    {
        if (string.IsNullOrEmpty(text)) return [string.Empty];
        var lines = new List<string>();
        foreach (var logical in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var current = new StringBuilder();
            var units = 0;
            foreach (var rune in logical.EnumerateRunes())
            {
                var width = RuneWidth(rune);
                if (units > 0 && units + width > capacity)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                    units = 0;
                }
                current.Append(rune.ToString());
                units += width;
            }
            lines.Add(current.ToString());
        }
        return lines.Count == 0 ? [string.Empty] : lines;
    }

    private static int RuneWidth(Rune rune)
    {
        if (rune.Value == '\t') return 4;
        if (rune.Value is >= 0x1100 and <= 0x11FF or >= 0x2E80 and <= 0xA4CF or >= 0x1F000)
            return 2;
        return 1;
    }

    private double FontSizeFor(AstBlock block)
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

    private double SpaceBeforeFor(AstBlock block)
        => block switch
        {
            HeadingBlock { Level: 1 } => _style.ChapterBeforeSpacingPoints,
            HeadingBlock => _style.SectionBeforeSpacingPoints,
            _ => 0
        };

    private double SpaceAfterFor(AstBlock block)
        => block switch
        {
            HeadingBlock { Level: 1 } => _style.ChapterAfterSpacingPoints,
            HeadingBlock => _style.SectionAfterSpacingPoints,
            ParagraphBlock => _style.ParagraphSpacingPoints,
            _ => 0
        };

    private bool ResolveKeepWithNext(ParagraphFormatting? paragraph, AstBlock block)
    {
        if (paragraph?.KeepWithNext is { } direct) return direct;
        var named = !string.IsNullOrWhiteSpace(paragraph?.StyleId)
            ? _style.NamedStyles.ResolveParagraph(paragraph!.StyleId)
            : null;
        if (named?.KeepWithNext is { } styleValue) return styleValue;
        return block is HeadingBlock;
    }

    private bool ResolveKeepLinesTogether(ParagraphFormatting? paragraph, AstBlock block)
    {
        if (paragraph?.KeepLinesTogether is { } direct) return direct;
        var named = !string.IsNullOrWhiteSpace(paragraph?.StyleId)
            ? _style.NamedStyles.ResolveParagraph(paragraph!.StyleId)
            : null;
        if (named?.KeepLinesTogether is { } styleValue) return styleValue;
        return block is HeadingBlock;
    }

    private int ResolveKeepFirstLines(ParagraphFormatting? paragraph)
    {
        if (paragraph?.KeepFirstLines is { } direct) return Math.Max(1, direct);
        var named = !string.IsNullOrWhiteSpace(paragraph?.StyleId)
            ? _style.NamedStyles.ResolveParagraph(paragraph!.StyleId)
            : null;
        if (named?.KeepFirstLines is { } styleValue) return Math.Max(1, styleValue);
        return _style.AvoidWidowsAndOrphans ? Math.Max(1, _options.DefaultOrphanLines) : 1;
    }

    private int ResolveKeepLastLines(ParagraphFormatting? paragraph)
    {
        if (paragraph?.KeepLastLines is { } direct) return Math.Max(1, direct);
        var named = !string.IsNullOrWhiteSpace(paragraph?.StyleId)
            ? _style.NamedStyles.ResolveParagraph(paragraph!.StyleId)
            : null;
        if (named?.KeepLastLines is { } styleValue) return Math.Max(1, styleValue);
        return _style.AvoidWidowsAndOrphans ? Math.Max(1, _options.DefaultWidowLines) : 1;
    }

    private void BuildFrameThreads()
    {
        var referenced = _frames.Values
            .Select(static frame => frame.NextFrameId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        var roots = _frames.Keys.Where(id => !referenced.Contains(id)).OrderBy(static id => id, StringComparer.Ordinal).ToList();
        foreach (var id in _frames.Keys.OrderBy(static id => id, StringComparer.Ordinal))
            if (!roots.Contains(id, StringComparer.Ordinal)) roots.Add(id);

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
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
                    _warnings.Add(new PageLayoutWarning("frame-cycle", $"Text frame thread contains a cycle at '{current}'."));
                    overset = true;
                    break;
                }
                emitted.Add(current);
                ids.Add(current);
                if (_frameSourceLines.TryGetValue(current, out var sourceLines)) lines.AddRange(sourceLines);
                current = frame.NextFrameId ?? string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(current) && !_frames.ContainsKey(current))
            {
                _warnings.Add(new PageLayoutWarning("missing-frame", $"Text frame thread references missing frame '{current}'."));
                overset = true;
            }

            overset |= _warnings.Any(warning => warning.Code == "overset" &&
                                                lines.Count > 0 &&
                                                warning.SourceLine is { } line && lines.Contains(line));
            _frameThreads.Add(new PageLayoutFrameThread(root, ids, lines.Order().ToArray(), overset));
        }
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

    private ResolvedPageGeometry ResolveGeometry(SectionFormatting section, int physicalNumber)
    {
        var pageStyleId = section.PageStyleId ?? _style.DefaultPageStyleId;
        var pageStyle = _style.NamedStyles.ResolvePage(pageStyleId);
        var width = (pageStyle?.WidthInches ?? _style.PageWidthInches) * 72d;
        var height = (pageStyle?.HeightInches ?? _style.PageHeightInches) * 72d;
        if (pageStyle?.Landscape == true) (width, height) = (height, width);

        var facing = section.FacingPages || pageStyle?.FacingPages == true;
        var isLeft = facing && physicalNumber % 2 == 0;
        var inner = (pageStyle?.MarginInnerInches ?? _style.MarginInnerInches) * 72d;
        var outer = (pageStyle?.MarginOuterInches ?? _style.MarginOuterInches) * 72d;
        var left = facing ? (isLeft ? outer : inner) : inner;
        var right = facing ? (isLeft ? inner : outer) : outer;
        var top = (pageStyle?.MarginTopInches ?? _style.MarginTopInches) * 72d;
        var bottom = (pageStyle?.MarginBottomInches ?? _style.MarginBottomInches) * 72d;
        var content = new PageLayoutRect(
            left,
            top,
            Math.Max(36, width - left - right),
            Math.Max(36, height - top - bottom));

        var bleedTop = section.BleedTopPoints > 0 ? section.BleedTopPoints : (pageStyle?.BleedTopInches ?? 0) * 72d;
        var bleedBottom = section.BleedBottomPoints > 0 ? section.BleedBottomPoints : (pageStyle?.BleedBottomInches ?? 0) * 72d;
        var bleedInside = section.BleedInsidePoints > 0 ? section.BleedInsidePoints : (pageStyle?.BleedInsideInches ?? 0) * 72d;
        var bleedOutside = section.BleedOutsidePoints > 0 ? section.BleedOutsidePoints : (pageStyle?.BleedOutsideInches ?? 0) * 72d;
        var slug = section.SlugPoints > 0 ? section.SlugPoints : (pageStyle?.SlugInches ?? 0) * 72d;

        return new ResolvedPageGeometry(
            width,
            height,
            content,
            facing,
            isLeft,
            pageStyleId,
            section.BaselineGrid ?? pageStyle?.BaselineGrid,
            section.CropMarks || pageStyle?.CropMarks == true,
            bleedTop,
            bleedBottom,
            bleedInside,
            bleedOutside,
            slug);
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

    private static string ToRoman(int number)
    {
        var values = new (int Value, string Token)[]
        {
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
            (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
            (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
        };
        var writer = new StringBuilder();
        foreach (var (value, token) in values)
        {
            while (number >= value)
            {
                writer.Append(token);
                number -= value;
            }
        }
        return writer.ToString();
    }

    private static string ToLetters(int number)
    {
        var writer = new StringBuilder();
        while (number > 0)
        {
            number--;
            writer.Insert(0, (char)('A' + number % 26));
            number /= 26;
        }
        return writer.ToString();
    }

    private string NextFragmentId() => $"layout-{_fragmentSerial++:D6}";

    private sealed record TextMetrics(IReadOnlyList<string> Lines, double LineHeightPoints);

    private sealed record BlockMetrics(
        IReadOnlyList<string> Lines,
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
        private readonly SectionFormatting _section;

        public PageBuilder(
            int index,
            int physicalNumber,
            int displayNumber,
            PageNumberStyle numberStyle,
            bool facingPages,
            ResolvedPageGeometry geometry,
            SectionFormatting section)
        {
            Index = index;
            PhysicalNumber = physicalNumber;
            DisplayNumber = displayNumber;
            NumberStyle = numberStyle;
            FacingPages = facingPages;
            Geometry = geometry;
            _section = section;

            var count = Math.Max(1, section.Columns);
            var gap = Math.Max(0, section.ColumnGapPoints);
            var width = Math.Max(12, (geometry.ContentBounds.WidthPoints - gap * (count - 1)) / count);
            var columns = new List<ColumnBuilder>(count);
            for (var column = 0; column < count; column++)
            {
                var x = geometry.ContentBounds.XPoints + column * (width + gap);
                columns.Add(new ColumnBuilder(column, new PageLayoutRect(
                    x,
                    geometry.ContentBounds.YPoints,
                    width,
                    geometry.ContentBounds.HeightPoints)));
            }
            Columns = columns;
        }

        public int Index { get; }
        public int PhysicalNumber { get; }
        public int DisplayNumber { get; }
        public PageNumberStyle NumberStyle { get; }
        public bool FacingPages { get; }
        public bool IsLeftPage => Geometry.IsLeftPage;
        public bool IsBlank { get; set; }
        public ResolvedPageGeometry Geometry { get; }
        public List<ColumnBuilder> Columns { get; }
        public List<PageLayoutFragment> FloatingObjects { get; } = [];
        public double ReservedFootnoteHeightPoints { get; private set; }
        public double BodyBottomPoints => Geometry.ContentBounds.BottomPoints - ReservedFootnoteHeightPoints;

        public bool HasFootnote(string id) => _footnotes.ContainsKey(id);

        public void ReserveFootnote(string id, int sourceLine, string text, double heightPoints, double fontSizePoints)
        {
            if (_footnotes.ContainsKey(id)) return;
            _footnotes[id] = new FootnoteReservation(id, sourceLine, text, heightPoints, fontSizePoints);
            ReservedFootnoteHeightPoints += heightPoints;
        }

        public PageLayoutPage Build()
        {
            var footnoteFragments = new List<PageLayoutFragment>(_footnotes.Count);
            var y = Geometry.ContentBounds.BottomPoints - ReservedFootnoteHeightPoints;
            var serial = 0;
            foreach (var reservation in _footnotes.Values)
            {
                var bounds = new PageLayoutRect(
                    Geometry.ContentBounds.XPoints,
                    y,
                    Geometry.ContentBounds.WidthPoints,
                    reservation.HeightPoints);
                footnoteFragments.Add(new PageLayoutFragment(
                    $"footnote-{Index:D4}-{serial++:D3}",
                    PageLayoutFragmentKind.Footnote,
                    reservation.SourceLine,
                    -1,
                    bounds,
                    reservation.Text));
                y += reservation.HeightPoints;
            }

            var guides = BuildGuides();
            return new PageLayoutPage(
                Index,
                PhysicalNumber,
                DisplayNumber,
                FormatPageNumber(DisplayNumber, NumberStyle),
                Geometry.IsLeftPage,
                IsBlank,
                FacingPages,
                Geometry.WidthPoints,
                Geometry.HeightPoints,
                Geometry.ContentBounds,
                Columns.Select(static column => column.Build()).ToArray(),
                FloatingObjects.ToArray(),
                footnoteFragments,
                guides,
                Geometry.BaselineGrid,
                Geometry.PageStyleId,
                Geometry.CropMarks,
                Geometry.BleedTopPoints,
                Geometry.BleedBottomPoints,
                Geometry.BleedInsidePoints,
                Geometry.BleedOutsidePoints,
                Geometry.SlugPoints);
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

        private sealed record FootnoteReservation(
            string Id,
            int SourceLine,
            string Text,
            double HeightPoints,
            double FontSizePoints);
    }
}

internal static class PagedLayoutEnumerableExtensions
{
    public static IEnumerable<T> PrependRange<T>(this IEnumerable<T> tail, IEnumerable<T> head)
    {
        foreach (var item in head) yield return item;
        foreach (var item in tail) yield return item;
    }
}
