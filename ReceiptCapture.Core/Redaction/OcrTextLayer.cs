using ReceiptCapture.Core.Pdf;

namespace ReceiptCapture.Core.Redaction;

/// <summary>
/// Builds the invisible text layer that makes a redacted PDF searchable again.
/// The words come from the OCR pass done before redacting, minus every word that touches a
/// blacked-out box, so nothing that was redacted can end up hidden in the text layer.
/// </summary>
public static class OcrTextLayer
{
    public static List<PdfTextWord> Build(ScannedPage page, IEnumerable<RedactionRegion> regionsOnThisPage)
    {
        var boxes = regionsOnThisPage.ToList();
        var result = new List<PdfTextWord>();
        if (page.PixelWidth < 1 || page.PixelHeight < 1) return result;

        double sx = page.WidthPoints / page.PixelWidth;
        double sy = page.HeightPoints / page.PixelHeight;

        foreach (var word in page.Words)
        {
            var text = Clean(word.Text);
            if (text.Length == 0 || word.Width <= 0 || word.Height <= 0) continue;
            if (boxes.Any(r => Overlaps(word, r))) continue;       // any contact at all: leave the word out

            result.Add(new PdfTextWord(text, word.X * sx, word.Y * sy, word.Width * sx, word.Height * sy));
        }
        return result;
    }

    /// <summary>The words of the layer, line by line: used to double-check nothing redacted slipped in.</summary>
    public static string PlainText(IEnumerable<PdfTextWord> words)
        => string.Join(" ", words.Select(w => w.Text));

    private static bool Overlaps(WordBox w, RedactionRegion r)
        => w.X < r.X + r.Width && r.X < w.X + w.Width
        && w.Y < r.Y + r.Height && r.Y < w.Y + w.Height;

    private static string Clean(string text)
        => new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
}
