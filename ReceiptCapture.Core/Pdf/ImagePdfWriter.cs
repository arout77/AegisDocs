using System.Globalization;
using System.Text;

namespace ReceiptCapture.Core.Pdf;

/// <summary>One page of an image-only PDF: a JPEG drawn across a page of the given size.</summary>
public sealed record PdfPageImage(
    byte[] Jpeg, int PixelWidth, int PixelHeight, double WidthPoints, double HeightPoints, int Components = 3);

/// <summary>
/// Writes a minimal PDF whose pages are nothing but full-page JPEG images.
/// That's exactly what true redaction needs: no text layer, no fonts, no metadata, nothing
/// from the original file, so there is nothing left to recover from underneath a black box.
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

            Begin(pageObj);
            Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] " +
                 $"/Resources << /XObject << /Im0 {imageObj} 0 R >> /ProcSet [/PDF /ImageC /ImageB] >> " +
                 $"/Contents {contentObj} 0 R >>\n");
            End();

            var content = $"q {w} 0 0 {h} 0 0 cm /Im0 Do Q\n";
            Begin(contentObj);
            Text($"<< /Length {content.Length} >>\nstream\n{content}endstream\n");
            End();

            Begin(imageObj);
            var colorSpace = page.Components == 1 ? "/DeviceGray" : "/DeviceRGB";
            Text($"<< /Type /XObject /Subtype /Image /Width {page.PixelWidth} /Height {page.PixelHeight} " +
                 $"/ColorSpace {colorSpace} /BitsPerComponent 8 /Filter /DCTDecode /Length {page.Jpeg.Length} >>\nstream\n");
            file.Write(page.Jpeg);
            Text("\nendstream\n");
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

    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
