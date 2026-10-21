using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using ReceiptCapture.Core.Redaction;

namespace ReceiptCapture.Services;

/// <summary>
/// Uses Windows' built-in PDF renderer and OCR engine (both fully offline) to turn each page into
/// an image plus a list of words with their positions.
/// </summary>
public sealed class WindowsRedactionService : IRedactionService
{
    // About 220 dpi for a Letter page, and safely under the OCR engine's 2600 px limit.
    private const double TargetLongSidePixels = 2400;

    private OcrEngine? _engine;

    private OcrEngine Engine => _engine ??= OcrEngine.TryCreateFromUserProfileLanguages()
        ?? throw new InvalidOperationException(
            "Windows has no OCR language installed. Add one under Settings > Time & language > Language & region.");

    public async Task<List<ScannedPage>> ScanAsync(string path, IProgress<string> progress, int maxPages)
    {
        var pdf = await LoadAsync(path);
        if (pdf.PageCount > maxPages)
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} has {pdf.PageCount} pages. This version handles up to {maxPages} pages at a time; split the file first (Documents tab) and redact the parts.");

        var pages = new List<ScannedPage>();
        for (uint i = 0; i < pdf.PageCount; i++)
        {
            progress.Report($"Reading page {i + 1} of {pdf.PageCount}…");
            var rendered = await RenderAsync(pdf, i);
            var words = await RecognizeWordsAsync(rendered.Png);
            pages.Add(rendered with { Words = words });
        }
        return pages;
    }

    public async Task<List<string>> ReadPageTextsAsync(string path, IProgress<string> progress)
    {
        var pdf = await LoadAsync(path);
        var texts = new List<string>();
        for (uint i = 0; i < pdf.PageCount; i++)
        {
            progress.Report($"page {i + 1} of {pdf.PageCount}…");
            var rendered = await RenderAsync(pdf, i);
            var words = await RecognizeWordsAsync(rendered.Png);
            texts.Add(string.Join("\n", words.GroupBy(w => w.Line).Select(line => string.Join(" ", line.Select(w => w.Text)))));
        }
        return texts;
    }

    public byte[] BurnToJpeg(ScannedPage page, IEnumerable<RedactionRegion> regions)
        => PageBurner.BurnToJpeg(page, regions);

    // ------------------------------------------------------------------

    private static async Task<PdfDocument> LoadAsync(string path)
    {
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            return await PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Couldn't open {Path.GetFileName(path)} ({ex.Message}). Password-protected or damaged PDFs aren't supported yet.", ex);
        }
    }

    private static async Task<ScannedPage> RenderAsync(PdfDocument pdf, uint index)
    {
        using PdfPage page = pdf.GetPage(index);

        var size = page.Size;   // in 1/96-inch units
        double scale = TargetLongSidePixels / Math.Max(size.Width, size.Height);
        uint width = (uint)Math.Max(1, Math.Round(size.Width * scale));
        uint height = (uint)Math.Max(1, Math.Round(size.Height * scale));

        var options = new PdfPageRenderOptions
        {
            DestinationWidth = width,
            DestinationHeight = height,
            BackgroundColor = new Windows.UI.Color { A = 255, R = 255, G = 255, B = 255 },
        };

        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, options);
        var png = await ReadAllBytesAsync(stream);

        // 1/96 inch -> points (1/72 inch)
        return new ScannedPage(png, (int)width, (int)height, size.Width * 0.75, size.Height * 0.75, Array.Empty<WordBox>());
    }

    private async Task<List<WordBox>> RecognizeWordsAsync(byte[] png)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);

        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        OcrResult result = await Engine.RecognizeAsync(bitmap);

        var words = new List<WordBox>();
        int lineIndex = 0;
        foreach (OcrLine line in result.Lines)
        {
            foreach (OcrWord word in line.Words)
            {
                var box = word.BoundingRect;
                words.Add(new WordBox(word.Text, box.X, box.Y, box.Width, box.Height, lineIndex));
            }
            lineIndex++;
        }
        return words;
    }

    private static async Task<byte[]> ReadAllBytesAsync(IRandomAccessStream stream)
    {
        var bytes = new byte[(int)stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
