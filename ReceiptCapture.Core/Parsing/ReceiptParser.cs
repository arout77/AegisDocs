using System.Globalization;
using System.Text.RegularExpressions;

namespace ReceiptCapture.Core.Parsing;

/// <summary>What the parser managed to work out. Anything it couldn't find is null.</summary>
public sealed record ParsedReceipt(string? Vendor, DateTime? Date, decimal? Total, decimal? Tax, string? Category);

/// <summary>
/// Turns raw OCR text into vendor / date / total / tax guesses.
/// It is heuristic by design: the app always shows the guesses in an editable form.
/// </summary>
public static class ReceiptParser
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // ---------- amounts ----------
    // 12.34  $12.34  1,234.56   (not 12.34% and not part of 12.34.56 / dates)
    private static readonly Regex AmountRx = new(
        @"(?<![\d.,])(?:[$€£]\s*)?(?<int>\d{1,3}(?:,\d{3})+|\d+)\.(?<frac>\d{2})(?!\d)(?!\.\d)(?!\s*%)",
        Opts);

    private static readonly Regex StrongTotalRx = new(
        @"\b(balance\s+due|amount\s+due|total\s+due|grand\s+total|total\s+amount|amount\s+payable|total\s+payable)\b", Opts);

    private static readonly Regex TotalRx = new(@"\btotal\b", Opts);

    private static readonly Regex NotATotalRx = new(
        @"\b(sub\s*-?\s*total|total\s+(tax|savings|saved|discount|items?|qty|quantity|points)|(tax|item|items)\s+total)\b", Opts);

    private static readonly Regex PaidRx = new(
        @"\b(amount\s+paid|total\s+paid|visa|mastercard|master\s+card|amex|discover|debit|credit\s+card)\b", Opts);

    private static readonly Regex TaxRx = new(@"\b(sales\s+tax|tax|vat|gst|hst|pst)\b", Opts);

    private static readonly Regex NotATaxRx = new(
        @"\b(tax\s+(id|invoice|number|no|reg\w*|exempt)|pre-?\s?tax|before\s+tax|excl\w*\s+tax|total\s+tax\s+and)\b", Opts);

    // ---------- dates ----------
    private const string MonthNames =
        @"Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|June?|July?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?";

    private static readonly Regex IsoDateRx = new(
        @"\b(?<y>(?:19|20)\d{2})[-/.](?<m>\d{1,2})[-/.](?<d>\d{1,2})\b", Opts);

    private static readonly Regex NumericDateRx = new(
        @"\b(?<a>\d{1,2})[/\-.](?<b>\d{1,2})[/\-.](?<y>\d{4}|\d{2})\b", Opts);

    private static readonly Regex MonthFirstDateRx = new(
        $@"\b(?<mon>{MonthNames})\.?\s+(?<d>\d{{1,2}})(?:st|nd|rd|th)?,?\s+(?<y>\d{{4}})\b", Opts);

    private static readonly Regex DayFirstDateRx = new(
        $@"\b(?<d>\d{{1,2}})(?:st|nd|rd|th)?\s+(?<mon>{MonthNames})\.?,?\s+(?<y>\d{{4}})\b", Opts);

    private static readonly Regex DateWordRx = new(@"\bdate\b", Opts);
    private static readonly Regex DueRx = new(@"\bdue\b", Opts);

    // ---------- vendor ----------
    private static readonly Regex VendorFromRx = new(
        @"^\s*(?:from|vendor|supplier|seller|billed\s+by|sold\s+by)\s*[:\-]\s*(?<v>.+)$", Opts);

    private static readonly Regex VendorSkipRx = new(
        @"\b(receipt|invoice|order|tel|telephone|phone|fax|www|http|welcome|thank|date|time|cashier|server|table|bill\s+to|ship\s+to|customer|duplicate|copy|statement|page)\b|@",
        Opts);

    private static readonly Regex StreetAddressRx = new(@"^\d+\s+\w+", Opts);

    // ---------- categories ----------
    private static readonly (string Category, Regex Keywords)[] CategoryRules =
    {
        ("Meals & Entertainment", new Regex(@"\b(restaurant|cafe|café|coffee|diner|pizza|pizzeria|grill|bistro|burger|bakery|tavern|pub|deli|catering|starbucks|mcdonald'?s|subway|cinema|theater|theatre)\b", Opts)),
        ("Fuel",                  new Regex(@"\b(gas\s+station|fuel|unleaded|diesel|gallons?|shell|exxon|chevron|sunoco|marathon|speedway|bp)\b", Opts)),
        ("Travel",                new Regex(@"\b(uber|lyft|taxi|cab|airline|airlines|airways|hotel|motel|inn|parking|flight|rental\s+car|airport|amtrak|train|toll)\b", Opts)),
        ("Office Supplies",       new Regex(@"\b(staples|office\s+depot|officemax|stationery|toner|printer|ink|paper|envelopes?|folders?)\b", Opts)),
        ("Software & Subscriptions", new Regex(@"\b(subscription|software|license|licence|saas|hosting|domain|cloud|renewal)\b", Opts)),
        ("Groceries",             new Regex(@"\b(grocery|groceries|supermarket|walmart|kroger|aldi|costco|safeway|publix|produce|whole\s+foods)\b", Opts)),
        ("Utilities",             new Regex(@"\b(electric|electricity|utility|utilities|water\s+bill|internet|broadband|telecom|wireless|natural\s+gas)\b", Opts)),
        ("Professional Services", new Regex(@"\b(consulting|consultant|legal|attorney|law\s+office|accounting|accountant|bookkeeping|services\s+rendered|retainer)\b", Opts)),
        ("Equipment",             new Regex(@"\b(equipment|laptop|monitor|keyboard|hardware|machinery|furniture)\b", Opts)),
    };

    public static ParsedReceipt Parse(string? text, DateTime? today = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new ParsedReceipt(null, null, null, null, null);

        var now = today ?? DateTime.Today;
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0)
                        .ToArray();

        var total = FindTotal(lines);
        var tax = FindTax(lines, total);
        var vendor = FindVendor(lines, now);
        var date = FindDate(lines, now);
        var category = GuessCategory(text, vendor);

        return new ParsedReceipt(vendor, date, total, tax, category);
    }

    // =====================================================================
    // Amounts
    // =====================================================================

    public static List<decimal> AmountsIn(string line)
    {
        var result = new List<decimal>();
        foreach (Match m in AmountRx.Matches(line))
        {
            var digits = m.Groups["int"].Value.Replace(",", "") + "." + m.Groups["frac"].Value;
            if (decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                result.Add(value);
        }
        return result;
    }

    /// <summary>Amount on the keyword line, or on the next line if that line is basically just a number.</summary>
    private static decimal? AmountOnOrAfter(string[] lines, int i)
    {
        var here = AmountsIn(lines[i]);
        if (here.Count > 0) return here[^1];

        if (i + 1 < lines.Length)
        {
            var next = lines[i + 1];
            var letters = next.Count(char.IsLetter);
            var amounts = AmountsIn(next);
            if (amounts.Count > 0 && letters <= 3) return amounts[^1];
        }
        return null;
    }

    private static decimal? FindTotal(string[] lines)
    {
        decimal? strong = null, plain = null, paid = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (StrongTotalRx.IsMatch(line))
            {
                var a = AmountOnOrAfter(lines, i);
                if (a.HasValue) strong = a;           // keep the last one: totals sit at the bottom
            }
            else if (TotalRx.IsMatch(line) && !NotATotalRx.IsMatch(line))
            {
                var a = AmountOnOrAfter(lines, i);
                if (a.HasValue) plain = a;
            }
            else if (PaidRx.IsMatch(line))
            {
                var a = AmountOnOrAfter(lines, i);
                if (a.HasValue) paid = a;
            }
        }

        if (strong.HasValue) return strong;
        if (plain.HasValue) return plain;
        if (paid.HasValue) return paid;

        // Last resort: the biggest amount anywhere in the document.
        var all = lines.SelectMany(AmountsIn).ToList();
        return all.Count > 0 ? all.Max() : null;
    }

    private static decimal? FindTax(string[] lines, decimal? total)
    {
        decimal? tax = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!TaxRx.IsMatch(line) || NotATaxRx.IsMatch(line)) continue;
            var a = AmountOnOrAfter(lines, i);
            if (a.HasValue) tax = a;
        }

        if (tax.HasValue && total.HasValue && tax.Value >= total.Value) return null;
        return tax;
    }

    // =====================================================================
    // Dates
    // =====================================================================

    public static DateTime? FindDate(string[] lines, DateTime today)
    {
        var candidates = new List<(DateTime Date, bool HasDateWord, bool HasDue)>();

        foreach (var line in lines)
        {
            var found = DatesIn(line, today);
            if (found.Count == 0) continue;
            var hasDateWord = DateWordRx.IsMatch(line);
            var hasDue = DueRx.IsMatch(line);
            foreach (var d in found) candidates.Add((d, hasDateWord, hasDue));
        }

        if (candidates.Count == 0) return null;

        // 1) "Date: ..." / "Invoice Date: ..." (but not "Due Date")
        foreach (var c in candidates) if (c.HasDateWord && !c.HasDue) return c.Date;
        // 2) first date that isn't a due date
        foreach (var c in candidates) if (!c.HasDue) return c.Date;
        // 3) whatever is left
        return candidates[0].Date;
    }

    private static List<DateTime> DatesIn(string line, DateTime today)
    {
        var results = new List<(int Index, DateTime Date)>();

        foreach (Match m in IsoDateRx.Matches(line))
        {
            if (TryMake(int.Parse(m.Groups["y"].Value), int.Parse(m.Groups["m"].Value), int.Parse(m.Groups["d"].Value), today, out var d))
                results.Add((m.Index, d));
        }

        foreach (Match m in NumericDateRx.Matches(line))
        {
            int a = int.Parse(m.Groups["a"].Value);
            int b = int.Parse(m.Groups["b"].Value);
            int y = NormalizeYear(m.Groups["y"].Value);

            // US style (month/day) first; fall back to day/month when that can't be a real date.
            if (TryMake(y, a, b, today, out var d) || TryMake(y, b, a, today, out d))
                results.Add((m.Index, d));
        }

        foreach (Match m in MonthFirstDateRx.Matches(line))
        {
            if (TryMake(int.Parse(m.Groups["y"].Value), MonthNumber(m.Groups["mon"].Value), int.Parse(m.Groups["d"].Value), today, out var d))
                results.Add((m.Index, d));
        }

        foreach (Match m in DayFirstDateRx.Matches(line))
        {
            if (TryMake(int.Parse(m.Groups["y"].Value), MonthNumber(m.Groups["mon"].Value), int.Parse(m.Groups["d"].Value), today, out var d))
                results.Add((m.Index, d));
        }

        return results.OrderBy(r => r.Index).Select(r => r.Date).ToList();
    }

    private static int NormalizeYear(string y) => y.Length == 2 ? 2000 + int.Parse(y) : int.Parse(y);

    private static int MonthNumber(string name)
    {
        string[] months = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };
        return Array.FindIndex(months, m => name.StartsWith(m, StringComparison.OrdinalIgnoreCase)) + 1;
    }

    private static bool TryMake(int year, int month, int day, DateTime today, out DateTime date)
    {
        date = default;
        if (year < 2000 || year > today.Year + 1) return false;
        if (month < 1 || month > 12) return false;
        if (day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
        var candidate = new DateTime(year, month, day);
        if (candidate > today.AddDays(1)) return false;   // receipts aren't dated in the future
        date = candidate;
        return true;
    }

    // =====================================================================
    // Vendor
    // =====================================================================

    private static string? FindVendor(string[] lines, DateTime today)
    {
        // Invoices often say "From: Acme Ltd".
        foreach (var line in lines)
        {
            var m = VendorFromRx.Match(line);
            if (m.Success)
            {
                var v = Clean(m.Groups["v"].Value);
                if (v.Length >= 2) return v;
            }
        }

        // Otherwise: the first plausible-looking line near the top.
        foreach (var line in lines.Take(8))
        {
            var letters = line.Count(char.IsLetter);
            var digits = line.Count(char.IsDigit);
            if (letters < 3) continue;
            if (digits > 0 && (double)digits / line.Length > 0.3) continue;
            if (VendorSkipRx.IsMatch(line)) continue;
            if (StreetAddressRx.IsMatch(line)) continue;
            if (DatesIn(line, today.AddYears(1)).Count > 0) continue;
            if (AmountsIn(line).Count > 0) continue;

            var cleaned = Clean(line);
            if (cleaned.Length >= 3) return cleaned;
        }
        return null;
    }

    private static string Clean(string s) => s.Trim(' ', '*', '#', '-', '=', '_', '.', ':', '|', '~');

    // =====================================================================
    // Category
    // =====================================================================

    private static string? GuessCategory(string text, string? vendor)
    {
        string? best = null;
        int bestScore = 0;

        foreach (var (category, rx) in CategoryRules)
        {
            var hits = rx.Matches(text).Select(m => m.Value.ToLowerInvariant()).Distinct().Count();
            if (hits == 0) continue;
            var score = hits + (vendor != null && rx.IsMatch(vendor) ? 3 : 0);
            if (score > bestScore) { best = category; bestScore = score; }
        }
        return best;
    }
}
