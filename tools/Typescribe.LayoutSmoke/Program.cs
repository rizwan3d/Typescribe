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
Assert(beta.Inlines.ToPlainText() == "beta", "Layout metadata editing must leave authored Markdown text intact.");
Assert(beta.Formatting?.Section?.Columns == 2, "Inserted section metadata did not round-trip through the rich parser.");

var removed = RichBlockFormattingEditor.Upsert(
    edited,
    beta.SourceLine,
    current => RichBlockFormattingEditor.ReplaceSection(current, null));
var reparsed = richParser.Parse(removed);
Assert(reparsed.Blocks.OfType<ParagraphBlock>().Last().Formatting?.Section is null,
    "Removing page-layout metadata should restore an unformatted block without changing its text.");

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

Console.WriteLine($"Paged layout smoke passed: {first.Pages.Count} pages, {first.Warnings.Count} warnings, deterministic fingerprint {Fingerprint(first).GetHashCode(StringComparison.Ordinal)}.");
