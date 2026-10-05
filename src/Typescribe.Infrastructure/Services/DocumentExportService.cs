using System.Text;
using Typescribe.Application.Abstractions;
using Typescribe.Domain.Models;

namespace Typescribe.Infrastructure.Services;

public sealed class DocumentExportService(
    IDocumentParser parser,
    IDocumentRenderer renderer,
    IPdfPublishingEngine publishingEngine) : IDocumentExportService
{
    public bool CanPublishPdf => publishingEngine.IsAvailable;
    public string PublishingEngineName => publishingEngine.Name;

    public Task EnsurePdfEngineAsync(CancellationToken cancellationToken = default)
        => publishingEngine.EnsureAvailableAsync(cancellationToken);

    public Task ExportPdfPreviewAsync(
        string source,
        string title,
        BookStyle style,
        string destination,
        CancellationToken cancellationToken = default)
    {
        var latex = renderer.RenderLatex(parser.Parse(source), title, style);
        return publishingEngine.PublishAsync(
            BuildEditorPreviewLatex(latex),
            destination,
            passes: 1,
            cancellationToken);
    }

    public Task ExportPdfAsync(
        string source,
        string title,
        BookStyle style,
        string destination,
        CancellationToken cancellationToken = default)
        => publishingEngine.PublishAsync(
            renderer.RenderLatex(parser.Parse(source), title, style),
            destination,
            passes: 2,
            cancellationToken);

    public Task ExportLatexAsync(
        string source,
        string title,
        BookStyle style,
        string destination,
        CancellationToken cancellationToken = default)
        => AtomicFileWriter.WriteTextAsync(destination, renderer.RenderLatex(parser.Parse(source), title, style), cancellationToken);

    private static string BuildEditorPreviewLatex(string latex)
    {
        // Live preview is an editor surface, not the publication artifact. Keep the exact page
        // geometry, typography and manuscript rendering while removing publication-only front
        // matter. Also collapse book-class recto/verso clearing so the preview contains only
        // pages required by the selected editor content.
        var normalized = (latex ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var output = new StringBuilder(normalized.Length + 64);
        var skipTocClearPage = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.Equals(trimmed, "\\maketitle", StringComparison.Ordinal))
                continue;

            if (string.Equals(trimmed, "\\tableofcontents", StringComparison.Ordinal))
            {
                skipTocClearPage = true;
                continue;
            }

            if (skipTocClearPage && string.Equals(trimmed, "\\cleardoublepage", StringComparison.Ordinal))
            {
                skipTocClearPage = false;
                continue;
            }

            skipTocClearPage = false;
            output.AppendLine(line);
            if (string.Equals(trimmed, "\\begin{document}", StringComparison.Ordinal))
                output.AppendLine("\\let\\cleardoublepage\\clearpage");
        }

        return output.ToString();
    }
}