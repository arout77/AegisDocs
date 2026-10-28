using System.Globalization;

namespace ReceiptCapture.Core;

/// <summary>Writes a size in bytes the way people read it: 12 KB, 3.4 MB, 1.20 GB.</summary>
public static class ByteSize
{
    public static string Format(long bytes)
    {
        if (bytes < 0) bytes = 0;
        var culture = CultureInfo.CurrentCulture;
        if (bytes < 1024) return string.Create(culture, $"{bytes} B");
        if (bytes < 1024L * 1024) return string.Create(culture, $"{bytes / 1024.0:0} KB");
        if (bytes < 1024L * 1024 * 1024) return string.Create(culture, $"{bytes / 1024.0 / 1024.0:0.0} MB");
        return string.Create(culture, $"{bytes / 1024.0 / 1024.0 / 1024.0:0.00} GB");
    }
}
