using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

public sealed record EpubDocumentSource(ProjectNode Node, string Content);

/// <summary>
/// Produces reflowable EPUB 3 packages from TypeScribe's semantic manuscript AST.
/// The package is intentionally layout-light: structure, navigation, metadata and
/// project-managed resources are preserved while presentation lives in book.css.
/// </summary>
public sealed class Epub3ExportService
{
    private const string EpubMediaType = "application/epub+zip";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Epub = "http://www.idpf.org/2007/ops";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    private readonly IDocumentParser _parser;

    public Epub3ExportService(IDocumentParser parser)
        => _parser = parser ?? throw new ArgumentNullException(nameof(parser));

    public async Task ExportAsync(
        BookProject project,
        IReadOnlyList<EpubDocumentSource> documents,
        string destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (documents.Count == 0) throw new InvalidOperationException("EPUB export requires at least one included manuscript document.");

        cancellationToken.ThrowIfCancellationRequested();
        var chapters = documents.Select((document, index) => ParseChapter(document, index)).ToArray();
        var targets = BuildCrossReferenceTargets(chapters);
        var assets = CollectAssets(project, chapters);
        var cover = AddCoverIfPresent(project, assets);
        var identifier = CreateIdentifier(project, documents);
        var modified = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        var fullDestination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        var temporary = fullDestination + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, useAsync: true))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false, entryNameEncoding: Encoding.UTF8))
            {
                // EPUB requires this to be the first ZIP entry and to be stored without compression.
                await WriteTextEntryAsync(archive, "mimetype", EpubMediaType, CompressionLevel.NoCompression, cancellationToken);
                await WriteTextEntryAsync(archive, "META-INF/container.xml", BuildContainerXml(), CompressionLevel.Optimal, cancellationToken);
                await WriteTextEntryAsync(archive, "styles/book.css", BookCss, CompressionLevel.Optimal, cancellationToken);

                foreach (var asset in assets.ByFullPath.Values.OrderBy(static asset => asset.PackagePath, StringComparer.Ordinal))
                    await CopyAssetAsync(archive, asset, cancellationToken);

                if (cover is not null)
                    await WriteTextEntryAsync(archive, "cover.xhtml", BuildCoverXhtml(project, cover), CompressionLevel.Optimal, cancellationToken);

                foreach (var chapter in chapters)
                {
                    var xhtml = BuildChapterXhtml(project, chapter, targets, assets.BySource);
                    await WriteTextEntryAsync(archive, chapter.PackagePath, xhtml, CompressionLevel.Optimal, cancellationToken);
                }

                await WriteTextEntryAsync(archive, "nav.xhtml", BuildNavigationXhtml(project, chapters, cover is not null), CompressionLevel.Optimal, cancellationToken);
                await WriteTextEntryAsync(
                    archive,
                    "package.opf",
                    BuildPackageOpf(project, chapters, assets.ByFullPath.Values.ToArray(), cover, identifier, modified),
                    CompressionLevel.Optimal,
                    cancellationToken);
            }

            File.Move(temporary, fullDestination, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private ParsedChapter ParseChapter(EpubDocumentSource source, int index)
    {
        var ast = _parser.Parse(source.Content ?? string.Empty);
        var firstHeading = ast.Blocks.OfType<HeadingBlock>().FirstOrDefault();
        var title = firstHeading?.Inlines.ToPlainText();
        if (string.IsNullOrWhiteSpace(title)) title = source.Node.Title;
        if (string.IsNullOrWhiteSpace(title)) title = $"Chapter {index + 1}";
        return new ParsedChapter(source.Node, ast, title.Trim(), index + 1, $"chapters/chapter-{index + 1:000}.xhtml");
    }

    private static IReadOnlyDictionary<string, CrossReferenceTarget> BuildCrossReferenceTargets(IReadOnlyList<ParsedChapter> chapters)
    {
        var targets = new Dictionary<string, CrossReferenceTarget>(StringComparer.Ordinal);
        var figure = 0;
        var table = 0;
        var equation = 0;

        foreach (var chapter in chapters)
        {
            var chapterFile = Path.GetFileName(chapter.PackagePath);
            foreach (var block in chapter.Ast.Blocks)
            {
                switch (block)
                {
                    case HeadingBlock heading when !string.IsNullOrWhiteSpace(heading.Identifier):
                        AddTarget(targets, heading.Identifier!, chapterFile, heading.Identifier!, heading.Inlines.ToPlainText());
                        break;
                    case FigureBlock figureBlock when !string.IsNullOrWhiteSpace(figureBlock.Identifier):
                        figure++;
                        AddTarget(targets, figureBlock.Identifier!, chapterFile, figureBlock.Identifier!, $"Figure {figure}");
                        break;
                    case FigureBlock:
                        figure++;
                        break;
                    case TableBlock tableBlock when !string.IsNullOrWhiteSpace(tableBlock.Identifier):
                        table++;
                        AddTarget(targets, tableBlock.Identifier!, chapterFile, tableBlock.Identifier!, $"Table {table}");
                        break;
                    case TableBlock:
                        table++;
                        break;
                    case DisplayMathBlock math when !string.IsNullOrWhiteSpace(math.Identifier):
                        equation++;
                        AddTarget(targets, math.Identifier!, chapterFile, math.Identifier!, $"Equation {equation}");
                        break;
                    case DisplayMathBlock:
                        equation++;
                        break;
                }
            }
        }
        return targets;
    }

    private static void AddTarget(
        IDictionary<string, CrossReferenceTarget> targets,
        string identifier,
        string chapterFile,
        string fragment,
        string label)
    {
        if (targets.ContainsKey(identifier)) return;
        targets[identifier] = new CrossReferenceTarget($"{chapterFile}#{HtmlId(fragment)}", string.IsNullOrWhiteSpace(label) ? identifier : label);
    }

    private static AssetCollection CollectAssets(BookProject project, IReadOnlyList<ParsedChapter> chapters)
    {
        var byFullPath = new Dictionary<string, AssetPackage>(PathComparer);
        var bySource = new Dictionary<string, AssetPackage>(StringComparer.Ordinal);
        var usedPackagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var figure in chapters.SelectMany(static chapter => chapter.Ast.Blocks.OfType<FigureBlock>()))
        {
            var fullPath = ResolveProjectResource(project.RootPath, figure.Source);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"EPUB image '{figure.Source}' was not found. Run manuscript preflight before exporting.", fullPath);
            var mediaType = ImageMediaType(fullPath)
                ?? throw new InvalidOperationException($"EPUB image '{figure.Source}' uses an unsupported format. Use PNG, JPEG, GIF, SVG, or WebP.");

            if (!byFullPath.TryGetValue(fullPath, out var asset))
            {
                var packagePath = UniqueAssetPath(fullPath, usedPackagePaths);
                asset = new AssetPackage(fullPath, packagePath, $"asset-{byFullPath.Count + 1}", mediaType, IsCover: false);
                byFullPath.Add(fullPath, asset);
            }
            bySource[figure.Source] = asset;
        }

        return new AssetCollection(byFullPath, bySource, usedPackagePaths);
    }

    private static AssetPackage? AddCoverIfPresent(BookProject project, AssetCollection assets)
    {
        var directory = Path.Combine(project.RootPath, "assets");
        if (!Directory.Exists(directory)) return null;
        var coverPath = Directory.EnumerateFiles(directory)
            .Where(static path => string.Equals(Path.GetFileNameWithoutExtension(path), "cover", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(static path => ImageMediaType(path) is not null);
        if (coverPath is null) return null;

        coverPath = Path.GetFullPath(coverPath);
        if (assets.ByFullPath.TryGetValue(coverPath, out var existing))
        {
            var cover = existing with { IsCover = true };
            assets.ByFullPath[coverPath] = cover;
            foreach (var source in assets.BySource.Where(pair => PathComparer.Equals(pair.Value.FullPath, coverPath)).Select(static pair => pair.Key).ToArray())
                assets.BySource[source] = cover;
            return cover;
        }

        var mediaType = ImageMediaType(coverPath)!;
        var packagePath = UniqueAssetPath(coverPath, assets.UsedPackagePaths);
        var added = new AssetPackage(coverPath, packagePath, $"asset-{assets.ByFullPath.Count + 1}", mediaType, IsCover: true);
        assets.ByFullPath.Add(coverPath, added);
        return added;
    }

    private static string BuildContainerXml()
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
           "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">" +
           "<rootfiles><rootfile full-path=\"package.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>";

    private static string BuildPackageOpf(
        BookProject project,
        IReadOnlyList<ParsedChapter> chapters,
        IReadOnlyList<AssetPackage> assets,
        AssetPackage? cover,
        string identifier,
        string modified)
    {
        var metadata = new XElement(Opf + "metadata",
            new XAttribute(XNamespace.Xmlns + "dc", Dc),
            new XElement(Dc + "identifier", new XAttribute("id", "book-id"), identifier),
            new XElement(Dc + "title", project.Title),
            new XElement(Dc + "language", string.IsNullOrWhiteSpace(project.Language) ? "en" : project.Language),
            string.IsNullOrWhiteSpace(project.Author) ? null : new XElement(Dc + "creator", project.Author),
            new XElement(Opf + "meta", new XAttribute("property", "dcterms:modified"), modified));

        var manifest = new XElement(Opf + "manifest",
            new XElement(Opf + "item",
                new XAttribute("id", "nav"),
                new XAttribute("href", "nav.xhtml"),
                new XAttribute("media-type", "application/xhtml+xml"),
                new XAttribute("properties", "nav")),
            new XElement(Opf + "item",
                new XAttribute("id", "css"),
                new XAttribute("href", "styles/book.css"),
                new XAttribute("media-type", "text/css")));

        if (cover is not null)
        {
            manifest.Add(new XElement(Opf + "item",
                new XAttribute("id", "cover-page"),
                new XAttribute("href", "cover.xhtml"),
                new XAttribute("media-type", "application/xhtml+xml")));
        }

        foreach (var chapter in chapters)
        {
            manifest.Add(new XElement(Opf + "item",
                new XAttribute("id", $"chapter-{chapter.Number:000}"),
                new XAttribute("href", chapter.PackagePath),
                new XAttribute("media-type", "application/xhtml+xml")));
        }

        foreach (var asset in assets.OrderBy(static item => item.PackagePath, StringComparer.Ordinal))
        {
            var item = new XElement(Opf + "item",
                new XAttribute("id", asset.Id),
                new XAttribute("href", asset.PackagePath),
                new XAttribute("media-type", asset.MediaType));
            if (asset.IsCover) item.Add(new XAttribute("properties", "cover-image"));
            manifest.Add(item);
        }

        var spine = new XElement(Opf + "spine");
        if (cover is not null) spine.Add(new XElement(Opf + "itemref", new XAttribute("idref", "cover-page")));
        foreach (var chapter in chapters)
            spine.Add(new XElement(Opf + "itemref", new XAttribute("idref", $"chapter-{chapter.Number:000}")));

        var package = new XElement(Opf + "package",
            new XAttribute("version", "3.0"),
            new XAttribute("unique-identifier", "book-id"),
            new XAttribute(XNamespace.Xml + "lang", string.IsNullOrWhiteSpace(project.Language) ? "en" : project.Language),
            metadata,
            manifest,
            spine);
        return Xml(package);
    }

    private static string BuildNavigationXhtml(BookProject project, IReadOnlyList<ParsedChapter> chapters, bool hasCover)
    {
        var toc = new XElement(Xhtml + "nav",
            new XAttribute(Epub + "type", "toc"),
            new XAttribute("id", "toc"),
            new XElement(Xhtml + "h1", "Contents"),
            new XElement(Xhtml + "ol",
                chapters.Select(chapter => new XElement(Xhtml + "li",
                    new XElement(Xhtml + "a", new XAttribute("href", chapter.PackagePath), chapter.Title)))));

        var landmarks = new XElement(Xhtml + "nav",
            new XAttribute(Epub + "type", "landmarks"),
            new XElement(Xhtml + "h2", "Landmarks"),
            new XElement(Xhtml + "ol",
                hasCover ? new XElement(Xhtml + "li", new XElement(Xhtml + "a", new XAttribute(Epub + "type", "cover"), new XAttribute("href", "cover.xhtml"), "Cover")) : null,
                new XElement(Xhtml + "li", new XElement(Xhtml + "a", new XAttribute(Epub + "type", "bodymatter"), new XAttribute("href", chapters[0].PackagePath), "Start of content"))));

        return XhtmlDocument(project, project.Title + " — Contents", new XElement(Xhtml + "main", toc, landmarks), "styles/book.css");
    }

    private static string BuildCoverXhtml(BookProject project, AssetPackage cover)
    {
        var section = new XElement(Xhtml + "section",
            new XAttribute(Epub + "type", "cover"),
            new XAttribute("class", "cover"),
            new XElement(Xhtml + "img",
                new XAttribute("src", cover.PackagePath),
                new XAttribute("alt", $"Cover of {project.Title}")));
        return XhtmlDocument(project, project.Title + " — Cover", section, "styles/book.css");
    }

    private static string BuildChapterXhtml(
        BookProject project,
        ParsedChapter chapter,
        IReadOnlyDictionary<string, CrossReferenceTarget> targets,
        IReadOnlyDictionary<string, AssetPackage> assetsBySource)
    {
        var section = new XElement(Xhtml + "section", new XAttribute(Epub + "type", "chapter"), new XAttribute("class", "chapter"));
        var definitions = chapter.Ast.Blocks.OfType<FootnoteDefinitionBlock>()
            .GroupBy(static definition => definition.Identifier, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var footnotes = new FootnoteRenderState(definitions);

        for (var index = 0; index < chapter.Ast.Blocks.Count;)
        {
            var block = chapter.Ast.Blocks[index];
            if (block is FootnoteDefinitionBlock or BibliographyEntryBlock)
            {
                index++;
                continue;
            }

            if (block is ListItemBlock listItem)
            {
                var list = new XElement(Xhtml + (listItem.Ordered ? "ol" : "ul"));
                if (listItem.Ordered && listItem.Number is > 1)
                    list.Add(new XAttribute("start", listItem.Number.Value));
                while (index < chapter.Ast.Blocks.Count && chapter.Ast.Blocks[index] is ListItemBlock item && item.Ordered == listItem.Ordered)
                {
                    list.Add(new XElement(Xhtml + "li", RenderInlines(item.Inlines, targets, footnotes)));
                    index++;
                }
                section.Add(list);
                continue;
            }

            section.Add(RenderBlock(block, project, targets, assetsBySource, footnotes));
            index++;
        }

        if (footnotes.Order.Count > 0)
        {
            var notes = new XElement(Xhtml + "section", new XAttribute("class", "footnotes"), new XElement(Xhtml + "hr"));
            foreach (var identifier in footnotes.Order)
            {
                if (!definitions.TryGetValue(identifier, out var definition)) continue;
                var number = footnotes.Numbers[identifier];
                var aside = new XElement(Xhtml + "aside",
                    new XAttribute(Epub + "type", "footnote"),
                    new XAttribute("id", FootnoteId(identifier)),
                    new XElement(Xhtml + "p",
                        new XElement(Xhtml + "span", new XAttribute("class", "footnote-number"), number.ToString(CultureInfo.InvariantCulture) + ". "),
                        RenderInlines(definition.Inlines, targets, footnotes),
                        " ",
                        new XElement(Xhtml + "a", new XAttribute("class", "footnote-back"), new XAttribute("href", "#" + footnotes.FirstReferenceIds[identifier]), "↩")));
                notes.Add(aside);
            }
            section.Add(notes);
        }

        return XhtmlDocument(project, chapter.Title, section, "../styles/book.css");
    }

    private static object? RenderBlock(
        AstBlock block,
        BookProject project,
        IReadOnlyDictionary<string, CrossReferenceTarget> targets,
        IReadOnlyDictionary<string, AssetPackage> assetsBySource,
        FootnoteRenderState footnotes)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                var element = new XElement(Xhtml + $"h{Math.Clamp(heading.Level, 1, 6)}", RenderInlines(heading.Inlines, targets, footnotes));
                if (!string.IsNullOrWhiteSpace(heading.Identifier)) element.Add(new XAttribute("id", HtmlId(heading.Identifier)));
                return element;
            }
            case ParagraphBlock paragraph:
                return new XElement(Xhtml + "p", RenderInlines(paragraph.Inlines, targets, footnotes));
            case QuoteBlock quote:
                return new XElement(Xhtml + "blockquote", new XElement(Xhtml + "p", RenderInlines(quote.Inlines, targets, footnotes)));
            case CodeBlock code:
            {
                var codeElement = new XElement(Xhtml + "code", code.Text);
                if (!string.IsNullOrWhiteSpace(code.Language)) codeElement.Add(new XAttribute("class", "language-" + CssToken(code.Language)));
                return new XElement(Xhtml + "pre", codeElement);
            }
            case DisplayMathBlock math:
            {
                var element = new XElement(Xhtml + "div", new XAttribute("class", "display-math"), new XElement(Xhtml + "code", math.Text));
                if (!string.IsNullOrWhiteSpace(math.Identifier)) element.Add(new XAttribute("id", HtmlId(math.Identifier)));
                return element;
            }
            case ThematicBreakBlock:
                return new XElement(Xhtml + "hr");
            case FigureBlock figure:
            {
                if (!assetsBySource.TryGetValue(figure.Source, out var asset))
                    throw new InvalidOperationException($"EPUB image '{figure.Source}' was not packaged.");
                var element = new XElement(Xhtml + "figure",
                    new XElement(Xhtml + "img",
                        new XAttribute("src", "../" + asset.PackagePath),
                        new XAttribute("alt", string.IsNullOrWhiteSpace(figure.Caption) ? Path.GetFileName(asset.FullPath) : figure.Caption)),
                    string.IsNullOrWhiteSpace(figure.Caption) ? null : new XElement(Xhtml + "figcaption", figure.Caption));
                if (!string.IsNullOrWhiteSpace(figure.Identifier)) element.Add(new XAttribute("id", HtmlId(figure.Identifier)));
                return element;
            }
            case TableBlock table:
            {
                var element = new XElement(Xhtml + "table");
                if (!string.IsNullOrWhiteSpace(table.Identifier)) element.Add(new XAttribute("id", HtmlId(table.Identifier)));
                if (!string.IsNullOrWhiteSpace(table.Caption)) element.Add(new XElement(Xhtml + "caption", table.Caption));
                element.Add(new XElement(Xhtml + "thead",
                    new XElement(Xhtml + "tr", table.Header.Select((cell, column) =>
                        CellElement("th", cell, column, table.Alignments, targets, footnotes)))));
                element.Add(new XElement(Xhtml + "tbody",
                    table.Rows.Select(row => new XElement(Xhtml + "tr",
                        row.Select((cell, column) => CellElement("td", cell, column, table.Alignments, targets, footnotes))))));
                return element;
            }
            default:
                return null;
        }
    }

    private static XElement CellElement(
        string name,
        TableCell cell,
        int column,
        IReadOnlyList<TableAlignment>? alignments,
        IReadOnlyDictionary<string, CrossReferenceTarget> targets,
        FootnoteRenderState footnotes)
    {
        var element = new XElement(Xhtml + name, RenderInlines(cell.Inlines, targets, footnotes));
        if (alignments is not null && column < alignments.Count && alignments[column] != TableAlignment.Default)
            element.Add(new XAttribute("class", "align-" + alignments[column].ToString().ToLowerInvariant()));
        return element;
    }

    private static IEnumerable<object> RenderInlines(
        IReadOnlyList<AstInline> inlines,
        IReadOnlyDictionary<string, CrossReferenceTarget> targets,
        FootnoteRenderState footnotes)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextInline text:
                    yield return new XText(text.Text);
                    break;
                case StrongInline strong:
                    yield return new XElement(Xhtml + "strong", RenderInlines(strong.Children, targets, footnotes));
                    break;
                case EmphasisInline emphasis:
                    yield return new XElement(Xhtml + "em", RenderInlines(emphasis.Children, targets, footnotes));
                    break;
                case CodeInline code:
                    yield return new XElement(Xhtml + "code", code.Text);
                    break;
                case LinkInline link:
                    yield return new XElement(Xhtml + "a", new XAttribute("href", link.Url), RenderInlines(link.Label, targets, footnotes));
                    break;
                case MathInline math:
                    yield return new XElement(Xhtml + "span", new XAttribute("class", "math"), math.Text);
                    break;
                case CitationInline citation:
                {
                    var label = "[" + citation.Key + (string.IsNullOrWhiteSpace(citation.Locator) ? string.Empty : ", " + citation.Locator) + "]";
                    yield return new XElement(Xhtml + "span", new XAttribute("class", "citation"), label);
                    break;
                }
                case CrossReferenceInline reference when targets.TryGetValue(reference.Identifier, out var target):
                    yield return new XElement(Xhtml + "a", new XAttribute("class", "cross-reference"), new XAttribute("href", target.Href), target.Label);
                    break;
                case CrossReferenceInline reference:
                    yield return new XElement(Xhtml + "span", new XAttribute("class", "broken-reference"), reference.Identifier);
                    break;
                case FootnoteReferenceInline reference:
                {
                    if (!footnotes.Definitions.ContainsKey(reference.Identifier))
                    {
                        yield return new XElement(Xhtml + "sup", "[" + reference.Identifier + "]");
                        break;
                    }
                    if (!footnotes.Numbers.TryGetValue(reference.Identifier, out var number))
                    {
                        number = footnotes.Numbers.Count + 1;
                        footnotes.Numbers.Add(reference.Identifier, number);
                        footnotes.Order.Add(reference.Identifier);
                    }
                    footnotes.ReferenceCounter++;
                    var referenceId = $"fnref-{HtmlId(reference.Identifier)}-{footnotes.ReferenceCounter}";
                    if (!footnotes.FirstReferenceIds.ContainsKey(reference.Identifier))
                        footnotes.FirstReferenceIds.Add(reference.Identifier, referenceId);
                    yield return new XElement(Xhtml + "a",
                        new XAttribute(Epub + "type", "noteref"),
                        new XAttribute("id", referenceId),
                        new XAttribute("href", "#" + FootnoteId(reference.Identifier)),
                        number.ToString(CultureInfo.InvariantCulture));
                    break;
                }
            }
        }
    }

    private static string XhtmlDocument(BookProject project, string title, object bodyContent, string cssHref)
    {
        var language = string.IsNullOrWhiteSpace(project.Language) ? "en" : project.Language;
        var html = new XElement(Xhtml + "html",
            new XAttribute(XNamespace.Xmlns + "epub", Epub),
            new XAttribute(XNamespace.Xml + "lang", language),
            new XAttribute("lang", language),
            new XElement(Xhtml + "head",
                new XElement(Xhtml + "title", title),
                new XElement(Xhtml + "meta", new XAttribute("charset", "utf-8")),
                new XElement(Xhtml + "link", new XAttribute("rel", "stylesheet"), new XAttribute("type", "text/css"), new XAttribute("href", cssHref))),
            new XElement(Xhtml + "body", bodyContent));
        return Xml(html);
    }

    private static string Xml(XElement root)
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + root.ToString(SaveOptions.DisableFormatting);

    private static async Task WriteTextEntryAsync(
        ZipArchive archive,
        string path,
        string content,
        CompressionLevel compression,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(path.Replace('\\', '/'), compression);
        await using var stream = entry.Open();
        var bytes = Utf8NoBom.GetBytes(content);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static async Task CopyAssetAsync(ZipArchive archive, AssetPackage asset, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(asset.PackagePath, CompressionLevel.Optimal);
        await using var source = new FileStream(asset.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        await using var destination = entry.Open();
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static string ResolveProjectResource(string projectRoot, string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidOperationException("EPUB image source cannot be empty.");
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile)
            throw new InvalidOperationException($"EPUB export only packages project-local images; remote image '{source}' is not supported.");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        var candidate = Uri.TryCreate(source, UriKind.Absolute, out uri) && uri.IsFile
            ? Path.GetFullPath(uri.LocalPath)
            : Path.GetFullPath(Path.Combine(root, source.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) || relative.Equals("..", StringComparison.Ordinal) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"EPUB image '{source}' resolves outside the project and cannot be packaged.");
        return candidate;
    }

    private static string UniqueAssetPath(string fullPath, ISet<string> used)
    {
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        var stem = SanitizeFileName(Path.GetFileNameWithoutExtension(fullPath));
        if (stem.Length == 0) stem = "image";
        var candidate = $"assets/{stem}{extension}";
        var suffix = 2;
        while (!used.Add(candidate)) candidate = $"assets/{stem}-{suffix++}{extension}";
        return candidate;
    }

    private static string? ImageMediaType(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            _ => null
        };

    private static string CreateIdentifier(BookProject project, IReadOnlyList<EpubDocumentSource> documents)
    {
        var payload = new StringBuilder()
            .Append(project.Title).Append('\n')
            .Append(project.Author).Append('\n')
            .Append(project.Language).Append('\n');
        foreach (var document in documents) payload.Append(document.Node.PersistentId).Append('\n').Append(document.Content).Append('\n');
        var hash = SHA256.HashData(Utf8NoBom.GetBytes(payload.ToString()));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return "urn:uuid:" + new Guid(bytes).ToString("D");
    }

    private static string HtmlId(string value)
    {
        var builder = new StringBuilder(value.Length + 4);
        foreach (var character in value.Trim())
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or ':' ? character : '-');
        return builder.Length == 0 ? "section" : builder.ToString();
    }

    private static string FootnoteId(string identifier) => "fn-" + HtmlId(identifier);

    private static string CssToken(string value)
    {
        var token = new string(value.Where(static character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        return token.Length == 0 ? "text" : token;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (invalid.Contains(character) || char.IsWhiteSpace(character)) builder.Append('-');
            else if (char.IsLetterOrDigit(character) || character is '-' or '_' or '.') builder.Append(character);
        }
        return builder.ToString().Trim('-');
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record ParsedChapter(ProjectNode Node, DocumentAst Ast, string Title, int Number, string PackagePath);
    private sealed record CrossReferenceTarget(string Href, string Label);
    private sealed record AssetPackage(string FullPath, string PackagePath, string Id, string MediaType, bool IsCover);

    private sealed class AssetCollection(
        Dictionary<string, AssetPackage> byFullPath,
        Dictionary<string, AssetPackage> bySource,
        HashSet<string> usedPackagePaths)
    {
        public Dictionary<string, AssetPackage> ByFullPath { get; } = byFullPath;
        public Dictionary<string, AssetPackage> BySource { get; } = bySource;
        public HashSet<string> UsedPackagePaths { get; } = usedPackagePaths;
    }

    private sealed class FootnoteRenderState(IReadOnlyDictionary<string, FootnoteDefinitionBlock> definitions)
    {
        public IReadOnlyDictionary<string, FootnoteDefinitionBlock> Definitions { get; } = definitions;
        public Dictionary<string, int> Numbers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> FirstReferenceIds { get; } = new(StringComparer.Ordinal);
        public List<string> Order { get; } = [];
        public int ReferenceCounter { get; set; }
    }

    private const string BookCss = """
        html { color-scheme: light dark; }
        body { font-family: serif; line-height: 1.55; margin: 5%; orphans: 2; widows: 2; }
        .chapter { max-width: 42em; margin: 0 auto; }
        h1, h2, h3, h4, h5, h6 { line-height: 1.2; break-after: avoid; }
        h1 { margin-top: 1.6em; }
        p { margin: 0 0 0.9em; }
        blockquote { margin: 1em 1.5em; font-style: italic; }
        figure { margin: 1.5em auto; text-align: center; break-inside: avoid; }
        figure img, .cover img { max-width: 100%; height: auto; }
        figcaption { font-size: 0.9em; margin-top: 0.5em; }
        .cover { display: flex; min-height: 90vh; align-items: center; justify-content: center; text-align: center; }
        table { border-collapse: collapse; width: 100%; margin: 1.25em 0; }
        th, td { border: 1px solid currentColor; padding: 0.35em 0.5em; vertical-align: top; }
        caption { font-weight: bold; margin-bottom: 0.5em; }
        .align-left { text-align: left; } .align-center { text-align: center; } .align-right { text-align: right; }
        pre { white-space: pre-wrap; overflow-wrap: anywhere; padding: 0.75em; }
        code { font-family: monospace; }
        .display-math { text-align: center; margin: 1em 0; }
        .citation, .cross-reference { white-space: normal; }
        .footnotes { font-size: 0.9em; margin-top: 2em; }
        .footnotes aside { margin: 0.5em 0; }
        .footnote-back { text-decoration: none; }
        nav ol { padding-left: 1.4em; }
        a { text-decoration: underline; }
        """;
}
