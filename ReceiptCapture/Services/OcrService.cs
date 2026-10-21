using System.Text;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ReceiptCapture.Services;

public interface IOcrService
{
    Task<string> ExtractTextAsync(string path);
}

/// <summary>
/// Reads text from images and PDFs using the OCR engine built into Windows 10/11.
/// It runs fully offline: nothing is uploaded anywhere.
/// </summary>
public sealed class WindowsOcrService : IOcrService
{
    private const int MaxPdfPages = 5;

    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) || ImageExtensions.Contains(ext);
    }

    private OcrEngine? _engine;

    private OcrEngine Engine => _engine ??= OcrEngine.TryCreateFromUserProfileLanguages()
        ?? throw new InvalidOperationException(
            "Windows has no OCR language installed. Add one under Settings > Time & language > Language & region.");

    public async Task<string> ExtractTextAsync(string path)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);

        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return await ReadPdfAsync(file);

        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
        return await RecognizeAsync(stream);
    }

    private async Task<string> ReadPdfAsync(StorageFile file)
    {
        PdfDocument pdf = await PdfDocument.LoadFromFileAsync(file);
        var text = new StringBuilder();
        uint pages = Math.Min(pdf.PageCount, (uint)MaxPdfPages);

        for (uint i = 0; i < pages; i++)
        {
            using PdfPage page = pdf.GetPage(i);
            using var memory = new InMemoryRandomAccessStream();
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = 2000,
                BackgroundColor = Windows.UI.Colors.White,
            };
            await page.RenderToStreamAsync(memory, options);
            memory.Seek(0);
            text.AppendLine(await RecognizeAsync(memory));
        }
        return text.ToString();
    }

    private async Task<string> RecognizeAsync(IRandomAccessStream stream)
    {
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

        // The OCR engine refuses images larger than MaxImageDimension, so scale big photos down.
        double scale = Math.Min(1.0, (double)OcrEngine.MaxImageDimension / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Floor(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Floor(decoder.PixelHeight * scale)),
        };

        using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);

        OcrResult result = await Engine.RecognizeAsync(bitmap);
        return string.Join("\n", result.Lines.Select(line => line.Text));
    }
}
