using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ReceiptCapture;

/// <summary>Turns a file path into an image for the preview box (images only; PDFs show "No preview").</summary>
public sealed class FilePathToImageConverter : IValueConverter
{
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path)) return null;
        if (!ImageExtensions.Contains(Path.GetExtension(path))) return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;   // read it all now so the file isn't left locked
            image.DecodePixelWidth = 900;                   // previews don't need full resolution
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
