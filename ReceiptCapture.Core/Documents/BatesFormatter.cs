using System.Globalization;

namespace ReceiptCapture.Core.Documents;

/// <summary>
/// Builds Bates labels such as "SMITH-000123". Bates numbers are the running page IDs
/// law firms stamp on every page of a production.
/// </summary>
public sealed record BatesFormatter(string Prefix = "", string Suffix = "", int Digits = 6)
{
    public string Label(long number)
    {
        if (number < 0) throw new ArgumentOutOfRangeException(nameof(number), "Bates numbers can't be negative.");
        var digits = Math.Clamp(Digits, 1, 12);
        return Prefix + number.ToString("D" + digits, CultureInfo.InvariantCulture) + Suffix;
    }
}
