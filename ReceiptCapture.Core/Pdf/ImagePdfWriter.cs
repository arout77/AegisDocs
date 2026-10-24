using System.Globalization;
using System.Text;

namespace ReceiptCapture.Core.Pdf;

/// <summary>
/// One page of an image-only PDF: a JPEG drawn across a page of the given size, optionally with an
/// invisible text layer on top (so the page can be searched and selected without changing how it looks).
/// </summary>
public sealed record PdfPageImage(
    byte[] Jpeg, int PixelWidth, int PixelHeight, double WidthPoints, double HeightPoints, int Components = 3,
    IReadOnlyList<PdfTextWord>? Text = null);

/// <summary>
/// Writes a minimal PDF whose pages are nothing but full-page JPEG images, plus (only if asked) an
/// invisible text layer. True redaction needs nothing from the original file: no fonts, no metadata,
/// no original text, so there is nothing left to recover from underneath a black box. The optional
/// text layer is built by the caller from words that are NOT covered by any box.
/// </summary>
public static class ImagePdfWriter
{
    public static void Write(Stream output, IReadOnlyList<PdfPageImage> pages)
    {
        if (pages.Count == 0) throw new ArgumentException("A PDF needs at least one page.", nameof(pages));
        foreach (var p in pages)
        {
            if (p.Jpeg.Length == 0) throw new ArgumentException("A page image is empty.", nameof(pages));
            if (p.PixelWidth < 1 || p.PixelHeight < 1 || p.WidthPoints <= 0 || p.HeightPoints <= 0)
                throw new ArgumentException("A page has an invalid size.", nameof(pages));
            if (p.Components is not (1 or 3))
                throw new ArgumentException("JPEG images must be grayscale (1) or RGB (3).", nameof(pages));
        }

        using var file = new MemoryStream();
        var offsets = new List<long>();      // offsets[n - 1] = where object n starts

        bool anyText = pages.Any(p => p.Text is { Count: > 0 });
        int fontObj = 3 + 3 * pages.Count;   // only written when some page has a text layer

        void Text(string s) => file.Write(Encoding.ASCII.GetBytes(s));
        void Begin(int number) { offsets.Add(file.Position); Text($"{number} 0 obj\n"); }
        void End() => Text("endobj\n");

        Text("%PDF-1.4\n");
        file.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });   // marks the file as binary

        // Object layout: 1 = catalog, 2 = page tree, then three objects per page (page, content, image).
        Begin(1);
        Text("<< /Type /Catalog /Pages 2 0 R >>\n");
        End();

        Begin(2);
        var kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => $"{3 + 3 * i} 0 R"));
        Text($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>\n");
        End();

        for (int i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            int pageObj = 3 + 3 * i, contentObj = pageObj + 1, imageObj = pageObj + 2;
            string w = Num(page.WidthPoints), h = Num(page.HeightPoints);

            bool pageHasText = page.Text is { Count: > 0 };

            Begin(pageObj);
            var fontResource = pageHasText ? $"/Font << /F0 {fontObj} 0 R >> " : "";
            Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] " +
                 $"/Resources << /XObject << /Im0 {imageObj} 0 R >> {fontResource}/ProcSet [/PDF /Text /ImageC /ImageB] >> " +
                 $"/Contents {contentObj} 0 R >>\n");
            End();

            var content = BuildContent(page, w, h);
            Begin(contentObj);
            Text($"<< /Length {content.Length} >>\nstream\n");
            file.Write(content);
            Text("endstream\n");
            End();

            Begin(imageObj);
            var colorSpace = page.Components == 1 ? "/DeviceGray" : "/DeviceRGB";
            Text($"<< /Type /XObject /Subtype /Image /Width {page.PixelWidth} /Height {page.PixelHeight} " +
                 $"/ColorSpace {colorSpace} /BitsPerComponent 8 /Filter /DCTDecode /Length {page.Jpeg.Length} >>\nstream\n");
            file.Write(page.Jpeg);
            Text("\nendstream\n");
            End();
        }

        if (anyText)
        {
            // Courier is one of the 14 standard fonts every viewer has built in, and every character is
            // exactly 0.6 em wide, which lets us stretch each word to precisely the width OCR measured.
            Begin(fontObj);
            Text("<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>\n");
            End();
        }

        // Cross-reference table: each entry is exactly 20 bytes.
        long xrefPosition = file.Position;
        int size = offsets.Count + 1;
        Text($"xref\n0 {size}\n");
        Text("0000000000 65535 f \n");
        foreach (var offset in offsets)
            Text($"{offset:D10} 00000 n \n");

        Text($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xrefPosition}\n%%EOF\n");

        file.Position = 0;
        file.CopyTo(output);
    }

    private const double CourierAdvance = 0.6;     // width of every Courier character, in em

    /// <summary>The page's drawing instructions: the picture, then the invisible words (text render mode 3).</summary>
    private static byte[] BuildContent(PdfPageImage page, string w, string h)
    {
        using var content = new MemoryStream();
        void Raw(string s) => content.Write(Encoding.ASCII.GetBytes(s));

        Raw($"q {w} 0 0 {h} 0 0 cm /Im0 Do Q\n");

        foreach (var word in page.Text ?? Array.Empty<PdfTextWord>())
        {
            var bytes = TextEncoding.GetBytes(word.Text);
            if (bytes.Length == 0 || word.Width <= 0 || word.Height <= 0) continue;

            double size = Math.Max(1, word.Height * 1.2);
            double natural = bytes.Length * CourierAdvance * size;
            double stretch = Math.Clamp(word.Width / natural * 100, 1, 10000);          // Tz is a percentage
            double baseline = page.HeightPoints - (word.Top + word.Height * 0.8);        // flip to PDF's bottom-left origin

            Raw($"BT 3 Tr /F0 {Num(size)} Tf {Num(stretch)} Tz 1 0 0 1 {Num(word.Left)} {Num(baseline)} Tm (");
            foreach (var b in bytes)
            {
                if (b is (byte)'(' or (byte)')' or (byte)'\\') { content.WriteByte((byte)'\\'); content.WriteByte(b); }
                else if (b < 32 || b > 126) Raw("\\" + Convert.ToString(b, 8).PadLeft(3, '0'));
                else content.WriteByte(b);
            }
            Raw(") Tj ET\n");
        }
        return content.ToArray();
    }

    // Windows-1252 is what /WinAnsiEncoding means. Letters it can't represent become '?'.
    private static readonly Encoding TextEncoding = CreateTextEncoding();

    private static Encoding CreateTextEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
