using Typescribe.Domain.Models;

namespace Typescribe.Application.Abstractions;

public interface IDocumentExportService
{
    bool CanPublishPdf { get; }
    string PublishingEngineName { get; }
    Task EnsurePdfEngineAsync(CancellationToken cancellationToken = default);
    Task ExportPdfPreviewAsync(string source, string title, BookStyle style, string destination, CancellationToken cancellationToken = default);
    Task ExportPdfAsync(string source, string title, BookStyle style, string destination, CancellationToken cancellationToken = default);
    Task ExportLatexAsync(string source, string title, BookStyle style, string destination, CancellationToken cancellationToken = default);
}
