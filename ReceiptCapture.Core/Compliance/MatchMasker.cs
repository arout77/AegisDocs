using System.Text;
using ReceiptCapture.Core.Redaction;

namespace ReceiptCapture.Core.Compliance;

/// <summary>
/// Hides most of a found value before it is shown in a list or written to a report, so a scan report
/// never becomes a second copy of the sensitive data it found.
/// </summary>
public static class MatchMasker
{
    private const char Dot = '•';

    public static string Mask(SensitiveKind kind, string value)
    {
        value = value.Replace('\n', ' ');
        switch (kind)
        {
            case SensitiveKind.CardTrackData: return "[track data hidden]";
            case SensitiveKind.Address: return "[address hidden]";
            case SensitiveKind.CustomTerm: return "[your term]";
            case SensitiveKind.CardCvv: return new string(Dot, Math.Max(3, value.Count(char.IsDigit)));

            case SensitiveKind.Email: return MaskEmail(value);
            case SensitiveKind.Url: return MaskUrl(value);
            case SensitiveKind.IpAddress: return MaskIp(value);

            // Numbers that identify someone or something: keep only the last four characters.
            case SensitiveKind.Ssn:
            case SensitiveKind.Ein:
            case SensitiveKind.RoutingNumber:
            case SensitiveKind.BankAccount:
            case SensitiveKind.Passport:
            case SensitiveKind.DriversLicense:
            case SensitiveKind.Mrn:
            case SensitiveKind.HealthPlanId:
            case SensitiveKind.CreditCard:
            case SensitiveKind.Phone:
            case SensitiveKind.Fax:
                return KeepLastFour(value);

            // Dates, ages, ZIP codes and expiry dates: nothing is kept, only the shape.
            default: return HideAll(value);
        }
    }

    private static string KeepLastFour(string value)
    {
        int alnum = value.Count(char.IsLetterOrDigit);
        if (alnum <= 6) return HideAll(value);       // too short to show any of it safely

        int keepFrom = alnum - 4, seen = 0;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c)) sb.Append(c);
            else sb.Append(seen++ >= keepFrom ? c : Dot);
        }
        return sb.ToString();
    }

    private static string HideAll(string value)
        => new string(value.Select(c => char.IsLetterOrDigit(c) ? Dot : c).ToArray());

    private static string MaskEmail(string value)
    {
        int at = value.IndexOf('@');
        return at <= 0 ? HideAll(value) : value[0] + "•••" + value[at..];
    }

    private static string MaskUrl(string value)
    {
        var rest = value;
        int scheme = rest.IndexOf("://", StringComparison.Ordinal);
        var prefix = "";
        if (scheme >= 0)
        {
            prefix = rest[..(scheme + 3)];
            rest = rest[(scheme + 3)..];
        }
        int end = rest.IndexOfAny(new[] { '/', '?', '#' });
        var host = end < 0 ? rest : rest[..end];
        return end < 0 ? prefix + host : prefix + host + "/…";
    }

    private static string MaskIp(string value)
    {
        var parts = value.Split('.');
        return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.{Dot}{Dot}{Dot}.{Dot}{Dot}{Dot}" : "[IPv6 address hidden]";
    }
}
