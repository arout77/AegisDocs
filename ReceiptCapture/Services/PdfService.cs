using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using ReceiptCapture.Core.Documents;

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
            using var doc = OpenPdf(path, PdfDocumentOpenMode.Modify);
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
            using var doc = OpenPdf(path, PdfDocumentOpenMode.Modify);
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
        try
        {
            return PdfReader.Open(path, mode);
        }
        catch (Exception ex)
        {
            // Name the file so a failure in a batch of 200 is easy to track down.
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)}: couldn't open it ({ex.Message}). Password-protected or damaged PDFs aren't supported yet.", ex);
        }
    }
}
