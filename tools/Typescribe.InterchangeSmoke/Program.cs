using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Typescribe.Application.Abstractions;
using Typescribe.Application.Services;
using Typescribe.Domain.Models;
using Typescribe.Infrastructure.Services;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static int CountOccurrences(string text, string value)
{
    var count = 0;
    var offset = 0;
    while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
    {
        count++;
        offset += value.Length;
    }
    return count;
}

static string ReadZipEntry(string path, string entryPath)
{
    using var archive = ZipFile.OpenRead(path);
    var entry = archive.GetEntry(entryPath) ?? throw new InvalidOperationException($"Missing ZIP entry: {entryPath}");
    using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
    return reader.ReadToEnd();
}

var parser = new EmojiDocumentParser(new AdvancedDocumentParser());
var projectRoot = Path.Combine(Path.GetTempPath(), "typescribe-interchange-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(projectRoot);
Directory.CreateDirectory(Path.Combine(projectRoot, "fonts"));

try
{
    var root = new ProjectNode("book", "Interchange smoke", NodeKind.Book);
    var chapter = new ProjectNode("chapter-1", "Multilingual", NodeKind.Chapter, "chapter-1.md");
    root.AddChild(chapter);
    var project = new BookProject
    {
        RootPath = projectRoot,
        Title = "Interchange smoke",
        Author = "TypeScribe",
        Language = "ur-PK",
        Root = root
    };

    var paragraph = new ParagraphFormatting(
        CharacterDefaults: new CharacterFormatting(
            Font: new FontReference("Noto Nastaliq Urdu", FontSourceKind.Project, "fonts/NotoNastaliqUrdu.ttf"),
            FontSizePoints: 16,
            Language: "ur-PK",
            Script: ScriptMode.UrduNastaliq,
            Direction: TextDirectionMode.RightToLeft),
        Alignment: TextAlignmentMode.Right,
        Direction: TextDirectionMode.RightToLeft,
        Language: "ur-PK",
        Script: ScriptMode.UrduNastaliq,
        LineSpacing: 1.35,
        SpaceAfterPoints: 7,
        KeepWithNext: true,
        KeepLinesTogether: true,
        Tabs: [new TabStop(72, TabStopAlignment.Right, '.')]);

    var characters = new CharacterFormatting(
        Font: new FontReference("Noto Nastaliq Urdu", FontSourceKind.Project, "fonts/NotoNastaliqUrdu.ttf"),
        FontSizePoints: 18,
        Bold: true,
        Underline: true,
        SmallCaps: true,
        Ligatures: true,
        Kerning: true,
        TrackingEm: .03,
        Language: "ur-PK",
        Script: ScriptMode.UrduNastaliq,
        Direction: TextDirectionMode.RightToLeft,
        OpenTypeFeatures: [new OpenTypeFeatureSetting("liga", 1), new OpenTypeFeatureSetting("kern", 1)],
        VariableAxes: [new VariableFontAxisSetting("wght", 500)]);

    var source = RichMarkdownFormattingCodec.CreateBlockMetadata(new RichBlockFormatting(Paragraph: paragraph))
        + Environment.NewLine
        + RichMarkdownFormattingCodec.WrapInline("اردو", characters)
        + " English 123";

    // LuaLaTeX: plain Unicode Arabic-script Markdown must not require rich metadata to publish.
    var latexRenderer = new RichDocumentRenderer();

    const string plainUrduText = "یہ ایک اردو کتاب ہے۔";
    var plainUrduLatex = latexRenderer.RenderLatex(parser.Parse(plainUrduText), "Plain Urdu", BookStyle.Default);
    Require(plainUrduLatex.Contains("\\usepackage[bidi=basic]{babel}", StringComparison.Ordinal), "Plain Urdu did not enable Babel BiDi support.");
    Require(plainUrduLatex.Contains("\\babelprovide[import]{urdu}", StringComparison.Ordinal), "Plain Urdu did not import the Babel Urdu locale.");
    Require(plainUrduLatex.Contains("\\foreignlanguage{urdu}{", StringComparison.Ordinal), "Plain Urdu was not wrapped as Urdu for LuaLaTeX.");
    Require(plainUrduLatex.Contains("\\IfFontExistsTF{Noto Nastaliq Urdu}", StringComparison.Ordinal), "Plain Urdu did not emit the Nastaliq font fallback chain.");
    Require(plainUrduLatex.Contains("Renderer=HarfBuzz,Script=Arabic", StringComparison.Ordinal), "Plain Urdu did not request HarfBuzz Arabic shaping.");
    Require(plainUrduLatex.Contains(plainUrduText, StringComparison.Ordinal), "Plain Urdu logical Unicode text changed during LaTeX rendering.");

    const string plainArabicText = "هذا كتاب عربي.";
    var plainArabicLatex = latexRenderer.RenderLatex(parser.Parse(plainArabicText), "Plain Arabic", BookStyle.Default);
    Require(plainArabicLatex.Contains("\\babelprovide[import]{arabic}", StringComparison.Ordinal), "Plain Arabic did not import the Babel Arabic locale.");
    Require(plainArabicLatex.Contains("\\foreignlanguage{arabic}{", StringComparison.Ordinal), "Plain Arabic was not wrapped as Arabic for LuaLaTeX.");
    Require(plainArabicLatex.Contains("\\IfFontExistsTF{Noto Naskh Arabic}", StringComparison.Ordinal), "Plain Arabic did not emit the Arabic font fallback chain.");
    Require(plainArabicLatex.Contains(plainArabicText, StringComparison.Ordinal), "Plain Arabic logical Unicode text changed during LaTeX rendering.");

    const string mixedUrduText = "یہ 2026 English ہے";
    var mixedUrduLatex = latexRenderer.RenderLatex(parser.Parse(mixedUrduText), "Mixed Urdu", BookStyle.Default);
    Require(mixedUrduLatex.Contains("\\foreignlanguage{urdu}{", StringComparison.Ordinal), "Mixed Urdu/English text did not keep an Urdu RTL base language.");
    Require(mixedUrduLatex.Contains(mixedUrduText, StringComparison.Ordinal), "Mixed Urdu/English logical Unicode order changed during LaTeX rendering.");

    var structuredUrduSource = "# بڑے عنوان\n\n- یہ فہرست ہے\n\n| بڑے عنوان |\n| --- |\n| یہ اردو ہے |";
    var structuredUrduLatex = latexRenderer.RenderLatex(parser.Parse(structuredUrduSource), "Structured Urdu", BookStyle.Default);
    Require(CountOccurrences(structuredUrduLatex, "\\foreignlanguage{urdu}{") >= 4, "Urdu headings, lists, or table cells were not auto-detected for publishing.");

    var plainEnglishLatex = latexRenderer.RenderLatex(parser.Parse("Plain English paragraph."), "Plain English", BookStyle.Default);
    Require(!plainEnglishLatex.Contains("\\usepackage[bidi=basic]{babel}", StringComparison.Ordinal), "Plain English unexpectedly enabled multilingual BiDi support.");

    var explicitUrduLatex = latexRenderer.RenderLatex(parser.Parse(source), "Explicit Urdu font", BookStyle.Default);
    Require(explicitUrduLatex.Contains("NotoNastaliqUrdu.ttf", StringComparison.Ordinal), "Explicit project Urdu font was not preserved in LaTeX output.");
    Require(!explicitUrduLatex.Contains("\\IfFontExistsTF{Noto Nastaliq Urdu}", StringComparison.Ordinal), "Automatic Urdu font fallback overrode an explicit project font.");

    // Section fonts: heading-defined sections can carry independent font choices without changing text.
    const string sectionSource = "# First section\n\nAlpha body.\n\n# Second section\n\nBeta body.";
    var firstFont = new FontReference("Section Serif A", FontSourceKind.System);
    var secondFontPath = Path.Combine(projectRoot, "fonts", "SectionCustom.ttf");
    var secondFont = new FontReference("Section Custom B", FontSourceKind.Project, secondFontPath);

    var firstRange = SectionFontFormatter.ResolveSection(parser, sectionSource, 1);
    Require(firstRange.Label == "First section", "Section font range did not resolve the current heading.");
    Require(firstRange.TextBlockCount == 2, "Section font range should include its heading and body paragraph.");

    var withFirstFont = SectionFontFormatter.Apply(parser, sectionSource, 1, firstFont, 12);
    var firstPass = parser.Parse(withFirstFont);
    var secondHeadingLine = firstPass.Blocks.OfType<HeadingBlock>()
        .First(heading => heading.Inlines.ToPlainText() == "Second section").SourceLine;
    var withBothFonts = SectionFontFormatter.Apply(parser, withFirstFont, secondHeadingLine, secondFont, 14);
    var sectionAst = parser.Parse(withBothFonts);

    var firstHeading = sectionAst.Blocks.OfType<HeadingBlock>()
        .First(heading => heading.Inlines.ToPlainText() == "First section");
    var alpha = sectionAst.Blocks.OfType<ParagraphBlock>()
        .First(block => block.Inlines.ToPlainText() == "Alpha body.");
    var secondHeading = sectionAst.Blocks.OfType<HeadingBlock>()
        .First(heading => heading.Inlines.ToPlainText() == "Second section");
    var beta = sectionAst.Blocks.OfType<ParagraphBlock>()
        .First(block => block.Inlines.ToPlainText() == "Beta body.");

    Require(firstHeading.Formatting?.Paragraph?.CharacterDefaults?.Font?.Family == "Section Serif A", "First section heading lost its font override.");
    Require(alpha.Formatting?.Paragraph?.CharacterDefaults?.Font?.Family == "Section Serif A", "First section body lost its font override.");
    Require(secondHeading.Formatting?.Paragraph?.CharacterDefaults?.Font?.Family == "Section Custom B", "Second section heading did not receive its independent font.");
    Require(beta.Formatting?.Paragraph?.CharacterDefaults?.Font?.ProjectPath == secondFontPath, "Second section custom font path was not preserved.");
    Require(RichMarkdownFormattingCodec.StripFormattingMetadata(withBothFonts).Trim() == sectionSource, "Applying section fonts changed authored Markdown text.");

    var sectionLatex = latexRenderer.RenderLatex(sectionAst, "Section fonts", BookStyle.Default);
    Require(sectionLatex.Contains("\\fontspec{Section Serif A}", StringComparison.Ordinal), "First section system font was not emitted to LaTeX.");
    Require(sectionLatex.Contains("SectionCustom.ttf", StringComparison.Ordinal), "Second section custom font file was not emitted to LaTeX.");

    var secondHeadingAfterApply = sectionAst.Blocks.OfType<HeadingBlock>()
        .First(heading => heading.Inlines.ToPlainText() == "Second section").SourceLine;
    var clearedSecond = SectionFontFormatter.Clear(parser, withBothFonts, secondHeadingAfterApply);
    var clearedAst = parser.Parse(clearedSecond);
    var clearedAlpha = clearedAst.Blocks.OfType<ParagraphBlock>()
        .First(block => block.Inlines.ToPlainText() == "Alpha body.");
    var clearedBeta = clearedAst.Blocks.OfType<ParagraphBlock>()
        .First(block => block.Inlines.ToPlainText() == "Beta body.");
    Require(clearedAlpha.Formatting?.Paragraph?.CharacterDefaults?.Font?.Family == "Section Serif A", "Clearing the second section font changed the first section.");
    Require(clearedBeta.Formatting?.Paragraph?.CharacterDefaults?.Font is null, "Clearing the second section font did not remove its override.");

    // Clipboard: TypeScribe -> HTML/RTF/plain keeps logical Unicode order and rich semantics.
    var clipboard = RichClipboardCodec.Export(source, parser);
    Require(clipboard.PlainText.Contains("اردو English 123", StringComparison.Ordinal), "Clipboard plain text changed logical Unicode order.");
    Require(clipboard.Html.Contains("dir=\"rtl\"", StringComparison.Ordinal), "Clipboard HTML did not preserve RTL direction.");
    Require(clipboard.Html.Contains("font-feature-settings", StringComparison.Ordinal), "Clipboard HTML did not preserve OpenType settings.");
    Require(clipboard.Html.Contains("font-variation-settings", StringComparison.Ordinal), "Clipboard HTML did not preserve variable-font axes.");

    var htmlImport = RichClipboardCodec.Import(null, Encoding.UTF8.GetBytes(clipboard.Html), null, null);
    Require(htmlImport.Markdown.Contains("اردو", StringComparison.Ordinal), "HTML clipboard import lost Urdu text.");
    Require(htmlImport.Markdown.Contains(RichMarkdownFormattingCodec.InlinePrefix, StringComparison.Ordinal), "HTML clipboard import lost rich inline metadata.");

    var rtfImport = RichClipboardCodec.Import(null, null, clipboard.Rtf, null);
    Require(rtfImport.Markdown.Contains("اردو", StringComparison.Ordinal), "RTF clipboard import lost Urdu text.");
    Require(rtfImport.Markdown.Contains(RichMarkdownFormattingCodec.InlinePrefix, StringComparison.Ordinal), "RTF clipboard import lost rich inline metadata.");

    var privateImport = RichClipboardCodec.Import(source, null, null, null);
    Require(string.Equals(privateImport.Markdown, source, StringComparison.Ordinal), "TypeScribe private clipboard flavor was not exact.");

    // DOCX: inspect actual WordprocessingML and then import back to rich Markdown.
    var docxPath = Path.Combine(projectRoot, "rich.docx");
    var docx = new RichDocxInterchangeService(parser, new BibTeXDatabase());
    await docx.ExportAsync(project, [(chapter, source)], docxPath);
    var wordXml = XDocument.Parse(ReadZipEntry(docxPath, "word/document.xml"));
    XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    Require(wordXml.Descendants(w + "bidi").Any(), "DOCX paragraph direction was not written.");
    Require(wordXml.Descendants(w + "rtl").Any(), "DOCX run RTL property was not written.");
    Require(wordXml.Descendants(w + "rFonts").Any(element =>
        string.Equals((string?)element.Attribute(w + "cs"), "Noto Nastaliq Urdu", StringComparison.Ordinal)), "DOCX font family was not written.");
    Require(wordXml.Descendants(w + "lang").Any(element =>
        string.Equals((string?)element.Attribute(w + "bidi"), "ur-PK", StringComparison.OrdinalIgnoreCase)), "DOCX language metadata was not written.");
    Require(wordXml.Descendants(w + "u").Any(), "DOCX underline was not written.");
    Require(wordXml.Descendants(w + "smallCaps").Any(), "DOCX small caps were not written.");
    Require(string.Concat(wordXml.Descendants(w + "t").Select(static node => node.Value)).Contains("اردو English 123", StringComparison.Ordinal), "DOCX changed logical Unicode text.");
    Require(docx.LastWarnings.Any(), "DOCX unsupported OpenType/variable settings should produce warnings.");

    var importedDocx = await docx.ImportAsync(project, docxPath);
    Require(importedDocx.Contains("اردو", StringComparison.Ordinal), "DOCX import lost Urdu text.");
    Require(importedDocx.Contains(RichMarkdownFormattingCodec.BlockPrefix, StringComparison.Ordinal), "DOCX import did not recreate paragraph metadata.");
    Require(importedDocx.Contains(RichMarkdownFormattingCodec.InlinePrefix, StringComparison.Ordinal), "DOCX import did not recreate character metadata.");

    // EPUB: inspect emitted XHTML/CSS. The unapproved project font must not be copied.
    var epubPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(projectRoot, "rich.epub");
    var epub = new RichEpub3ExportService(parser);
    await epub.ExportAsync(project, [new EpubDocumentSource(chapter, source)], epubPath);
    var xhtml = XDocument.Parse(ReadZipEntry(epubPath, "chapters/chapter-001.xhtml"));
    XNamespace xhtmlNs = "http://www.w3.org/1999/xhtml";
    Require(xhtml.Descendants().Any(element => string.Equals((string?)element.Attribute("dir"), "rtl", StringComparison.Ordinal)), "EPUB did not preserve RTL direction.");
    Require(xhtml.Descendants().Any(element => string.Equals((string?)element.Attribute("lang"), "ur-PK", StringComparison.OrdinalIgnoreCase)), "EPUB did not preserve language.");
    Require(xhtml.Descendants(xhtmlNs + "span").Any(element => ((string?)element.Attribute("style"))?.Contains("font-feature-settings", StringComparison.Ordinal) == true), "EPUB did not preserve OpenType feature CSS.");
    Require(xhtml.Descendants(xhtmlNs + "span").Any(element => ((string?)element.Attribute("style"))?.Contains("font-variation-settings", StringComparison.Ordinal) == true), "EPUB did not preserve variable-font CSS.");
    Require(string.Concat(xhtml.DescendantNodes().OfType<XText>().Select(static node => node.Value)).Contains("اردو English 123", StringComparison.Ordinal), "EPUB changed logical Unicode text.");
    Require(epub.LastWarnings.Any(warning => warning.Contains("not embedded", StringComparison.OrdinalIgnoreCase)), "EPUB should warn when a project font is not explicitly approved for embedding.");
    using (var package = ZipFile.OpenRead(epubPath))
        Require(!package.Entries.Any(entry => entry.FullName.StartsWith("fonts/", StringComparison.Ordinal)), "EPUB embedded an unapproved project font.");

    Console.WriteLine("TypeScribe rich interchange smoke fixtures passed.");
}
finally
{
    // Keep an explicitly requested EPUB output for EPUBCheck; clean only the temporary project.
    var requestedOutput = args.Length > 0 ? Path.GetFullPath(args[0]) : null;
    if (requestedOutput is not null && requestedOutput.StartsWith(projectRoot, StringComparison.Ordinal)) requestedOutput = null;
    try { Directory.Delete(projectRoot, recursive: true); } catch { }
}
