using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

public sealed class DocumentExportService(IDocumentParser parser, IDocumentRenderer renderer, IPdfPublishingEngine publishingEngine) : IDocumentExportService
{
    public bool CanPublishPdf => publishingEngine.IsAvailable;
    public string PublishingEngineName => publishingEngine.Name;

    public Task EnsurePdfEngineAsync(CancellationToken cancellationToken = default)
        => publishingEngine.EnsureAvailableAsync(cancellationToken);

    public Task ExportPdfAsync(string source, string title, string destination, CancellationToken cancellationToken = default)
        => publishingEngine.PublishAsync(renderer.RenderTypst(parser.Parse(source), title), destination, cancellationToken);

    public Task ExportTypstAsync(string source, string title, string destination, CancellationToken cancellationToken = default)
        => AtomicFileWriter.WriteTextAsync(destination, renderer.RenderTypst(parser.Parse(source), title), cancellationToken);

    public Task ExportLatexAsync(string source, string title, string destination, CancellationToken cancellationToken = default)
        => AtomicFileWriter.WriteTextAsync(destination, renderer.RenderLatex(parser.Parse(source), title), cancellationToken);
}
