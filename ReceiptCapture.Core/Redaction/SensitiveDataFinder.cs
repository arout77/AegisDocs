using System.Text.RegularExpressions;

namespace ReceiptCapture.Core.Redaction;

public enum SensitiveKind { CustomTerm, CreditCard, Ssn, DateOfBirth, Email, Phone }

/// <summary>A span of text that looks sensitive.</summary>
public sealed record SensitiveMatch(SensitiveKind Kind, int Start, int Length)
{
    public int End => Start + Length;
}

public sealed class FindOptions
{
    public bool Ssn { get; init; } = true;
    public bool Phone { get; init; } = true;
    public bool Email { get; init; } = true;
    public bool CreditCard { get; init; } = true;
    public bool DateOfBirth { get; init; } = true;

    /// <summary>Words or phrases the user wants redacted wherever they appear (case-insensitive).</summary>
    public IReadOnlyList<string> CustomTerms { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Finds US-style sensitive data in plain text. It errs on the side of finding too much:
/// the user reviews every hit before anything is blacked out.
/// </summary>
public static class SensitiveDataFinder
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Regex SsnRx = new(
        @"(?<![\d-])(?<a>\d{3})[- ](?<g>\d{2})[- ](?<s>\d{4})(?![\d-])", Opts);

    private static readonly Regex PhoneRx = new(
        @"(?<!\d)(?:\+?1[ .-]?)?(?:\(\d{3}\)|\d{3})[ .-]?\d{3}[ .-]?\d{4}(?!\d)", Opts);

    private static readonly Regex EmailRx = new(
        @"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", Opts);

    private static readonly Regex CardRx = new(
        @"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)", Opts);

    private const string Month = @"(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?";

    // Only the date itself (group "d") is marked, not the "DOB:" label before it.
    private static readonly Regex DobRx = new(
        @"(?:\bDOB|\bD\.O\.B\.?|\bdate[ ]+of[ ]+birth|\bbirth[ ]?date|\bborn)(?:[ \t]+on)?[ \t]*[:\-]?[ \t]*" +
        @"(?<d>\d{1,2}[/\-.]\d{1,2}[/\-.]\d{2,4}" +
        $@"|{Month}[ ]+\d{{1,2}}(?:st|nd|rd|th)?,?[ ]+\d{{4}}" +
        $@"|\d{{1,2}}(?:st|nd|rd|th)?[ ]+{Month},?[ ]+\d{{4}})", Opts);

    public static List<SensitiveMatch> Find(string text, FindOptions options)
    {
        var all = new List<SensitiveMatch>();
        if (string.IsNullOrEmpty(text)) return all;

        if (options.Ssn)
        {
            foreach (Match m in SsnRx.Matches(text))
            {
                var area = m.Groups["a"].Value;
                if (area == "000" || area == "666" || area[0] == '9') continue;
                if (m.Groups["g"].Value == "00" || m.Groups["s"].Value == "0000") continue;
                all.Add(new SensitiveMatch(SensitiveKind.Ssn, m.Index, m.Length));
            }
        }

        if (options.CreditCard)
        {
            foreach (Match m in CardRx.Matches(text))
            {
                var digits = new string(m.Value.Where(char.IsDigit).ToArray());
                if (digits.Length is >= 13 and <= 19 && PassesLuhn(digits))
                    all.Add(new SensitiveMatch(SensitiveKind.CreditCard, m.Index, m.Length));
            }
        }

        if (options.Email)
            foreach (Match m in EmailRx.Matches(text))
                all.Add(new SensitiveMatch(SensitiveKind.Email, m.Index, m.Length));

        if (options.Phone)
            foreach (Match m in PhoneRx.Matches(text))
                all.Add(new SensitiveMatch(SensitiveKind.Phone, m.Index, m.Length));

        if (options.DateOfBirth)
            foreach (Match m in DobRx.Matches(text))
            {
                var d = m.Groups["d"];
                all.Add(new SensitiveMatch(SensitiveKind.DateOfBirth, d.Index, d.Length));
            }

        foreach (var term in options.CustomTerms)
        {
            var trimmed = term.Trim();
            if (trimmed.Length == 0) continue;

            // Any run of spaces in the term matches any whitespace in the text, including a line wrap.
            var pattern = string.Join(@"\s+", trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                                                    .Select(Regex.Escape));
            var rx = new Regex($@"(?<!\w){pattern}(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (Match m in rx.Matches(text))
                all.Add(new SensitiveMatch(SensitiveKind.CustomTerm, m.Index, m.Length));
        }

        // When two finds overlap (a card number also looks like a phone number), keep the more specific one.
        var accepted = new List<SensitiveMatch>();
        foreach (var m in all.OrderBy(x => x.Kind).ThenBy(x => x.Start))
            if (!accepted.Any(a => m.Start < a.End && a.Start < m.End))
                accepted.Add(m);

        return accepted.OrderBy(m => m.Start).ToList();
    }

    private static bool PassesLuhn(string digits)
    {
        int sum = 0;
        bool doubleIt = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            doubleIt = !doubleIt;
        }
        return sum % 10 == 0;
    }
}
