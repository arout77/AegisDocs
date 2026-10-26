using System.Globalization;

namespace ReceiptCapture.Core.Updates;

/// <summary>Reading and comparing version numbers such as "0.2.0" or "v1.4.2.1".</summary>
public static class AppVersion
{
    /// <summary>
    /// Accepts two to four whole numbers separated by dots, with an optional leading "v". The result always has four
    /// parts, so "1.2" and "1.2.0.0" are equal. Anything else (including "1.2.0-beta") is rejected on purpose:
    /// releases are plain numbers.
    /// </summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (trimmed.Length > 1 && (trimmed[0] == 'v' || trimmed[0] == 'V')) trimmed = trimmed[1..];

        var parts = trimmed.Split('.');
        if (parts.Length < 2 || parts.Length > 4) return false;

        var numbers = new int[4];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || parts[i].Length > 5 || !parts[i].All(char.IsAsciiDigit)) return false;
            numbers[i] = int.Parse(parts[i], CultureInfo.InvariantCulture);
        }

        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    /// <summary>True if <paramref name="candidate"/> is a higher version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string candidate, string current)
        => TryParse(candidate, out var a) && TryParse(current, out var b) && a > b;
}
