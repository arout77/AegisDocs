namespace ReceiptCapture.Core.Redaction;

/// <summary>Decides how big a picture's PDF page should be and how many pixels to read it at.</summary>
public static class ImagePageSizing
{
    private const double DefaultDpi = 150;          // used when a file doesn't say (or says something silly)
    private const double MaxLongSidePoints = 1008;  // 14 inches: anything bigger is shrunk onto a Letter-sized page
    private const double FitLongSidePoints = 792;   // 11 inches

    /// <summary>The size of the PDF page in points (1/72 inch) for a picture of this many pixels.</summary>
    public static (double WidthPoints, double HeightPoints) PageSize(int widthPixels, int heightPixels, double dpi)
    {
        if (widthPixels < 1 || heightPixels < 1) throw new ArgumentOutOfRangeException(nameof(widthPixels));

        double usable = dpi is >= 50 and <= 1200 ? dpi : DefaultDpi;
        double width = widthPixels / usable * 72;
        double height = heightPixels / usable * 72;

        // A phone photo reports 72 dpi, which would make a page the size of a billboard.
        double longSide = Math.Max(width, height);
        if (longSide > MaxLongSidePoints)
        {
            double shrink = FitLongSidePoints / longSide;
            width *= shrink;
            height *= shrink;
        }
        return (width, height);
    }

    /// <summary>The pixel size to read a picture at: never enlarged, and no side longer than the OCR engine allows.</summary>
    public static (int Width, int Height) PixelSize(int widthPixels, int heightPixels, int maxLongSide)
    {
        double scale = Math.Min(1.0, (double)maxLongSide / Math.Max(widthPixels, heightPixels));
        return (Math.Max(1, (int)Math.Round(widthPixels * scale)), Math.Max(1, (int)Math.Round(heightPixels * scale)));
    }
}
