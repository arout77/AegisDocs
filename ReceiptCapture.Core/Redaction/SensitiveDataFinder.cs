using System.Text.RegularExpressions;

namespace ReceiptCapture.Core.Redaction;

/// <summary>
/// Declaration order is also the priority when two finds overlap: earlier kinds win. Labelled and
/// highly specific kinds therefore sit before the loose ones (a number printed after "Fax:" is a fax
/// number, not just a phone number).
/// </summary>
public enum SensitiveKind
{
    CustomTerm, CardTrackData, CreditCard, Ssn, Ein, RoutingNumber, BankAccount, Passport, DriversLicense,
    Mrn, HealthPlanId, RxNumber, DateOfBirth, CardCvv, CardExpiry, Email, Url, IpAddress, Fax, Phone,
    AgeOver89, Date, Address, ZipCode,
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

    // Detectors added for the compliance scanner. They are off unless asked for, so the Redact tab
    // behaves exactly as before.
    public bool Mrn { get; init; }
    public bool HealthPlanId { get; init; }
    public bool RxNumber { get; init; }
    public bool ZipCode { get; init; }
    public bool IpAddress { get; init; }
    public bool Url { get; init; }
    public bool Fax { get; init; }
    public bool AgeOver89 { get; init; }
    /// <summary>Every date, not just ones labelled as a birth date (HIPAA counts all elements of a date except the year).</summary>
    public bool AllDates { get; init; }
    public bool CardCvv { get; init; }
    public bool CardExpiry { get; init; }
    public bool CardTrackData { get; init; }

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

    // ---- Social Security numbers -------------------------------------------------------------------
    // Written with dashes (any kind), with or without spaces around them, and with a digit or two possibly misread by
    // OCR (O for 0, I or l for 1, S for 5, B for 8). A stray dash stuck to either end (a card border) is ignored.
    private const string SsnDigit = @"[\dOIlSB|]";
    private const string SsnDash = @"[-–—−‐‑]";

    private static readonly Regex SsnDashedRx = new(
        $@"(?<!\d)(?<!\d{SsnDash})(?<a>{SsnDigit}(?: ?{SsnDigit}){{2}})[ ]{{0,2}}{SsnDash}[ ]{{0,2}}(?<g>{SsnDigit} ?{SsnDigit})" +
        $@"[ ]{{0,2}}{SsnDash}[ ]{{0,2}}(?<s>{SsnDigit}(?: ?{SsnDigit}){{3}})(?!\d)(?!{SsnDash}\d)", Opts);

    // Spaces only ("123 45 6789"): kept strict, because without a dash it's an ordinary run of numbers.
    private static readonly Regex SsnSpacedRx = new(
        @"(?<![\d-])(?<a>\d{3})[ ]+(?<g>\d{2})[ ]+(?<s>\d{4})(?![\d-])", Opts);

    // Nine digits with no (or odd) separators: only believed when the words "Social Security" or "SSN" are just before.
    private static readonly Regex SsnBareRx = new(
        @"(?<![\d-])\d{3}[ -]?\d{2}[ -]?\d{4}(?![\d-])", Opts);

    private static readonly Regex SsnContextRx = new(
        @"social[ ]+security|\bSSN\b|\bSS[ ]?#|\bITIN\b|taxpayer[ ]+identification", Opts);

    private static string SsnDigitsOf(string raw)
    {
        var sb = new System.Text.StringBuilder(9);
        foreach (var c in raw)
        {
            if (c == ' ') continue;
            sb.Append(c switch { 'O' or 'o' => '0', 'I' or 'i' or 'l' or 'L' or '|' => '1', 'S' or 's' => '5', 'B' or 'b' => '8', _ => c });
        }
        return sb.ToString();
    }

    /// <summary>OCR's guesses are only trusted when most of the nine characters really are digits.</summary>
    private static bool MostlyDigits(string raw) => raw.Count(char.IsLetter) <= 2 && raw.Count(char.IsDigit) >= 6;

    /// <summary>
    /// Placeholders (000, 666, a 00 group, 0000 serial) and 9xx numbers that aren't in an ITIN range are not real
    /// numbers, so they're passed over, unless the page itself talks about Social Security.
    /// </summary>
    private static bool PlausibleSsn(string digits, bool dashed, bool pageMentionsSsn)
    {
        if (pageMentionsSsn) return true;
        string area = digits[..3], group = digits.Substring(3, 2), serial = digits[5..];
        if (area == "000" || area == "666" || group == "00" || serial == "0000") return false;
        if (area[0] == '9')
        {
            int g = int.Parse(group);
            bool itinGroup = g is >= 50 and <= 65 or >= 70 and <= 88 or >= 90 and <= 92 or >= 94 and <= 99;
            return dashed && itinGroup;
        }
        return true;
    }

    private static void AddSsn(List<SensitiveMatch> all, string text)
    {
        var context = SsnContextRx.Matches(text);
        bool pageMentionsSsn = context.Count > 0;

        foreach (Match m in SsnDashedRx.Matches(text))
        {
            var raw = m.Groups["a"].Value + m.Groups["g"].Value + m.Groups["s"].Value;
            if (!MostlyDigits(raw.Replace(" ", ""))) continue;
            var digits = SsnDigitsOf(raw);
            if (digits.Length == 9 && digits.All(char.IsDigit) && PlausibleSsn(digits, dashed: true, pageMentionsSsn))
                all.Add(new SensitiveMatch(SensitiveKind.Ssn, m.Index, m.Length));
        }

        foreach (Match m in SsnSpacedRx.Matches(text))
            if (PlausibleSsn(m.Groups["a"].Value + m.Groups["g"].Value + m.Groups["s"].Value, dashed: false, pageMentionsSsn))
                all.Add(new SensitiveMatch(SensitiveKind.Ssn, m.Index, m.Length));

        foreach (Match m in SsnBareRx.Matches(text))
        {
            // The context words must be shortly before it (a label, or the heading of a card).
            bool labelled = context.Cast<Match>().Any(c => c.Index + c.Length <= m.Index && m.Index - (c.Index + c.Length) <= 80);
            if (labelled) all.Add(new SensitiveMatch(SensitiveKind.Ssn, m.Index, m.Length));
        }
    }

    /// <summary>True if the whole text is a Social Security number, in any of the forms above (used for values found under a label).</summary>
    internal static bool LooksLikeSsn(string value)
    {
        value = value.Trim();
        var dashed = SsnDashedRx.Match(value);
        if (dashed.Success && dashed.Index == 0 && dashed.Length == value.Length)
        {
            var raw = (dashed.Groups["a"].Value + dashed.Groups["g"].Value + dashed.Groups["s"].Value).Replace(" ", "");
            return MostlyDigits(raw);
        }
        return Regex.IsMatch(value, @"^\d{3}[ -]?\d{2}[ -]?\d{4}$");
    }

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
        @"(?<d>\d{1,2}[/\-. ]\d{1,2}[/\-. ]\d{2,4}" +
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
        @"(?:driver[ ]*[/\\][ ]*ID|driver[’']?s?[ ]+(?:ID|licen[sc]e)|\bDLN?\b|\blicen[sc]e|\bstate[ ]+ID" +
        @"|\bID(?=[ ]*(?:number|no\b|num\b|#)))[ \t]*(?:number|no\.?|num\.?|#)?[ \t]*[:#\-]?[ \t]*" +
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

    // ---- Detectors added for the compliance scanner ----------------------------------------------

    // Health identifiers. Like the bank numbers above, these have no shape of their own, so they are found by the
    // label printed next to them. Only the number is marked. The lookahead insists on at least one digit so a label
    // followed by an ordinary word ("MRN: pending") is not a hit.
    private static readonly Regex MrnRx = new(
        @"(?:\bMRN\b|\bMR[ ]?#|medical[ ]+records?(?:[ ]+(?:number|no\.?|num\.?|#))?" +
        @"|chart[ ]+(?:number|no\.?|num\.?|#)|patient[ ]+(?:id|number|no\.?|num\.?|#))" +
        @"[ \t]*(?:number|no\.?|num\.?|#)?[ \t]*[:#\-]?[ \t]*(?<n>(?=[A-Z0-9-]*\d)[A-Z0-9][A-Z0-9-]{3,14})(?![A-Z0-9])", Opts);

    // "Prescription number 1006599", "Rx # 4455667": the number of a prescription, which ties a person to their medication.
    private static readonly Regex RxRx = new(
        @"\b(?:prescription|Rx)[ ]*(?:number|no\b\.?|num\b\.?|#)[ \t]*[:#\-]?[ \t]*" +
        @"(?<n>(?=[A-Z0-9-]*\d)[A-Z0-9][A-Z0-9-]{3,14})(?![A-Z0-9])", Opts);

    private static readonly Regex PlanIdRx = new(
        @"\b(?:member|subscriber|insurance|insured|beneficiary|policy|medicaid|medicare|health[ ]+plan)[ ]+" +
        @"(?:id|identification|number|no\.?|num\.?|#)(?:[ ]+(?:number|no\.?|num\.?|#))?" +
        @"[ \t]*[:#\-]?[ \t]*(?<n>(?=[A-Z0-9-]*\d)[A-Z0-9][A-Z0-9-]{4,18})(?![A-Z0-9])", Opts);

    // Medicare Beneficiary Identifier: 11 characters in a fixed pattern (letters never include S, L, O, I, B, Z).
    // Specific enough to find without a label. Upper case only, as printed.
    private static readonly Regex MbiRx = new(
        @"(?<![A-Z0-9])(?<n>[1-9][AC-HJKMNP-RT-Y][AC-HJKMNP-RT-Y0-9][0-9]-?[AC-HJKMNP-RT-Y][AC-HJKMNP-RT-Y0-9][0-9]-?[AC-HJKMNP-RT-Y]{2}[0-9]{2})(?![A-Z0-9])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Card details beyond the number itself.
    private static readonly Regex CvvRx = new(
        @"(?:\bCVV2?\b|\bCVC2?\b|\bCSC\b|\bCID\b|\bCAV2?\b|card[ ]+(?:verification|security)[ ]+(?:code|value|number)|security[ ]+code)" +
        @"[ \t]*(?:code|value|number|no\.?|#)?[ \t]*[:#\-]?[ \t]*(?<n>\d{3,4})(?!\d)", Opts);

    private const string ExpiryValue = @"(?<n>(?:0?[1-9]|1[0-2])[ ]?[/\-][ ]?(?:\d{4}|\d{2}))(?!\d)";

    private static readonly Regex ExpiryLabelledRx = new(
        @"(?:\bexp(?:iry|iration|ires)?\b\.?(?:[ ]+date)?|valid[ ]+(?:thru|through|until)|good[ ]+(?:thru|through))" +
        @"[ \t]*[:#\-]?[ \t]*" + ExpiryValue, Opts);

    // An unlabelled MM/YY shortly after a card number on the same line.
    private static readonly Regex ExpiryAfterCardRx = new(
        @"\G[^\d\n]{0,12}?(?<n>(?:0[1-9]|1[0-2])[ ]?[/\-][ ]?(?:\d{4}|\d{2}))(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Magnetic-stripe data: track 1 starts %B, track 2 starts ;. Both contain the whole card number.
    private static readonly Regex Track1Rx = new(
        @"%B\d{13,19}\^[^\^\n]{2,26}\^\d{4}[^\n?]{0,70}\??", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Track2Rx = new(
        @"(?<!\w);\d{13,19}=\d{4}[0-9=]{0,40}\??", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Fax numbers: only when labelled (the number alone looks like any phone number).
    private static readonly Regex FaxRx = new(
        @"(?:\bfax(?:[ ]+(?:number|no\.?|num\.?|#))?|\bfacsimile|\bF(?=[ ]?:))[ \t]*[:#\-]?[ \t]*" +
        @"(?<n>(?:\+?1[ .-]?)?(?:\(\d{3}\)|\d{3})[ .-]?\d{3}[ .-]?\d{4})(?!\d)", Opts);

    // Web addresses and IP addresses.
    private static readonly Regex UrlRx = new(
        @"(?<![\w@.])(?:(?:https?|ftp)://|www\.)[^\s<>""']*[^\s<>""'.,;:!?)\]}]", Opts);

    private const string Octet = @"(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)";
    private static readonly Regex Ipv4Rx = new(
        @"(?<![\d.])" + Octet + @"(?:\." + Octet + @"){3}(?!\d)(?!\.\d)", Opts);
    private static readonly Regex Ipv6Rx = new(
        @"(?<![0-9A-F:])(?:[0-9A-F]{1,4}:){7}[0-9A-F]{1,4}(?![0-9A-F:])", Opts);

    // Ages over 89 (HIPAA treats them as identifying): "95 years old", "95-year-old", "95 y/o", "age 95", "aged: 102".
    // Only the number is marked. A bare "95 years" is ignored: it is as likely to be a company's age.
    private static readonly Regex AgeUnitRx = new(
        @"(?<![\d.$,/-])(?<n>9\d|1[0-4]\d)(?![\d,%]|\.\d)(?=[ ]?-?[ ]?(?:years?|yrs?)[ -]?old\b|[ ]?y/?o\b)", Opts);
    private static readonly Regex AgeLabelledRx = new(
        @"\bage[d]?[ \t]*[:\-]?[ \t]*(?<n>9\d|1[0-4]\d)(?![\d%]|\.\d)", Opts);

    // Dates of any kind: 03/14/2025, 2025-03-14, March 14, 2025, 14 March 2025, March 14, March 2025.
    // The word forms are case-sensitive (the month must start with a capital) so "may 5 people" is left alone.
    private const string MonthName =
        "(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|June?|July?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)";

    private static readonly Regex NumericDateRx = new(
        @"(?<![\d/.\-])(?<a>\d{1,2})[/.\-](?<b>\d{1,2})[/.\-](?<y>\d{4}|\d{2})(?![\d/\-]|\.\d)", Opts);
    private static readonly Regex IsoDateRx = new(
        @"(?<![\d/.\-])(?<y>(?:19|20)\d{2})-(?<m>0[1-9]|1[0-2])-(?<d>0[1-9]|[12]\d|3[01])(?![\d\-])", Opts);
    private static readonly Regex[] WordDateRx =
    {
        new(@"(?<![A-Za-z])" + MonthName + @"\.?[ ]+\d{1,2}(?:st|nd|rd|th)?(?:,?[ ]+(?:19|20)\d{2})?(?!\d)",
            RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"(?<![\dA-Za-z])\d{1,2}(?:st|nd|rd|th)?[ ]+" + MonthName + @"\b\.?(?:,?[ ]+(?:19|20)\d{2})?(?!\d)",
            RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"(?<![A-Za-z])" + MonthName + @"\.?,?[ ]+(?:19|20)\d{2}(?!\d)",
            RegexOptions.CultureInvariant | RegexOptions.Compiled),
    };

    // ZIP codes: only where they are clearly a ZIP (after a state, or labelled), never a bare five-digit number.
    private const string States =
        "AL|AK|AZ|AR|CA|CO|CT|DE|DC|FL|GA|HI|ID|IL|IN|IA|KS|KY|LA|ME|MD|MA|MI|MN|MS|MO|MT|NE|NV|NH|NJ|NM|NY|NC|ND|OH|OK|OR|PA|RI|SC|SD|TN|TX|UT|VT|VA|WA|WV|WI|WY|PR|VI|GU|AS|MP";
    private static readonly Regex ZipAfterStateRx = new(
        @"(?<![A-Za-z])(?:" + States + @")\.?,?[ ]+(?<n>\d{5}(?:-\d{4})?)(?![\d-])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ZipLabelledRx = new(
        @"\b(?:zip(?:[ ]*code)?|postal[ ]+code)\b[ \t]*[:#\-]?[ \t]*(?<n>\d{5}(?:-\d{4})?)(?![\d-])", Opts);

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

    public static List<SensitiveMatch> Find(string text, FindOptions options, IEnumerable<SensitiveMatch>? extraMatches = null)
    {
        var all = new List<SensitiveMatch>();
        if (string.IsNullOrEmpty(text)) return all;

        if (options.Ssn) AddSsn(all, text);

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

        // ---- compliance-scanner detectors (all off unless asked for) ----
        if (options.CardTrackData)
        {
            AddWhole(all, Track1Rx, text, SensitiveKind.CardTrackData);
            AddWhole(all, Track2Rx, text, SensitiveKind.CardTrackData);
        }
        if (options.CardCvv) AddGroup(all, CvvRx, text, SensitiveKind.CardCvv);
        if (options.CardExpiry)
        {
            AddGroup(all, ExpiryLabelledRx, text, SensitiveKind.CardExpiry);
            foreach (Match card in CardRx.Matches(text))
            {
                var digits = new string(card.Value.Where(char.IsDigit).ToArray());
                if (digits.Length is < 13 or > 19 || !PassesLuhn(digits)) continue;
                var after = ExpiryAfterCardRx.Match(text, card.Index + card.Length);
                if (after.Success)
                {
                    var g = after.Groups["n"];
                    all.Add(new SensitiveMatch(SensitiveKind.CardExpiry, g.Index, g.Length));
                }
            }
        }
        if (options.Mrn) AddGroup(all, MrnRx, text, SensitiveKind.Mrn);
        if (options.RxNumber) AddGroup(all, RxRx, text, SensitiveKind.RxNumber);
        if (options.HealthPlanId)
        {
            AddGroup(all, PlanIdRx, text, SensitiveKind.HealthPlanId);
            AddGroup(all, MbiRx, text, SensitiveKind.HealthPlanId);
        }
        if (options.Fax) AddGroup(all, FaxRx, text, SensitiveKind.Fax);
        if (options.Url) AddWhole(all, UrlRx, text, SensitiveKind.Url);
        if (options.IpAddress)
        {
            AddWhole(all, Ipv4Rx, text, SensitiveKind.IpAddress);
            AddWhole(all, Ipv6Rx, text, SensitiveKind.IpAddress);
        }
        if (options.AgeOver89)
        {
            AddGroup(all, AgeUnitRx, text, SensitiveKind.AgeOver89);
            AddGroup(all, AgeLabelledRx, text, SensitiveKind.AgeOver89);
        }
        if (options.AllDates)
        {
            foreach (Match m in NumericDateRx.Matches(text))
            {
                int a = int.Parse(m.Groups["a"].Value), b = int.Parse(m.Groups["b"].Value);
                bool plausible = (a is >= 1 and <= 12 && b is >= 1 and <= 31) || (a is >= 1 and <= 31 && b is >= 1 and <= 12);
                if (plausible) all.Add(new SensitiveMatch(SensitiveKind.Date, m.Index, m.Length));
            }
            AddWhole(all, IsoDateRx, text, SensitiveKind.Date);
            foreach (var rx in WordDateRx) AddWhole(all, rx, text, SensitiveKind.Date);
        }
        if (options.ZipCode)
        {
            AddGroup(all, ZipAfterStateRx, text, SensitiveKind.ZipCode);
            AddGroup(all, ZipLabelledRx, text, SensitiveKind.ZipCode);
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

        // Matches found another way (by where words sit on the page) compete on the same terms as the ones above.
        if (extraMatches != null) all.AddRange(extraMatches);

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
