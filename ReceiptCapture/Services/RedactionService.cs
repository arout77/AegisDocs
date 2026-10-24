using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using ReceiptCapture.Core.Pdf;
using ReceiptCapture.Core.Redaction;

namespace ReceiptCapture.Services;

/// <summary>
/// Uses Windows' built-in PDF renderer and OCR engine (both fully offline) to turn each page into
/// an image plus a list of words with their positions. PDFs are read directly; Word documents are
/// first converted to PDF; pictures become one page each.
/// </summary>
public sealed class WindowsRedactionService : IRedactionService
{
    // About 220 dpi for a Letter page, and safely under the OCR engine's 2600 px limit.
    private const double TargetLongSidePixels = 2400;

    private OcrEngine? _engine;

    private OcrEngine Engine => _engine ??= OcrEngine.TryCreateFromUserProfileLanguages()
        ?? throw new InvalidOperationException(
            "Windows has no OCR language installed. Add one under Settings > Time & language > Language & region.");

    public async Task<List<ScannedPage>> ScanAsync(string path, IProgress<string> progress, int maxPages, string? password = null)
    {
        switch (InputFile.Classify(path))
        {
            case InputKind.Pdf:
                return await ScanPdfAsync(await LoadAsync(path, password), Path.GetFileName(path), progress, maxPages);
            case InputKind.Word:
                return await ScanWordAsync(path, progress, maxPages);
            case InputKind.Image:
                return await ScanImageAsync(path, progress, maxPages);
            default:
                throw new InvalidOperationException($"{Path.GetFileName(path)}: choose {InputFile.SupportedText}.");
        }
    }

    public async Task<List<string>> ReadPageTextsAsync(string path, IProgress<string> progress)
    {
        var pdf = await LoadAsync(path, null);
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
    // PDFs
    // ------------------------------------------------------------------

    private async Task<List<ScannedPage>> ScanPdfAsync(PdfDocument pdf, string name, IProgress<string> progress, int maxPages)
    {
        if (pdf.PageCount > maxPages)
            throw new InvalidOperationException(
                $"{name} has {pdf.PageCount} pages. This version handles up to {maxPages} pages at a time; split the file first (Documents tab) and redact the parts.");

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

    private static async Task<PdfDocument> LoadAsync(string path, string? password)
    {
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            return password == null
                ? await PdfDocument.LoadFromFileAsync(file)
                : await PdfDocument.LoadFromFileAsync(file, password);
        }
        catch (Exception ex)
        {
            // Windows only says "couldn't open it". An /Encrypt entry in the file tells us it's a password, not damage.
            if (PdfEncryptionProbe.IsEncrypted(path))
                throw new PdfPasswordException(path, wrongPassword: password != null);

            throw new InvalidOperationException(
                $"Couldn't open {Path.GetFileName(path)} ({ex.Message}). The file may be damaged.", ex);
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

    // ------------------------------------------------------------------
    // Word documents: converted to PDF first, entirely on this PC
    // ------------------------------------------------------------------

    private async Task<List<ScannedPage>> ScanWordAsync(string path, IProgress<string> progress, int maxPages)
    {
        progress.Report("Converting the Word document to a PDF…");

        // The converted copy holds the whole unredacted document, so it lives in a private temp folder
        // and is deleted as soon as it has been read into memory.
        var tempFolder = Path.Combine(Path.GetTempPath(), "AegisDocs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);

        byte[] pdfBytes;
        try
        {
            var converted = await DocumentConverter.ToPdfAsync(path, tempFolder);
            pdfBytes = await File.ReadAllBytesAsync(converted);
        }
        finally
        {
            try { Directory.Delete(tempFolder, recursive: true); }
            catch (Exception) { /* leave nothing behind if we can; never fail the job over it */ }
        }

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(pdfBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);

        PdfDocument pdf;
        try { pdf = await PdfDocument.LoadFromStreamAsync(stream); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} was converted, but the result couldn't be read ({ex.Message}).", ex);
        }
        return await ScanPdfAsync(pdf, Path.GetFileName(path), progress, maxPages);
    }

    // ------------------------------------------------------------------
    // Pictures: each one (or each frame of a multi-page TIFF) becomes a page
    // ------------------------------------------------------------------

    private async Task<List<ScannedPage>> ScanImageAsync(string path, IProgress<string> progress, int maxPages)
    {
        var name = Path.GetFileName(path);
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);

        BitmapDecoder decoder;
        try { decoder = await BitmapDecoder.CreateAsync(stream); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Couldn't open {name} as a picture ({ex.Message}).", ex);
        }

        // Only TIFFs are really multi-page; for a GIF or anything else, the first frame is the picture.
        uint frameCount = Path.GetExtension(path).StartsWith(".tif", StringComparison.OrdinalIgnoreCase)
            ? decoder.FrameCount
            : 1;
        if (frameCount > maxPages)
            throw new InvalidOperationException(
                $"{name} has {frameCount} pages. This version handles up to {maxPages} pages at a time.");

        var pages = new List<ScannedPage>();
        for (uint i = 0; i < frameCount; i++)
        {
            progress.Report(frameCount == 1 ? "Reading the picture…" : $"Reading page {i + 1} of {frameCount}…");

            BitmapFrame frame = await decoder.GetFrameAsync(i);
            int widthPx = (int)frame.OrientedPixelWidth;
            int heightPx = (int)frame.OrientedPixelHeight;
            var (pageW, pageH) = ImagePageSizing.PageSize(widthPx, heightPx, frame.DpiX);
            var (readW, readH) = ImagePageSizing.PixelSize(widthPx, heightPx, (int)TargetLongSidePixels);

            // Turn it upright (phone photos are often stored sideways), then shrink while saving as PNG if it's huge.
            using SoftwareBitmap bitmap = await frame.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            byte[] png;
            using (var output = new InMemoryRandomAccessStream())
            {
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                encoder.SetSoftwareBitmap(bitmap);
                if (readW != bitmap.PixelWidth || readH != bitmap.PixelHeight)
                {
                    encoder.BitmapTransform.ScaledWidth = (uint)readW;
                    encoder.BitmapTransform.ScaledHeight = (uint)readH;
                    encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
                }
                await encoder.FlushAsync();
                png = await ReadAllBytesAsync(output);
            }

            var words = await RecognizeWordsAsync(png);
            pages.Add(new ScannedPage(png, readW, readH, pageW, pageH, words));
        }
        return pages;
    }

    // ------------------------------------------------------------------
    // OCR
    // ------------------------------------------------------------------

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
