using System.Collections.Concurrent;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using ReceiptCapture.Core.Documents;
using ReceiptCapture.Core.Pdf;

namespace ReceiptCapture.Services;

/// <summary>
/// PDF operations (merge, split, stamp). Every method writes NEW files; the originals are never modified.
/// All the PDFsharp-specific code lives in this one file.
/// </summary>
public static class PdfService
{
    static PdfService()
    {
        // PDFsharp needs to be told where fonts live before it can draw any text.
        GlobalFontSettings.FontResolver = new WindowsFontResolver();
    }

    // Passwords the person typed this session, by file. Held in memory only, never written anywhere.
    private static readonly ConcurrentDictionary<string, string> Passwords = new(StringComparer.OrdinalIgnoreCase);

    public static void RememberPassword(string path, string password) => Passwords[Path.GetFullPath(path)] = password;

    public static void ForgetPassword(string path) => Passwords.TryRemove(Path.GetFullPath(path), out _);

    public static int GetPageCount(string path)
    {
        using var doc = OpenPdf(path, PdfDocumentOpenMode.InformationOnly);
        return doc.PageCount;
    }

    /// <summary>Joins the files, in the order given, into one PDF.</summary>
    public static int Merge(IReadOnlyList<string> inputs, string outputPath)
    {
        var opened = new List<PdfDocument>();
        try
        {
            using var output = new PdfDocument();
            foreach (var path in inputs)
            {
                var input = OpenPdf(path, PdfDocumentOpenMode.Import);
                opened.Add(input);                       // keep open until the output is saved
                foreach (PdfPage page in input.Pages)
                    output.AddPage(page);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            output.Save(outputPath);
            return output.PageCount;
        }
        finally
        {
            foreach (var doc in opened) doc.Dispose();
        }
    }

    /// <summary>Writes one PDF per group of 1-based page numbers. Returns the new file paths.</summary>
    public static List<string> Split(string input, IReadOnlyList<List<int>> groups, string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        var baseName = Path.GetFileNameWithoutExtension(input);
        var results = new List<string>();

        using var source = OpenPdf(input, PdfDocumentOpenMode.Import);
        foreach (var group in groups)
        {
            using var output = new PdfDocument();
            foreach (var pageNumber in group)
                output.AddPage(source.Pages[pageNumber - 1]);

            var first = group[0];
            var last = group[^1];
            var label = first == last ? $"p{first}" : $"p{first}-{last}";
            var target = UniquePath(outputFolder, $"{baseName}_{label}.pdf");
            output.Save(target);
            results.Add(target);
        }
        return results;
    }

    /// <summary>
    /// Stamps a Bates number at the bottom-right of every page. Numbering continues across files in the
    /// order given. Returns the output paths and the next unused number.
    /// </summary>
    public static (List<string> Outputs, long Next) StampBates(
        IReadOnlyList<string> inputs, string outputFolder, BatesFormatter format, long start)
    {
        Directory.CreateDirectory(outputFolder);
        var outputs = new List<string>();
        var font = new XFont("Arial", 10);
        long number = start;

        foreach (var path in inputs)
        {
            using var stampable = OpenForStamping(path);
            var doc = stampable.Document;
            foreach (PdfPage page in doc.Pages)
            {
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                var size = gfx.PageSize;
                // Anchored to the bottom-right corner with a half-inch margin.
                var area = new XRect(0, 0, size.Width - 36, size.Height - 24);
                gfx.DrawString(format.Label(number), font, XBrushes.Black, area, XStringFormats.BottomRight);
                number++;
            }

            var target = UniquePath(outputFolder, Path.GetFileName(path));
            doc.Save(target);
            outputs.Add(target);
        }
        return (outputs, number);
    }

    /// <summary>Draws light diagonal text (e.g. CONFIDENTIAL) across every page.</summary>
    public static List<string> Watermark(IReadOnlyList<string> inputs, string outputFolder, string text)
    {
        Directory.CreateDirectory(outputFolder);
        var outputs = new List<string>();
        var brush = new XSolidBrush(XColor.FromArgb(60, 128, 128, 128));

        foreach (var path in inputs)
        {
            using var stampable = OpenForStamping(path);
            var doc = stampable.Document;
            foreach (PdfPage page in doc.Pages)
            {
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                var size = gfx.PageSize;

                // Shrink the type for long text so it stays on the page.
                var emSize = Math.Clamp(size.Width / Math.Max(text.Length, 6) * 1.5, 20, 96);
                var font = new XFont("Arial", emSize);

                gfx.TranslateTransform(size.Width / 2, size.Height / 2);
                gfx.RotateTransform(-45);
                gfx.DrawString(text, font, brush,
                    new XRect(-size.Width / 2, -emSize, size.Width, emSize * 2), XStringFormats.Center);
            }

            var target = UniquePath(outputFolder, Path.GetFileName(path));
            doc.Save(target);
            outputs.Add(target);
        }
        return outputs;
    }

    // ------------------------------------------------------------------
    // Page tools: reorder / delete, rotate, compress. Each writes a NEW file.
    // ------------------------------------------------------------------

    /// <summary>
    /// Writes a copy with exactly the pages given (1-based), in the order given. Leaving a page out deletes it;
    /// listing a page twice repeats it.
    /// </summary>
    public static string ReorderPages(string input, IReadOnlyList<int> pages, string outputFolder, string label)
    {
        Directory.CreateDirectory(outputFolder);

        // A page can't be added to a document twice from the same opened source, so every repeat of a page comes from
        // its own fresh opening of the file.
        var sources = new List<PdfDocument>();
        try
        {
            var uses = new Dictionary<int, int>();
            using var output = new PdfDocument();

            foreach (var number in pages)
            {
                uses.TryGetValue(number, out int occurrence);
                uses[number] = occurrence + 1;

                while (sources.Count <= occurrence)
                    sources.Add(OpenPdf(input, PdfDocumentOpenMode.Import));

                output.AddPage(sources[occurrence].Pages[number - 1]);
            }

            var target = UniquePath(outputFolder, $"{Path.GetFileNameWithoutExtension(input)} ({label}).pdf");
            output.Save(target);
            return target;
        }
        finally
        {
            foreach (var source in sources) source.Dispose();
        }
    }

    /// <summary>Turns the given pages clockwise by <paramref name="degrees"/> (90, 180 or 270) and writes a copy.</summary>
    public static string RotatePages(string input, ISet<int> pages, int degrees, string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);

        using var stampable = OpenForStamping(input);
        var doc = stampable.Document;
        for (int i = 0; i < doc.PageCount; i++)
        {
            if (!pages.Contains(i + 1)) continue;
            var page = doc.Pages[i];
            page.Rotate = PageSelection.Rotated(page.Rotate, degrees);
        }

        var target = UniquePath(outputFolder, $"{Path.GetFileNameWithoutExtension(input)} (rotated).pdf");
        doc.Save(target);
        return target;
    }

    /// <summary>What compressing one PDF achieved. <see cref="OutputPath"/> is null when it couldn't be made meaningfully smaller (nothing is written then).</summary>
    public sealed record CompressOutcome(string? OutputPath, int PicturesShrunk, int PicturesLeftAlone, long BytesBefore, long BytesAfter);

    /// <summary>
    /// Makes a smaller copy by scaling down and re-saving the JPEG pictures inside the PDF. Only plain colour or gray JPEG pictures
    /// are touched; anything else (masks, ICC profiles, CMYK, text, vector drawings) is left exactly as it was.
    /// If the result isn't clearly smaller, no file is written.
    /// </summary>
    public static CompressOutcome Compress(string input, ShrinkSettings settings, string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        long before = new FileInfo(input).Length;

        using var stampable = OpenForStamping(input);
        var doc = stampable.Document;

        var seen = new HashSet<int>();      // a picture used on several pages is shrunk once
        int shrunk = 0, leftAlone = 0;

        foreach (PdfPage page in doc.Pages)
        {
            var xobjects = page.Resources.Elements.GetDictionary("/XObject");
            if (xobjects == null) continue;

            foreach (var item in xobjects.Elements.Values.ToList())
            {
                if (item is not PdfReference reference || reference.Value is not PdfDictionary image) continue;
                if (!seen.Add(reference.ObjectNumber)) continue;

                var result = TryShrinkPicture(image, settings);
                if (result == true) shrunk++;
                else if (result == false) leftAlone++;
            }
        }

        if (shrunk == 0) return new CompressOutcome(null, 0, leftAlone, before, before);

        doc.Options.CompressContentStreams = true;
        doc.Options.NoCompression = false;
        doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;

        var target = UniquePath(outputFolder, $"{Path.GetFileNameWithoutExtension(input)} (compressed).pdf");
        doc.Save(target);

        // Safety net: the new file must open, have every page, and really be smaller. Otherwise it is thrown away.
        long after = new FileInfo(target).Length;
        int expectedPages = doc.PageCount;
        bool good = after < before;
        if (good)
        {
            try
            {
                using var check = PdfReader.Open(target, PdfDocumentOpenMode.Import);
                good = check.PageCount == expectedPages;
            }
            catch (Exception)
            {
                good = false;
            }
        }

        if (!good)
        {
            File.Delete(target);
            return new CompressOutcome(null, 0, leftAlone + shrunk, before, before);
        }
        return new CompressOutcome(target, shrunk, leftAlone, before, after);
    }

    /// <summary>True: shrunk. False: a picture that was left alone. Null: not a picture at all.</summary>
    private static bool? TryShrinkPicture(PdfDictionary image, ShrinkSettings settings)
    {
        try
        {
            if (image.Elements.GetName("/Subtype") != "/Image") return null;

            // Only a plain, single-filter JPEG with an ordinary colour description is safe to redo.
            if (image.Elements.GetName("/Filter") != "/DCTDecode") return false;
            if (image.Elements.ContainsKey("/SMask") || image.Elements.ContainsKey("/Mask")
                || image.Elements.ContainsKey("/Decode") || image.Elements.ContainsKey("/ImageMask")) return false;

            var colourSpace = image.Elements.GetName("/ColorSpace");
            if (colourSpace != "/DeviceRGB" && colourSpace != "/DeviceGray") return false;
            if (image.Elements.GetInteger("/BitsPerComponent") != 8) return false;

            var original = image.Stream.Value;
            var shrunk = JpegShrinker.Reencode(original, grayscale: colourSpace == "/DeviceGray", settings);
            if (shrunk == null || !ImageShrink.WorthKeeping(original.Length, shrunk.Bytes.Length)) return false;

            image.Stream.Value = shrunk.Bytes;                 // also updates /Length
            image.Elements.SetInteger("/Width", shrunk.Width);
            image.Elements.SetInteger("/Height", shrunk.Height);
            return true;
        }
        catch (InvalidCastException)
        {
            // An entry written as a list or reference of a kind we don't handle (for example an ICC colour profile): leave the picture alone.
            return false;
        }
    }

    /// <summary>"name.pdf", or "name (2).pdf" etc. if that file already exists.</summary>
    public static string UniquePath(string folder, string fileName)
    {
        var target = Path.Combine(folder, fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int copy = 2; File.Exists(target); copy++)
            target = Path.Combine(folder, $"{baseName} ({copy}){ext}");
        return target;
    }

    private static PdfDocument OpenPdf(string path, PdfDocumentOpenMode mode)
    {
        Passwords.TryGetValue(Path.GetFullPath(path), out var password);

        PdfDocument? document;
        try
        {
            // PDFsharp calls the provider only when the file is protected and the password (if any) didn't
            // work. Setting Abort makes Open hand back null instead of throwing, which is how we can tell
            // "needs a password" from "damaged".
            document = PdfReader.Open(path, password, mode, args => args.Abort = true);
        }
        catch (Exception ex)
        {
            // Name the file so a failure in a batch of 200 is easy to track down.
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)}: couldn't open it ({ex.Message}). The file may be damaged.", ex);
        }

        if (document == null) throw new PdfPasswordException(path, wrongPassword: password != null);
        return document;
    }

    /// <summary>A document ready to be drawn on and saved, plus whatever must stay open until it is saved.</summary>
    private sealed class Stampable : IDisposable
    {
        private readonly PdfDocument? _source;

        public Stampable(PdfDocument document, PdfDocument? source)
        {
            Document = document;
            _source = source;
        }

        public PdfDocument Document { get; }

        public void Dispose()
        {
            Document.Dispose();
            _source?.Dispose();
        }
    }

    private static Stampable OpenForStamping(string path)
    {
        if (!PdfEncryptionProbe.IsEncrypted(path))
            return new Stampable(OpenPdf(path, PdfDocumentOpenMode.Modify), null);

        // PDFsharp edits a protected file in place only with its OWNER password. Copying the pages into a
        // fresh document works with the ordinary password (or none, for files that are merely restricted)
        // and writes an unprotected result.
        var source = OpenPdf(path, PdfDocumentOpenMode.Import);
        var copy = new PdfDocument();
        foreach (PdfPage page in source.Pages)
            copy.AddPage(page);
        return new Stampable(copy, source);
    }
}
