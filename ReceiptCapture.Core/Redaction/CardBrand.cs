namespace ReceiptCapture.Core.Redaction;

/// <summary>Works out the card brand from the leading digits of a card number (used to label findings in reports).</summary>
public static class CardBrand
{
    /// <summary>Returns the brand name, or null if the number doesn't match a brand we know.</summary>
    public static string? Detect(string digitsOnly)
    {
        var d = new string(digitsOnly.Where(char.IsDigit).ToArray());
        int len = d.Length;
        if (len < 13) return null;

        int P(int n) => int.Parse(d[..n]);    // the first n digits as a number

        if (d[0] == '4' && len is 13 or 16 or 19) return "Visa";
        if (len == 16 && ((P(2) is >= 51 and <= 55) || (P(4) is >= 2221 and <= 2720))) return "Mastercard";
        if (len == 15 && (P(2) == 34 || P(2) == 37)) return "American Express";
        if (len is >= 16 and <= 19 && (P(4) == 6011 || P(2) == 65 || P(3) is >= 644 and <= 649 || P(6) is >= 622126 and <= 622925))
            return "Discover";
        if (len is >= 14 and <= 19 && (P(3) is >= 300 and <= 305 || P(2) is 36 or 38 or 39)) return "Diners Club";
        if (len is >= 16 and <= 19 && P(4) is >= 3528 and <= 3589) return "JCB";
        if (len is >= 16 and <= 19 && P(2) == 62) return "UnionPay";
        return null;
    }
}
