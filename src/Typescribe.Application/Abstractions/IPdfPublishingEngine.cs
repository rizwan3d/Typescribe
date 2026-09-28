namespace Typescribe.Application.Abstractions;

public interface IPdfPublishingEngine
{
    string Name { get; }
    bool IsAvailable { get; }
    Task EnsureAvailableAsync(CancellationToken cancellationToken = default);
    Task PublishAsync(string source, string outputPdfPath, CancellationToken cancellationToken = default);
}
