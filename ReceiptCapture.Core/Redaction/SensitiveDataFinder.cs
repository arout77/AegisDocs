using System.Text.RegularExpressions;

namespace ReceiptCapture.Core.Redaction;

/// <summary>Declaration order is also the priority when two finds overlap: earlier kinds win.</summary>
public enum SensitiveKind
{
    CustomTerm, CreditCard, Ssn, Ein, RoutingNumber, BankAccount, Passport, DriversLicense,
    DateOfBirth, Email, Phone, Address,
}

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
    public bool Ein { get; init; } = true;
    public bool BankAccount { get; init; } = true;
    public bool RoutingNumber { get; init; } = true;
    public bool DriversLicense { get; init; } = true;
    public bool Passport { get; init; } = true;
    public bool Address { get; init; } = true;

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


    // ---- Tax / bank / ID numbers -------------------------------------------------------------
    // Most of these have no reliable shape of their own (a routing number is just nine digits), so they are
    // found by the label printed next to them. Only the number is marked, not the label.

    // IRS campus prefixes: an EIN that starts with anything else can't be real.
    private static readonly HashSet<string> EinPrefixes = new((
        "01 02 03 04 05 06 10 11 12 13 14 15 16 20 21 22 23 24 25 26 27 30 31 32 33 34 35 36 37 38 39 " +
        "40 41 42 43 44 45 46 47 48 50 51 52 53 54 55 56 57 58 59 60 61 62 63 64 65 66 67 68 " +
        "71 72 73 74 75 76 77 80 81 82 83 84 85 86 87 88 90 91 92 93 94 95 98 99").Split(' '));

    private static readonly Regex EinRx = new(@"(?<![\d-])(?<n>\d{2}-\d{7})(?![\d-])", Opts);

    private static readonly Regex EinLabelledRx = new(
        @"(?:\bEIN\b|\bTIN\b|employer[ ]+identification(?:[ ]+(?:number|no\.?|num\.?))?" +
        @"|federal[ ]+(?:tax[ ]+)?(?:id|identification)|tax[ ]+id(?:entification)?(?:[ ]+(?:number|no\.?))?)" +
        @"[ \t]*(?:number|no\.?|#)?[ \t]*[:#\-]?[ \t]*(?<n>\d{2}-?\d{7})(?!\d)", Opts);

    private static readonly Regex RoutingRx = new(
        @"(?:\brouting(?:[ ]+(?:number|no\.?|num\.?|transit))?|\bABA(?:[ ]+(?:number|no\.?|routing))?|\bRTN\b|\btransit[ ]+number)" +
        @"[ \t]*(?:number|no\.?|#)?[ \t]*[:#\-]?[ \t]*(?<n>\d{9})(?!\d)", Opts);

    private static readonly Regex AccountRx = new(
        @"\b(?:acct|account|a/c)\b\.?[ \t]*(?:number|no\.?|num\.?|#)?[ \t]*[:#\-]?[ \t]*(?<n>\d(?:[ -]?\d){5,16})(?!\d)", Opts);

    private static readonly Regex LicenseRx = new(
        @"(?:driver[’']?s?[ ]+licen[sc]e|\bDLN?\b|\blicen[sc]e)[ \t]*(?:number|no\.?|num\.?|#)?[ \t]*[:#\-]?[ \t]*" +
        @"(?:[A-Z]{2}[ \t]+)?(?<n>[A-Z]{0,2}\d(?:[ -]?\d){5,14})(?!\d)", Opts);

    private static readonly Regex PassportRx = new(
        @"\bpassport\b[ \t]*(?:number|no\.?|num\.?|#)?[ \t]*[:#\-]?[ \t]*" +
        @"(?<n>(?=[A-Z0-9]{6,9}(?![A-Z0-9]))[A-Z0-9]*\d[A-Z0-9]*)", Opts);

    // ---- Street addresses -----------------------------------------------------------------------
    // US style: "123 N. Main Street, Apt 4B" optionally followed by "Springfield, IL 62704" (even on the next line).
    // Deliberately case-sensitive for the street name (must start with a capital) so ordinary sentences like
    // "item 5 of the Court order" don't match.
    private const string StreetSuffixes =
        "Street|Avenue|Road|Boulevard|Drive|Lane|Court|Circle|Place|Way|Parkway|Terrace|Trail|Highway|Square|Alley|Pike|Route|" +
        "St|Ave|Rd|Blvd|Dr|Ln|Ct|Cir|Pl|Pkwy|Ter|Trl|Hwy|Sq|Rte";

    private static readonly string StreetSuffixAlternation = string.Join("|",
        StreetSuffixes.Split('|').SelectMany(x => new[] { x, x.ToUpperInvariant() }).Distinct());

    private const string Directional = "(?:NE|NW|SE|SW|N|S|E|W|North|South|East|West|NORTH|SOUTH|EAST|WEST)";

    private static readonly Regex StreetAddressRx = new(
        @"(?<![\w#])\d{1,6}[A-Z]?[ ]+(?:" + Directional + @"\.?[ ]+)?" +
        @"(?:(?:[A-Z][A-Za-z0-9.'’-]*|\d{1,3}(?:st|nd|rd|th|ST|ND|RD|TH))[ ]+){1,4}?" +
        "(?:" + StreetSuffixAlternation + @")\b\.?" +
        @"(?:[ ]+" + Directional + @"\b\.?)?" +
        @"(?:,?[ ]+(?:Apt|APT|Apartment|APARTMENT|Suite|SUITE|Ste|STE|Unit|UNIT|Room|Rm|#)\.?[ ]*[A-Za-z0-9-]+)?" +
        @"(?:,?[ ]*\n?[ ]*[A-Z][A-Za-z.'’-]*(?:[ ]+[A-Z][A-Za-z.'’-]*){0,3},?[ ]+[A-Z]{2}[ ]+\d{5}(?:-\d{4})?)?",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PoBoxRx = new(@"\bP\.?[ ]?O\.?[ ]+Box[ ]+\d{1,6}\b", Opts);

    private static void AddGroup(List<SensitiveMatch> into, Regex rx, string text, SensitiveKind kind, Func<Group, bool>? accept = null)
    {
        foreach (Match m in rx.Matches(text))
        {
            var g = m.Groups["n"];
            if (g.Success && (accept?.Invoke(g) ?? true))
                into.Add(new SensitiveMatch(kind, g.Index, g.Length));
        }
    }

    private static void AddWhole(List<SensitiveMatch> into, Regex rx, string text, SensitiveKind kind)
    {
        foreach (Match m in rx.Matches(text))
            into.Add(new SensitiveMatch(kind, m.Index, m.Length));
    }

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

        if (options.Ein)
        {
            AddGroup(all, EinRx, text, SensitiveKind.Ein, g => EinPrefixes.Contains(g.Value[..2]));
            AddGroup(all, EinLabelledRx, text, SensitiveKind.Ein);
        }
        if (options.RoutingNumber) AddGroup(all, RoutingRx, text, SensitiveKind.RoutingNumber);
        if (options.BankAccount) AddGroup(all, AccountRx, text, SensitiveKind.BankAccount);
        if (options.DriversLicense) AddGroup(all, LicenseRx, text, SensitiveKind.DriversLicense);
        if (options.Passport) AddGroup(all, PassportRx, text, SensitiveKind.Passport);
        if (options.Address)
        {
            AddWhole(all, StreetAddressRx, text, SensitiveKind.Address);
            AddWhole(all, PoBoxRx, text, SensitiveKind.Address);
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
