using Avalonia.Media.Imaging;
using PDFtoImage;
using SkiaSharp;

namespace Typescribe.Desktop;

public sealed class PdfPreviewRenderer
{
    private const int RenderWidth = 1100;

    public Task<PdfPreviewPage> RenderAsync(
        string pdfPath,
        int requestedPage,
        CancellationToken cancellationToken = default)
        => Task.Run(() => RenderCore(pdfPath, requestedPage, cancellationToken), cancellationToken);

    private static PdfPreviewPage RenderCore(string pdfPath, int requestedPage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(
            pdfPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            128 * 1024,
            FileOptions.SequentialScan);

        var pageCount = Conversion.GetPageCount(stream, leaveOpen: true);
        if (pageCount <= 0) throw new InvalidOperationException("The generated PDF contains no pages.");

        var page = Math.Clamp(requestedPage, 0, pageCount - 1);
        stream.Position = 0;
        using var rendered = Conversion.ToImage(
            stream,
            page,
            leaveOpen: true,
            options: new RenderOptions(Width: RenderWidth, WithAspectRatio: true));

        cancellationToken.ThrowIfCancellationRequested();
        using var image = SKImage.FromBitmap(rendered);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Could not encode the rendered PDF page.");
        using var memory = new MemoryStream(encoded.ToArray(), writable: false);
        var bitmap = new Bitmap(memory);
        return new PdfPreviewPage(bitmap, page, pageCount);
    }
}

public sealed record PdfPreviewPage(Bitmap Bitmap, int PageIndex, int PageCount) : IDisposable
{
    public void Dispose() => Bitmap.Dispose();
}
