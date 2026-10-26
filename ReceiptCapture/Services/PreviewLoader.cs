using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReceiptCapture.Core.Security;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ReceiptCapture.Services;

/// <summary>
/// Builds the thumbnail shown in the Receipts "Details" pane. Pictures are loaded directly;
/// for a PDF, the first page is drawn with the PDF renderer that is built into Windows.
/// </summary>
public static class PreviewLoader
{
    private const int PreviewWidth = 900;   // previews don't need full resolution

    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    /// <summary>Returns a ready-to-show image, or null if there is nothing to preview.</summary>
    public static async Task<ImageSource?> LoadAsync(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        string? temp = null;
        try
        {
            // A locked receipt is unlocked into a short-lived copy, just long enough to draw the preview.
            if (Vault.IsSealedPath(path))
            {
                var sealedPath = path;
                temp = await Task.Run(() => AppVault.OpenToTemp(sealedPath));
                path = temp;
            }

            var extension = Path.GetExtension(path);

            if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                return await LoadPdfFirstPageAsync(path);

            if (ImageExtensions.Contains(extension))
                return await Task.Run(() => LoadPicture(path));
        }
        catch (Exception)
        {
            // A preview is a nicety: if it can't be drawn, the pane just says "No preview".
        }
        finally
        {
            if (temp != null)
            {
                try { File.Delete(temp); }
                catch (IOException) { }               // still open: it is cleared when the program exits
                catch (UnauthorizedAccessException) { }
            }
        }

        return null;
    }

    private static ImageSource LoadPicture(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;   // read it all now so the file isn't left locked
        image.DecodePixelWidth = PreviewWidth;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static async Task<ImageSource?> LoadPdfFirstPageAsync(string path)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        PdfDocument pdf = await PdfDocument.LoadFromFileAsync(file);
        if (pdf.PageCount == 0) return null;

        using PdfPage page = pdf.GetPage(0);
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = PreviewWidth,
            BackgroundColor = new Windows.UI.Color { A = 255, R = 255, G = 255, B = 255 },   // opaque white paper
        };

        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, options);

        var bytes = new byte[(int)stream.Size];
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
