using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;
using Typescribe.Infrastructure.Services;

namespace Typescribe.Desktop;

/// <summary>
/// Desktop-facing adapter retaining the established service name while routing every existing
/// DOCX command through the multilingual rich interchange layer. Keeping the name local to the
/// Desktop namespace upgrades legacy call sites without duplicating menu wiring.
/// </summary>
internal sealed class DocxInterchangeService
{
    private readonly IDocumentParser _parser;
    private readonly RichDocxInterchangeService _inner;

    public DocxInterchangeService(IDocumentParser parser, BibTeXDatabase bibliography)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _inner = new RichDocxInterchangeService(_parser, bibliography);
    }

    public IReadOnlyList<string> LastWarnings => _inner.LastWarnings;

    public async Task ExportAsync(
        BookProject project,
        IReadOnlyList<(ProjectNode Node, string Content)> documents,
        string destination,
        CancellationToken cancellationToken = default)
    {
        await _inner.ExportAsync(project, documents, destination, cancellationToken);
        await MergedTableExportPostProcessor.ApplyDocxAsync(
            destination,
            documents.Select(static document => document.Content),
            _parser,
            cancellationToken);
        WriteWarningSidecar(destination, _inner.LastWarnings);
    }

    public Task<string> ImportAsync(
        BookProject project,
        string sourcePath,
        CancellationToken cancellationToken = default)
        => _inner.ImportAsync(project, sourcePath, cancellationToken);

    private static void WriteWarningSidecar(string destination, IReadOnlyList<string> warnings)
    {
        var path = destination + ".warnings.txt";
        if (warnings.Count == 0)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            return;
        }
        File.WriteAllLines(path,
        [
            "TypeScribe interchange warnings",
            "",
            .. warnings.Select(static warning => "- " + warning)
        ]);
    }
}

/// <summary>
/// Desktop-facing EPUB adapter. Existing publishing UI keeps using the same service-shaped API,
/// while rich language/direction/font metadata and the explicit font-embedding policy are applied.
/// </summary>
internal sealed class Epub3ExportService
{
    private readonly IDocumentParser _parser;
    private readonly RichEpub3ExportService _inner;

    public Epub3ExportService(IDocumentParser parser)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _inner = new RichEpub3ExportService(_parser);
    }

    public IReadOnlyList<string> LastWarnings => _inner.LastWarnings;

    public async Task ExportAsync(
        BookProject project,
        IReadOnlyList<EpubDocumentSource> documents,
        string destination,
        CancellationToken cancellationToken = default)
    {
        await _inner.ExportAsync(project, documents, destination, cancellationToken);
        await MergedTableExportPostProcessor.ApplyEpubAsync(
            destination,
            documents.Select(static document => document.Content),
            _parser,
            cancellationToken);
        WriteWarningSidecar(destination, _inner.LastWarnings);
    }

    private static void WriteWarningSidecar(string destination, IReadOnlyList<string> warnings)
    {
        var path = destination + ".warnings.txt";
        if (warnings.Count == 0)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            return;
        }
        File.WriteAllLines(path,
        [
            "TypeScribe interchange warnings",
            "",
            .. warnings.Select(static warning => "- " + warning)
        ]);
    }
}
