using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReceiptCapture.Core.Redaction;

namespace ReceiptCapture.Services;

/// <summary>
/// Paints solid black rectangles into the page image. The result is flat pixels: whatever was
/// underneath a box no longer exists anywhere in the output.
/// </summary>
public static class PageBurner
{
    public static byte[] BurnToJpeg(ScannedPage page, IEnumerable<RedactionRegion> regions)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(page.Png);
        image.EndInit();
        image.Freeze();

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            // White underneath, so a picture with transparent areas (a screenshot, say) doesn't turn black.
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, page.PixelWidth, page.PixelHeight));
            context.DrawImage(image, new Rect(0, 0, page.PixelWidth, page.PixelHeight));
            foreach (var region in regions)
                context.DrawRectangle(Brushes.Black, null, new Rect(region.X, region.Y, region.Width, region.Height));
        }

        // 96 dpi means one drawing unit = one pixel, so region coordinates line up exactly.
        var target = new RenderTargetBitmap(page.PixelWidth, page.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
