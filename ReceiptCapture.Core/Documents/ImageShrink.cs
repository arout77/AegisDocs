namespace ReceiptCapture.Core.Documents;

public enum CompressLevel { Smaller, Smallest }

/// <summary>How hard to squeeze the pictures inside a PDF.</summary>
/// <param name="MaxSide">No picture is kept larger than this many pixels on its longest side.</param>
/// <param name="JpegQuality">1 to 100. Lower is smaller and blurrier.</param>
public sealed record ShrinkSettings(int MaxSide, int JpegQuality);

/// <summary>The decisions behind PDF compression, kept apart from the PDF code so they can be tested.</summary>
public static class ImageShrink
{
    public static ShrinkSettings For(CompressLevel level) => level switch
    {
        // About 150 dots per inch on a letter page at "Smaller"; about 100 at "Smallest".
        CompressLevel.Smallest => new ShrinkSettings(MaxSide: 1100, JpegQuality: 55),
        _ => new ShrinkSettings(MaxSide: 1650, JpegQuality: 75),
    };

    /// <summary>The size a picture should be scaled to: the same shape, never bigger than it is, longest side at most <paramref name="maxSide"/>.</summary>
    public static (int Width, int Height) ScaledSize(int width, int height, int maxSide)
    {
        if (width < 1 || height < 1) return (Math.Max(1, width), Math.Max(1, height));
        int longest = Math.Max(width, height);
        if (longest <= maxSide) return (width, height);

        double scale = (double)maxSide / longest;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>A re-encoded picture replaces the original only if it is meaningfully smaller (at least 10% less).</summary>
    public static bool WorthKeeping(long originalBytes, long newBytes) => newBytes > 0 && newBytes <= originalBytes * 0.9;

    /// <summary>"Saved 4.2 MB (61%)" or "No saving".</summary>
    public static string DescribeSaving(long before, long after)
    {
        if (before <= 0 || after >= before) return "No saving";
        double percent = (before - after) * 100.0 / before;
        return $"Saved {ByteSize.Format(before - after)} ({percent:0}%)";
    }
}
