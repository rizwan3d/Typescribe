namespace Typescribe.Application.Abstractions;

public interface IPdfPublishingEngine
{
    string Name { get; }
    bool IsAvailable { get; }
    Task PublishAsync(string typstSource, string outputPdfPath, CancellationToken cancellationToken = default);
}
