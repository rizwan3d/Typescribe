using System.Text;
using Typescribe.Application.Services;
using Typescribe.Domain.Models;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static TextInline Text(string value) => new(value);

static string Fingerprint(PagedLayoutResult result)
{
    var output = new StringBuilder();
    foreach (var page in result.Pages)
    {
        output.Append(page.PhysicalNumber).Append(':')
            .Append(page.DisplayNumberText).Append(':')
            .Append(page.IsBlank).Append(':')
            .Append(page.WidthPoints).Append('x').Append(page.HeightPoints).Append('|');
        foreach (var column in page.Columns)
        {
            output.Append('C').Append(column.Index).Append('[');
            foreach (var fragment in column.Fragments)
            {
                output.Append(fragment.Kind).Append('@').Append(fragment.SourceLine).Append('/')
                    .Append(fragment.FragmentIndex).Append('/')
                    .Append(fragment.IsRepeatedHeader).Append('/')
                    .Append(fragment.Bounds.XPoints).Append(',')
                    .Append(fragment.Bounds.YPoints).Append(',')
                    .Append(fragment.Bounds.WidthPoints).Append(',')
                    .Append(fragment.Bounds.HeightPoints).Append(';');
            }
            output.Append(']');
        }
        foreach (var floating in page.FloatingObjects)
            output.Append("F:").Append(floating.Kind).Append('@').Append(floating.SourceLine).Append(';');
        foreach (var footnote in page.Footnotes)
            output.Append("N:").Append(footnote.SourceLine).Append(';');
    }
    foreach (var warning in result.Warnings)
        output.Append("W:").Append(warning.Code).Append('@').Append(warning.SourceLine).Append(';');
    return output.ToString();
}

var namedStyles = BookStyle.Default.NamedStyles with
{
    PageStyles =
    [
        new PageStyleDefinition(
            "page.smoke",
            "Smoke Page",
            null,
            WidthInches: 4.25,
            HeightInches: 5.5,
            MarginTopInches: .45,
            MarginBottomInches: .45,
            MarginInnerInches: .5,
            MarginOuterInches: .4,
            FacingPages: true,
            BaselineGrid: new BaselineGridFormatting(true, 12, 0))
    ]
};
var style = BookStyle.Default with
{
    DefaultPageStyleId = "page.smoke",
    NamedStyles = namedStyles,
    BodyFontSizePoints = 10,
    LineSpacing = 1.05,
    FootnoteFontSizePoints = 8,
    AvoidWidowsAndOrphans = true
};

var longText = string.Join(' ', Enumerable.Repeat(
    "Deterministic pagination keeps Markdown canonical while content flows across columns and pages.", 75));
var frameText = string.Join(' ', Enumerable.Repeat(
    "Threaded frame text must flow in logical order and report overset instead of losing content.", 100));

var header = new[]
{
    new TableCell([Text("Item")]),
    new TableCell([Text("Description")])
};
var tableRows = Enumerable.Range(1, 42)
    .Select(index => (IReadOnlyList<TableCell>)
    [
        new TableCell([Text(index.ToString())]),
        new TableCell([Text($"Row {index} contains enough text to exercise deterministic continuation.")])
    ])
    .ToArray();

var blocks = new List<AstBlock>
{
    new HeadingBlock(1, 1, [Text("Paged layout smoke")])
    {
        Formatting = new RichBlockFormatting(
            Paragraph: new ParagraphFormatting(KeepWithNext: true),
            Section: new SectionFormatting(
                Columns: 2,
                ColumnGapPoints: 14,
                Start: SectionStartMode.NextOddPage,
                PageStyleId: "page.smoke",
                PageNumberStyle: PageNumberStyle.LowerRoman,
                PageNumberStart: 3,
                BaselineGrid: new BaselineGridFormatting(true, 12, 0),
                FacingPages: true))
    },
    new ParagraphBlock(2, [Text(longText), new FootnoteReferenceInline("layout-note")])
    {
        Formatting = new RichBlockFormatting(
            Paragraph: new ParagraphFormatting(KeepFirstLines: 2, KeepLastLines: 2))
    },
    new FigureBlock(3, "assets/diagram.png", "Margin figure", "fig:margin")
    {
        Formatting = new RichBlockFormatting(
            AnchoredObject: new AnchoredObjectFormatting(
                "anchor.margin",
                Placement: FloatPlacementMode.Margin,
                Wrap: TextWrapMode.BoundingBox,
                WrapLeftPoints: 6,
                WrapRightPoints: 6))
    },
    new TableBlock(
        4,
        header,
        tableRows,
        Caption: "Continuation table",
        RepeatHeaderRows: 1,
        Properties: new TableProperties(KeepTogether: false, AllowRowBreakAcrossPages: false)),
    new ParagraphBlock(5, [Text(frameText)])
    {
        Formatting = new RichBlockFormatting(
            TextFrame: new TextFrameFormatting("frame.one", NextFrameId: "frame.two", Columns: 1))
    },
    new ParagraphBlock(6, [Text("Second frame declaration")])
    {
        Formatting = new RichBlockFormatting(
            TextFrame: new TextFrameFormatting("frame.two", Columns: 1))
    },
    new HeadingBlock(7, 2, [Text("New numbered section")])
    {
        Formatting = new RichBlockFormatting(
            Section: new SectionFormatting(
                Columns: 1,
                Start: SectionStartMode.NextEvenPage,
                PageStyleId: "page.smoke",
                PageNumberStyle: PageNumberStyle.Arabic,
                PageNumberStart: 10,
                FacingPages: true))
    },
    new ParagraphBlock(8, [Text("Section content after an even-page section start.")]),
    new FootnoteDefinitionBlock(9, "layout-note", [Text("Footnote content is reserved at the page bottom.")])
};

var document = new DocumentAst(blocks);
var engine = new PagedLayoutEngine();
var first = engine.Paginate(document, style);
var second = engine.Paginate(document, style);

Assert(first.Pages.Count >= 4, "Expected the fixture to paginate across several pages.");
Assert(Fingerprint(first) == Fingerprint(second), "Pagination must be deterministic across identical runs.");
Assert(first.Pages.Any(page => page.Columns.Count == 2), "Expected a two-column section.");
Assert(first.Pages.Any(page => page.BaselineGrid?.Enabled == true), "Expected a baseline grid on paged output.");
Assert(first.Pages.Any(page => page.Footnotes.Count > 0), "Expected footnote reservation/layout.");
Assert(first.Pages.SelectMany(page => page.FloatingObjects).Any(item => item.Kind == PageLayoutFragmentKind.MarginNote),
    "Expected the anchored margin figure to be projected as a margin object.");
Assert(first.Pages.SelectMany(page => page.Columns).SelectMany(column => column.Fragments)
        .Any(item => item.Kind == PageLayoutFragmentKind.TableHeader && item.IsRepeatedHeader),
    "Expected table headers to repeat after a page/column continuation.");
Assert(first.FrameThreads.Any(thread => thread.FrameIds.SequenceEqual(new[] { "frame.one", "frame.two" })),
    "Expected linked text frames to form one thread.");
Assert(first.Warnings.Any(warning => warning.Code == "overset"),
    "Expected deliberately long threaded text to produce an overset warning.");
Assert(first.Pages.Any(page => page.DisplayNumberText == "iii"), "Expected lower-Roman page numbering restart at iii.");
Assert(first.Pages.Any(page => page.DisplayNumber == 10 && page.NumberStyle == PageNumberStyle.Arabic),
    "Expected the later section to restart Arabic page numbers at 10.");
Assert(first.Pages.Any(page => page.IsBlank), "Expected odd/even section starts to insert a recoverable blank page when needed.");

var parentEditedPage = namedStyles.PageStyles[0] with
{
    WidthInches = 5,
    HeightInches = 7,
    MarginTopInches = .6,
    MarginBottomInches = .7,
    MarginInnerInches = .8,
    MarginOuterInches = .55
};
var parentEditedCatalog = namedStyles with { PageStyles = [parentEditedPage] };
var parentEditedStyle = style with { NamedStyles = parentEditedCatalog };
var parentEditedLayout = engine.Paginate(
    new DocumentAst(
    [
        new ParagraphBlock(1, [Text("Parent-page geometry remains a deterministic projection.")])
        {
            Formatting = new RichBlockFormatting(
                Section: new SectionFormatting(PageStyleId: "page.smoke", FacingPages: true))
        }
    ]),
    parentEditedStyle);
var parentEdited = parentEditedLayout.Pages.First(page => !page.IsBlank);
Assert(Math.Abs(parentEdited.WidthPoints - 360) < .001 &&
       Math.Abs(parentEdited.HeightPoints - 504) < .001,
    "Editing a named parent page must change projected trim size.");
Assert(Math.Abs(parentEdited.ContentBounds.YPoints - 43.2) < .001 &&
       Math.Abs(parentEdited.ContentBounds.HeightPoints - (504 - 43.2 - 50.4)) < .001,
    "Editing parent-page top/bottom margins must change the projected content box.");
Assert(parentEdited.PageStyleId == "page.smoke",
    "Projected pages must retain the parent page ID used by the spread inspector.");

var continuationFragments = first.Pages
    .SelectMany(page => page.Columns)
    .SelectMany(column => column.Fragments)
    .Where(fragment => fragment.IsContinuation && fragment.SourceTextStart > 0)
    .ToArray();
Assert(continuationFragments.Length > 0,
    "Text continued onto later columns/pages must retain its plain-text source offset.");
Assert(continuationFragments.All(fragment => fragment.SourceTextLength >= 0),
    "Continuation fragments must carry a valid source-text length.");

var orderedSourceLines = first.Pages
    .SelectMany(page => page.Columns)
    .SelectMany(column => column.Fragments)
    .Where(fragment => fragment.SourceBlockIndex >= 0 && fragment.Kind != PageLayoutFragmentKind.OversetIndicator)
    .Select(fragment => fragment.SourceLine)
    .ToArray();
Assert(orderedSourceLines.Zip(orderedSourceLines.Skip(1)).All(pair => pair.First <= pair.Second),
    "Layout fragments must preserve authored source-block order.");
Assert(((ParagraphBlock)document.Blocks[1]).Inlines.ToPlainText().StartsWith("Deterministic pagination", StringComparison.Ordinal),
    "Pagination must not mutate canonical authored text.");

Assert(PagedLayoutEngine.FormatPageNumber(14, PageNumberStyle.UpperRoman) == "XIV", "Roman page-number formatting failed.");
Assert(PagedLayoutEngine.FormatPageNumber(27, PageNumberStyle.UpperLetters) == "AA", "Alphabetic page-number formatting failed.");

var wrapDocument = new DocumentAst(
[
    new FigureBlock(
        1,
        "assets/wrap.png",
        "Wrapped figure",
        "fig-wrap",
        Layout: new FigureLayout(
            WidthPercent: 40,
            Alignment: FigureAlignment.Left,
            Placement: FigurePlacement.Here))
    {
        Formatting = new RichBlockFormatting(
            AnchoredObject: new AnchoredObjectFormatting(
                "fig-wrap-anchor",
                Placement: FloatPlacementMode.Here,
                Wrap: TextWrapMode.BoundingBox,
                WrapRightPoints: 8))
    },
    new ParagraphBlock(
        2,
        [Text(string.Join(' ', Enumerable.Repeat("Side flow follows the figure boundary.", 8)))])
]);
var wrappedLayout = engine.Paginate(wrapDocument, style);
var wrappedPage = wrappedLayout.Pages.First(page => !page.IsBlank);
var wrappedFigure = wrappedPage.FloatingObjects.Single(fragment => fragment.SourceLine == 1);
var wrappedText = wrappedPage.Columns
    .SelectMany(column => column.Fragments)
    .First(fragment => fragment.SourceLine == 2);
var wrappedColumn = wrappedPage.Columns.First(column =>
    wrappedText.Bounds.XPoints >= column.Bounds.XPoints &&
    wrappedText.Bounds.XPoints < column.Bounds.RightPoints);
Assert(wrappedFigure.Wrap == TextWrapMode.BoundingBox,
    "Wrapped figures must remain floating page objects.");
Assert(wrappedText.Bounds.WidthPoints < wrappedColumn.Bounds.WidthPoints &&
       wrappedText.Bounds.XPoints > wrappedColumn.Bounds.XPoints,
    "Bounding-box wrap must move following text into the available side-flow region.");

var explicitFrame = new TextFrameFormatting(
    "frame.direct",
    Columns: 2,
    ColumnGapPoints: 10,
    InsetTopPoints: 6,
    InsetRightPoints: 7,
    InsetBottomPoints: 8,
    InsetLeftPoints: 9,
    XPoints: 24,
    YPoints: 36,
    WidthPoints: 160,
    HeightPoints: 72);
var explicitFrameDocument = new DocumentAst(
[
    new ParagraphBlock(
        1,
        [Text(string.Join(' ', Enumerable.Repeat("Explicit text frame geometry must constrain story flow.", 20)))])
    {
        Formatting = new RichBlockFormatting(TextFrame: explicitFrame)
    }
]);
var explicitFrameLayout = engine.Paginate(explicitFrameDocument, style);
var explicitFramePage = explicitFrameLayout.Pages.First(page => !page.IsBlank);
var explicitFrameFragment = explicitFramePage.Columns
    .SelectMany(column => column.Fragments)
    .First(fragment => fragment.Kind == PageLayoutFragmentKind.TextFrame);
Assert(explicitFrameFragment.ContainerBounds is not null,
    "Text-frame fragments must expose their containing frame rectangle.");
Assert(Math.Abs(explicitFrameFragment.ContainerBounds!.XPoints - (explicitFramePage.ContentBounds.XPoints + 24)) < .001 &&
       Math.Abs(explicitFrameFragment.ContainerBounds.YPoints - (explicitFramePage.ContentBounds.YPoints + 36)) < .001 &&
       Math.Abs(explicitFrameFragment.ContainerBounds.WidthPoints - 160) < .001 &&
       Math.Abs(explicitFrameFragment.ContainerBounds.HeightPoints - 72) < .001,
    "Explicit text-frame geometry must project relative to the page content box.");
Assert(explicitFrameFragment.FrameColumns == 2,
    "Text-frame column count must survive into the paged projection.");
var explicitFrameFragments = explicitFramePage.Columns
    .SelectMany(column => column.Fragments)
    .Where(fragment => fragment.Kind == PageLayoutFragmentKind.TextFrame &&
                       fragment.FrameId == "frame.direct")
    .ToArray();
Assert(explicitFrameFragments.Length >= 2 &&
       explicitFrameFragments.Select(fragment => fragment.Bounds.XPoints).Distinct().Count() >= 2,
    "A multi-column text frame must flow through distinct internal frame columns.");
Assert(explicitFrameLayout.Warnings.Any(warning => warning.Code == "overset"),
    "A deliberately small explicit frame must report overset instead of dropping text.");
Assert(explicitFramePage.Columns.SelectMany(column => column.Fragments)
        .Any(fragment => fragment.Kind == PageLayoutFragmentKind.OversetIndicator &&
                         fragment.FrameId == "frame.direct" &&
                         fragment.ContainerBounds is not null),
    "Overset indicators must remain attached to the explicit frame container.");

var markdown = "alpha\n\nbeta\n";
var edited = RichBlockFormattingEditor.Upsert(
    markdown,
    3,
    current => RichBlockFormattingEditor.ReplaceSection(
        current,
        new SectionFormatting(Columns: 2, ColumnGapPoints: 16, PageStyleId: "page.smoke")));
var richParser = new RichDocumentParser(new AdvancedDocumentParser());
var parsed = richParser.Parse(edited);
var beta = parsed.Blocks.OfType<ParagraphBlock>().Last();

var frameMarkdown = RichBlockFormattingEditor.SetTextFrame(
    "Frame body remains Markdown.\n",
    1,
    explicitFrame);
var frameParsed = richParser.Parse(frameMarkdown);
var frameBlock = frameParsed.Blocks.OfType<ParagraphBlock>().Single();
Assert(frameBlock.Formatting?.TextFrame is { } parsedFrame &&
       parsedFrame.Id == "frame.direct" &&
       parsedFrame.Columns == 2 &&
       parsedFrame.XPoints == 24 &&
       parsedFrame.YPoints == 36 &&
       parsedFrame.WidthPoints == 160 &&
       parsedFrame.HeightPoints == 72,
    "Direct text-frame geometry must round-trip through canonical Markdown metadata.");
var movedFrameMarkdown = TextFrameSourceEditor.UpdateFrame(
    frameMarkdown,
    frameParsed,
    "frame.direct",
    current => current with { XPoints = 42, WidthPoints = 180 },
    frameBlock.SourceLine);
var movedFrame = richParser.Parse(movedFrameMarkdown).Blocks.OfType<ParagraphBlock>().Single().Formatting?.TextFrame;
Assert(movedFrame?.XPoints == 42 && movedFrame.WidthPoints == 180,
    "TextFrameSourceEditor must update direct-manipulation geometry without rewriting authored text.");
Assert(beta.Inlines.ToPlainText() == "beta", "Layout metadata editing must leave authored Markdown text intact.");
Assert(beta.Formatting?.Section?.Columns == 2, "Inserted section metadata did not round-trip through the rich parser.");

var removed = RichBlockFormattingEditor.Upsert(
    edited,
    beta.SourceLine,
    current => RichBlockFormattingEditor.ReplaceSection(current, null));
var reparsed = richParser.Parse(removed);
Assert(reparsed.Blocks.OfType<ParagraphBlock>().Last().Formatting?.Section is null,
    "Removing page-layout metadata should restore an unformatted block without changing its text.");

var figureMarkdown = FigureMarkupCodec.Serialize(
    new FigureBlock(
        2,
        "assets/cover.png",
        "Cover art",
        "fig-cover",
        Layout: new FigureLayout(WidthPercent: 72, Placement: FigurePlacement.Here)),
    "\n") + "\n";
var anchoredFigureMarkdown = RichBlockFormattingEditor.SetAnchoredObject(
    figureMarkdown,
    2,
    new AnchoredObjectFormatting(
        "fig-cover-anchor",
        Placement: FloatPlacementMode.Here,
        Wrap: TextWrapMode.BoundingBox,
        OffsetXPoints: 18,
        OffsetYPoints: -6));
var combinedFigureParser = new EmojiDocumentParser(new AdvancedDocumentParser());
var combinedFigure = combinedFigureParser.Parse(anchoredFigureMarkdown).Blocks.OfType<FigureBlock>().Single();
Assert(combinedFigure.Layout?.WidthPercent == 72,
    "Figure semantic metadata must survive adjacent rich anchored-object metadata.");
Assert(combinedFigure.Formatting?.AnchoredObject?.Wrap == TextWrapMode.BoundingBox &&
       combinedFigure.Formatting.AnchoredObject.OffsetXPoints == 18 &&
       combinedFigure.Formatting.AnchoredObject.OffsetYPoints == -6,
    "Anchored drag/wrap metadata must survive beside figure metadata.");
var figureLines = anchoredFigureMarkdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
Assert(RichMarkdownFormattingCodec.IsBlockMetadata(figureLines[0]) &&
       figureLines[1].StartsWith(FigureMarkupCodec.MetadataPrefix, StringComparison.Ordinal) &&
       figureLines[2].StartsWith("![", StringComparison.Ordinal),
    "Rich block metadata must be emitted before figure metadata so both layers bind to the same image.");

var editableMarkdown =
    "# Canvas title\r\n\r\n" +
    "First **editable** paragraph line.\r\n" +
    "Continuation stays in the same source block.\r\n\r\n" +
    "## Next heading\r\n\r\n" +
    "Following paragraph.\r\n";
var editableDocument = richParser.Parse(editableMarkdown);
var editableIndex = editableDocument.Blocks
    .Select((block, index) => (block, index))
    .First(item => item.block is ParagraphBlock paragraph &&
                   paragraph.Inlines.ToPlainText().Contains("First", StringComparison.Ordinal))
    .index;
var editableSpan = PagedLayoutSourceMapper.GetEditableSpan(editableMarkdown, editableDocument, editableIndex);
Assert(editableSpan.Text ==
       "First **editable** paragraph line.\r\nContinuation stays in the same source block.",
    "Paged source mapping should expose the exact Markdown block without consuming blank separators.");
var replacedBlock = PagedLayoutSourceMapper.ReplaceBlock(
    editableMarkdown,
    editableDocument,
    editableIndex,
    "Changed paragraph.\r\nStill the same block.");
Assert(replacedBlock.Contains(
        "Changed paragraph.\r\nStill the same block.\r\n\r\n## Next heading",
        StringComparison.Ordinal),
    "Paged source replacement must preserve the surrounding Markdown block separators.");
var replacedDocument = richParser.Parse(replacedBlock);
Assert(replacedDocument.Blocks.OfType<ParagraphBlock>()
        .Any(paragraph => paragraph.Inlines.ToPlainText().Contains("Changed paragraph", StringComparison.Ordinal)),
    "Markdown edited through the page-source mapper must remain parseable by the canonical parser.");

var formattedMarkdown = "## Heading\n\nAlpha **bravo** charlie [delta](https://example.test) echo.\n";
var formattedDocument = richParser.Parse(formattedMarkdown);
var formattedBlockIndex = formattedDocument.Blocks
    .Select((block, index) => (block, index))
    .First(item => item.block is ParagraphBlock)
    .index;
var formattedPlain = PagedLayoutSourceMapper.PlainTextFor(formattedDocument.Blocks[formattedBlockIndex]);
var charlieOffset = formattedPlain.IndexOf("charlie", StringComparison.Ordinal);
var mappedCharlie = PagedLayoutSourceMapper.GetSourceOffsetForPlainText(
    formattedMarkdown,
    formattedDocument,
    formattedBlockIndex,
    charlieOffset);
Assert(formattedMarkdown.AsSpan(mappedCharlie).StartsWith("charlie".AsSpan(), StringComparison.Ordinal),
    "Plain pagination offsets must map through Markdown emphasis/link punctuation to the authored source.");
var deltaOffset = formattedPlain.IndexOf("delta", StringComparison.Ordinal);
var mappedDelta = PagedLayoutSourceMapper.GetSourceOffsetForPlainText(
    formattedMarkdown,
    formattedDocument,
    formattedBlockIndex,
    deltaOffset);
Assert(formattedMarkdown.AsSpan(mappedDelta).StartsWith("delta".AsSpan(), StringComparison.Ordinal),
    "Plain pagination offsets must map to link labels rather than link destination syntax.");

Console.WriteLine($"Paged layout smoke passed: {first.Pages.Count} pages, {first.Warnings.Count} warnings, deterministic fingerprint {Fingerprint(first).GetHashCode(StringComparison.Ordinal)}.");
