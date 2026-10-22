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

/// <summary>Turns PNG bytes (a rendered page) into an image for display.</summary>
public sealed class BytesToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] bytes || bytes.Length == 0) return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
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

/// <summary>
/// Makes a panel grow with the window: result = available height - reserved space, kept between a minimum and maximum.
/// ConverterParameter is "reserved|min|max", e.g. "410|200|900".
/// </summary>
public sealed class FillHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double reserved = 400, min = 200, max = 900;
        var parts = (parameter as string)?.Split('|');
        if (parts is { Length: 3 })
        {
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out reserved);
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out min);
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out max);
        }

        double available = value is double d && !double.IsNaN(d) ? d : 0;
        return Math.Max(min, Math.Min(max, available - reserved));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
