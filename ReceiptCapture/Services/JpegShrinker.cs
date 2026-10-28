using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReceiptCapture.Core.Documents;

namespace ReceiptCapture.Services;

/// <summary>Shrinks one JPEG picture (as found inside a PDF) using Windows' own image code. Used by PDF compression.</summary>
public static class JpegShrinker
{
    public sealed record Result(byte[] Bytes, int Width, int Height);

    /// <summary>
    /// Scales the picture down if it is bigger than the settings allow and saves it again at the settings' quality.
    /// <paramref name="grayscale"/> says whether the PDF describes the picture as gray (otherwise colour).
    /// Returns null if the picture can't be read, so the caller leaves the original alone.
    /// </summary>
    public static Result? Reencode(byte[] jpeg, bool grayscale, ShrinkSettings settings)
    {
        try
        {
            using var input = new MemoryStream(jpeg);
            var decoder = new JpegBitmapDecoder(input,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];

            // The PDF says "RGB" or "gray", so the new picture must be exactly that.
            var wanted = grayscale ? PixelFormats.Gray8 : PixelFormats.Bgr24;
            if (frame.Format != wanted) frame = new FormatConvertedBitmap(frame, wanted, null, 0);

            var (width, height) = ImageShrink.ScaledSize(frame.PixelWidth, frame.PixelHeight, settings.MaxSide);
            if (width != frame.PixelWidth || height != frame.PixelHeight)
                frame = new TransformedBitmap(frame, new ScaleTransform((double)width / frame.PixelWidth, (double)height / frame.PixelHeight));

            var encoder = new JpegBitmapEncoder { QualityLevel = settings.JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(frame));

            using var output = new MemoryStream();
            encoder.Save(output);
            return new Result(output.ToArray(), frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException or ArgumentException
                                       or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            return null;
        }
    }
}
