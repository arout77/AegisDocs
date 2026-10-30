using ReceiptCapture.Core.Pdf;
using ReceiptCapture.Core.Redaction;

namespace ReceiptCapture.Services;

/// <summary>
/// Rebuilds a PDF from pictures of its pages, which drops everything hidden in it. Same method the Compliance tab uses
/// for clean copies, without any questions asked: a PDF that needs a password fails with PdfPasswordException.
/// </summary>
public static class PdfRebuilder
{
    public static async Task RebuildAsync(
        IRedactionService service, string source, string destination, bool keepSearchable, int maxPages,
        IProgress<string> progress, CancellationToken cancel)
    {
        var pages = await service.ScanAsync(source, progress, maxPages, null);

        var images = new List<PdfPageImage>();
        var nothingCovered = new List<RedactionRegion>();
        for (int i = 0; i < pages.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            progress.Report($"rebuilding page {i + 1} of {pages.Count}…");
            await Task.Yield();
            var page = pages[i];
            var jpeg = service.BurnToJpeg(page, nothingCovered);
            var layer = keepSearchable ? OcrTextLayer.Build(page, nothingCovered) : null;
            images.Add(new PdfPageImage(jpeg, page.PixelWidth, page.PixelHeight, page.WidthPoints, page.HeightPoints, 3, layer));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        try
        {
            await Task.Run(() =>
            {
                using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
                ImagePdfWriter.Write(file, images);
            }, cancel);
        }
        catch
        {
            try { if (File.Exists(destination)) File.Delete(destination); } catch (IOException) { }
            throw;
        }
    }
}
