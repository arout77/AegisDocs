using System.Globalization;
using System.Text;

namespace ReceiptCapture.Core.Export;

public static class CsvExporter
{
    public static void Export(IEnumerable<Receipt> receipts, string path)
    {
        // UTF-8 with BOM so Excel opens accented characters correctly.
        File.WriteAllText(path, Build(receipts), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static string Build(IEnumerable<Receipt> receipts)
    {
        var sb = new StringBuilder();
        sb.Append("Date,Vendor,Category,Tax,Total,Notes,File\r\n");

        foreach (var r in receipts)
        {
            sb.Append(string.Join(",", new[]
            {
                r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Text(r.Vendor),
                Text(r.Category),
                r.Tax?.ToString("0.00", CultureInfo.InvariantCulture) ?? "",
                r.Total.ToString("0.00", CultureInfo.InvariantCulture),
                Text(r.Notes),
                Text(r.FilePath ?? ""),
            }));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Text(string value)
    {
        // Spreadsheet apps run cells that start with = + - @ as formulas. OCR'd text is untrusted, so defuse it.
        if (value.Length > 0 && "=+-@\t\r".IndexOf(value[0]) >= 0)
            value = "'" + value;

        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
            value = "\"" + value.Replace("\"", "\"\"") + "\"";

        return value;
    }
}
