namespace Typescribe.Application.Abstractions;

public interface IDocumentExportService
{
    bool CanPublishPdf { get; }
    string PublishingEngineName { get; }
    Task ExportPdfAsync(string source, string title, string destination, CancellationToken cancellationToken = default);
    Task ExportTypstAsync(string source, string title, string destination, CancellationToken cancellationToken = default);
    Task ExportLatexAsync(string source, string title, string destination, CancellationToken cancellationToken = default);
}
