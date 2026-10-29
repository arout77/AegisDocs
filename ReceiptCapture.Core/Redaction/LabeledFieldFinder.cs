using System.Text;
using System.Text.RegularExpressions;

namespace ReceiptCapture.Core.Redaction;

/// <summary>
/// Finds values that sit <b>under</b> their label, as in a table header over a row of cells (an ID card, a bank
/// statement, an intake form) or a field name above its box. The text patterns in <see cref="SensitiveDataFinder"/>
/// only see "label, then value" on one line; this looks at where the words are on the page instead.
/// A label such as "BIRTH DATE" or "DRIVER / ID NUMBER" is found first, then the words in the row just below it,
/// in the same column, are checked to look like that kind of value before they are marked.
/// </summary>
public static class LabeledFieldFinder
{
    private const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private sealed record Spec(SensitiveKind Kind, Regex Label, Func<string, bool> ValueOk, Func<FindOptions, bool> Enabled);

    // ------------------------------------------------------------------
    // What each kind of field is called, and what its value looks like
    // ------------------------------------------------------------------
    private const string NumberWord = @"(?:NUMBER|NO|NUM|NBR|#)";

    private static readonly Spec[] Specs =
    {
        new(SensitiveKind.DateOfBirth,
            new(@"^(?:DOB|DATE ?OF ?BIRTH|BIRTH ?DATE|BIRTH|BORN)$", Opts),
            IsDate, o => o.DateOfBirth),

        new(SensitiveKind.DriversLicense,
            new($@"^(?:DRIVERS? (?:ID|LICEN[SC]E|LIC)(?: {NumberWord})?|DRIVERS? {NumberWord}|DLN?(?: {NumberWord})?|STATE ID(?: {NumberWord})?" +
                $@"|ID {NumberWord}|LICEN[SC]E {NumberWord}|LIC {NumberWord}|OPERATORS? LICEN[SC]E(?: {NumberWord})?)$", Opts),
            v => IdLike(v, 6, 20, 5), o => o.DriversLicense),

        new(SensitiveKind.Passport,
            new($@"^PASSPORT(?: {NumberWord})?$", Opts),
            v => IdLike(v, 6, 12, 4), o => o.Passport),

        new(SensitiveKind.Ssn,
            new($@"^(?:SSN|SS #?|SOCIAL SECURITY(?: {NumberWord})?)$", Opts),
            SensitiveDataFinder.LooksLikeSsn, o => o.Ssn),

        new(SensitiveKind.Ein,
            new($@"^(?:EIN|TIN|EMPLOYER IDENTIFICATION(?: {NumberWord})?|FEDERAL (?:TAX )?(?:ID|IDENTIFICATION)|TAX ID(?:ENTIFICATION)?(?: {NumberWord})?)$", Opts),
            v => Regex.IsMatch(NoSpaces(v), @"^\d{2}-?\d{7}$"), o => o.Ein),

        new(SensitiveKind.RoutingNumber,
            new($@"^(?:ROUTING(?: {NumberWord}| TRANSIT)?|ABA(?: {NumberWord}| ROUTING)?|RTN|TRANSIT {NumberWord})$", Opts),
            v => Regex.IsMatch(NoSpaces(v), @"^\d{9}$"), o => o.RoutingNumber),

        new(SensitiveKind.BankAccount,
            new($@"^(?:ACCOUNT|ACCT|A C)(?: {NumberWord})?$", Opts),
            v => Regex.IsMatch(v, @"^\d(?:[ -]?\d){5,16}$"), o => o.BankAccount),

        new(SensitiveKind.Mrn,
            new($@"^(?:MRN|MEDICAL RECORDS?(?: {NumberWord})?|CHART {NumberWord}|PATIENT (?:ID|{NumberWord}))$", Opts),
            v => IdLike(v, 4, 15, 1), o => o.Mrn),

        new(SensitiveKind.RxNumber,
            new($@"^(?:PRESCRIPTION|RX) {NumberWord}$", Opts),
            v => IdLike(v, 5, 15, 4), o => o.RxNumber),

        new(SensitiveKind.HealthPlanId,
            new($@"^(?:MEMBER|SUBSCRIBER|INSURANCE|INSURED|BENEFICIARY|POLICY|MEDICAID|MEDICARE|HEALTH PLAN) (?:ID|IDENTIFICATION|{NumberWord})(?: {NumberWord})?$", Opts),
            v => IdLike(v, 5, 19, 1), o => o.HealthPlanId),

        // Any other column or field called "... DATE" (ISSUE DATE, SERVICE DATE, DATE OF ADMISSION ...). Birth dates are
        // handled above, so this comes last: when both match the same words, the birth-date reading wins.
        new(SensitiveKind.Date,
            new(@"^(?:[A-Z]+ )*DATE(?: OF [A-Z]+)?$|^DATE OF [A-Z]+(?: [A-Z]+)?$", Opts),
            IsDate, o => o.AllDates),
    };

    // ------------------------------------------------------------------
    // Finding them
    // ------------------------------------------------------------------
    /// <summary>The matches, as spans of the page's text (so they merge with the text finder's own results).</summary>
    public static IReadOnlyList<SensitiveMatch> Find(PageTextBuilder.Built page, FindOptions options)
    {
        var active = Specs.Where(s => s.Enabled(options)).ToArray();
        var words = page.Words;
        var found = new List<SensitiveMatch>();
        if (active.Length == 0 || words.Count < 2) return found;

        var usedAsLabel = new HashSet<int>();

        for (int i = 0; i < words.Count; i++)
        {
            if (usedAsLabel.Contains(i)) continue;

            // The longest run of neighbouring words on this line that reads as a label.
            (Spec Spec, int Last)? best = null;
            var text = new StringBuilder();
            for (int j = i; j < words.Count && j < i + 6; j++)
            {
                if (j > i && (words[j].Line != words[j - 1].Line || Gap(words[j - 1], words[j]) > 1.5 * Math.Max(words[j].Height, words[j - 1].Height)))
                    break;
                text.Append(' ').Append(words[j].Text);
                var normalized = Normalize(text.ToString());
                foreach (var spec in active)
                    if (spec.Label.IsMatch(normalized)) { best = (spec, j); break; }
            }
            if (best is not { } label) continue;

            for (int k = i; k <= label.Last; k++) usedAsLabel.Add(k);
            var valueIndexes = ValueBelow(words, i, label.Last, label.Spec);
            if (valueIndexes == null) continue;

            // One match per run of words that are neighbours in the page's text.
            int runStart = 0;
            for (int k = 1; k <= valueIndexes.Count; k++)
            {
                if (k < valueIndexes.Count && valueIndexes[k] == valueIndexes[k - 1] + 1) continue;
                int first = valueIndexes[runStart], last = valueIndexes[k - 1];
                found.Add(new SensitiveMatch(label.Spec.Kind, page.Spans[first].Start, page.Spans[last].End - page.Spans[first].Start));
                runStart = k;
            }
        }
        return found;
    }

    /// <summary>A rectangle (page-image pixels) where a value should be but OCR read nothing.</summary>
    public sealed record UnreadBox(SensitiveKind Kind, double X, double Y, double Width, double Height);

    private static readonly Regex SsnLabel = new(@"^(?:SSN|SS #?|SOCIAL SECURITY(?: NUMBER)?)$", Opts);

    /// <summary>
    /// Forms with one box per digit often come back from OCR with the label but none of the digits.
    /// Nothing can match a number that was never read, so when a Social Security label has no readable words in the
    /// row under it (or beside it), that row is returned as a box to black out. The person reviews it like any other hit.
    /// </summary>
    public static IReadOnlyList<UnreadBox> FindUnreadSsnBoxes(PageTextBuilder.Built page, FindOptions options)
    {
        var found = new List<UnreadBox>();
        var words = page.Words;
        if (!options.Ssn || words.Count == 0) return found;

        for (int i = 0; i < words.Count; i++)
        {
            int last = -1;
            var text = new StringBuilder();
            for (int j = i; j < words.Count && j < i + 6; j++)
            {
                if (j > i && Math.Abs((words[j].Y + words[j].Height / 2) - (words[j - 1].Y + words[j - 1].Height / 2)) > 0.6 * words[j].Height) break;
                if (j > i && Gap(words[j - 1], words[j]) > 1.5 * Math.Max(words[j].Height, words[j - 1].Height)) break;
                text.Append(' ').Append(words[j].Text);
                if (SsnLabel.IsMatch(Normalize(text.ToString()))) last = j;
            }
            if (last < 0) continue;

            var label = Enumerable.Range(i, last - i + 1).Select(k => words[k]).ToList();
            double left = label.Min(w => w.X), right = label.Max(w => w.X + w.Width);
            double h = label.Average(w => w.Height);
            double cy = label.Average(w => w.Y + w.Height / 2);
            // The label may be followed by "(if any)" on the same row: that belongs to the label, so extend over it
            int next = last + 1;
            while (next < words.Count && Math.Abs(words[next].Y + words[next].Height / 2 - cy) <= 0.6 * h
                   && words[next].X >= right - 1 && Gap(words[next - 1], words[next]) <= 1.5 * h
                   && words[next].Text.Any(ch => char.IsLetter(ch) || ch == '(' || ch == ')'))
            { right = Math.Max(right, words[next].X + words[next].Width); next++; }

            // The row under the label, as wide as the label plus some room for the boxes
            double boxLeft = left, boxRight = right + 0.6 * (right - left);
            double boxTop = cy + 0.6 * h, boxBottom = cy + 2.5 * h;

            bool somethingRead = words.Any(w =>
            {
                double wx = w.X + w.Width / 2, wy = w.Y + w.Height / 2;
                bool inUnder = wx >= boxLeft && wx <= boxRight && wy >= boxTop && wy <= boxBottom;
                bool inBeside = wy >= cy - 0.6 * h && wy <= cy + 0.6 * h && wx > right && wx <= right + 0.8 * (right - left)
                                && w.Text.Any(char.IsDigit);
                return inUnder || inBeside;
            });
            if (somethingRead) continue;

            found.Add(new UnreadBox(SensitiveKind.Ssn, boxLeft, boxTop, boxRight - boxLeft, boxBottom - boxTop));
        }
        return found;
    }

    /// <summary>The words in the row just under a label, in the label's column, if they look like that kind of value.</summary>
    private static List<int>? ValueBelow(IReadOnlyList<WordBox> words, int first, int last, Spec spec)
    {
        var label = Enumerable.Range(first, last - first + 1).Select(k => words[k]).ToList();
        double left = label.Min(w => w.X), right = label.Max(w => w.X + w.Width);
        double height = label.Average(w => w.Height);
        double centerY = label.Average(w => w.Y + w.Height / 2);
        double width = Math.Max(right - left, height);

        // Other labels on the same row, to the left and right: they mark where this label's column ends.
        var sameRow = Enumerable.Range(0, words.Count)
            .Where(k => (k < first || k > last) && Math.Abs(words[k].Y + words[k].Height / 2 - centerY) <= 0.6 * height)
            .Select(k => words[k]).OrderBy(w => w.X).ToList();
        double? leftNeighbourEdge = null, rightNeighbourEdge = null;
        foreach (var w in sameRow)
        {
            if (w.X + w.Width <= left + 1) leftNeighbourEdge = w.X + w.Width;          // keeps the nearest one on the left
            else if (w.X >= right - 1 && rightNeighbourEdge == null) rightNeighbourEdge = w.X;
        }
        double columnLeft = leftNeighbourEdge is { } l ? (l + left) / 2 : left - 2 * width;
        double columnRight = rightNeighbourEdge is { } r ? (right + r) / 2 : right + 4 * width;

        // The nearest row of words below the label that sits in the column.
        var below = Enumerable.Range(0, words.Count)
            .Where(k => (k < first || k > last))
            .Select(k => (Index: k, Word: words[k], Dy: words[k].Y + words[k].Height / 2 - centerY))
            .Where(c => c.Dy > 0.7 * height && c.Dy < 3.5 * height
                        && c.Word.X < columnRight && c.Word.X + c.Word.Width > columnLeft
                        && c.Word.Text.Any(char.IsLetterOrDigit))                 // table borders come back as "|" or "["
            .ToList();
        if (below.Count == 0) return null;

        var anchor = below.OrderBy(c => c.Dy).First();
        var row = below.Where(c => Math.Abs(c.Dy - anchor.Dy) <= 0.6 * Math.Max(anchor.Word.Height, c.Word.Height))
                       .OrderBy(c => c.Word.X).ToList();

        // Split the row where there's a wide gap and keep the group that sits most under the label.
        var groups = new List<List<(int Index, WordBox Word, double Dy)>> { new() { row[0] } };
        for (int k = 1; k < row.Count; k++)
        {
            if (Gap(row[k - 1].Word, row[k].Word) > 2.0 * Math.Max(row[k].Word.Height, row[k - 1].Word.Height)) groups.Add(new());
            groups[^1].Add(row[k]);
        }
        var chosen = groups.OrderByDescending(g => Overlap(g.Min(c => c.Word.X), g.Max(c => c.Word.X + c.Word.Width), left, right)).First();

        // Accept the whole group, or the group without a stray word at either end (OCR often adds one).
        var candidates = new List<List<(int Index, WordBox Word, double Dy)>> { chosen };
        if (chosen.Count > 2)
        {
            candidates.Add(chosen.Skip(1).ToList());
            candidates.Add(chosen.Take(chosen.Count - 1).ToList());
        }
        if (chosen.Count > 3) candidates.Add(chosen.Skip(1).Take(chosen.Count - 2).ToList());
        foreach (var c in candidates)
        {
            var value = string.Join(" ", c.Select(x => TrimMarks(x.Word.Text)));
            if (spec.ValueOk(value)) return c.Select(x => x.Index).OrderBy(x => x).ToList();
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Small helpers
    // ------------------------------------------------------------------
    private static double Gap(WordBox a, WordBox b) => b.X - (a.X + a.Width);

    private static double Overlap(double aLeft, double aRight, double bLeft, double bRight)
        => Math.Max(0, Math.Min(aRight, bRight) - Math.Max(aLeft, bLeft));

    /// <summary>Upper-case, with punctuation turned into spaces ("Driver's / ID No." becomes "DRIVERS ID NO").</summary>
    internal static string Normalize(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '#') sb.Append(c);
            else if (c is '\'' or '’' or '.') { }
            else sb.Append(' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static string NoSpaces(string s) => s.Replace(" ", "");

    /// <summary>Drops punctuation stuck to the ends of a word, such as a cell border read as part of it ("|25" becomes "25").</summary>
    private static string TrimMarks(string word)
    {
        int start = 0, end = word.Length;
        while (start < end && !char.IsLetterOrDigit(word[start])) start++;
        while (end > start && !char.IsLetterOrDigit(word[end - 1])) end--;
        return word[start..end];
    }

    /// <summary>Only letters, digits, spaces and dashes, of a plausible length, with enough digits to be a number and not a word.</summary>
    private static bool IdLike(string value, int minLength, int maxLength, int minDigits)
    {
        if (!value.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-')) return false;
        var alnum = value.Where(char.IsLetterOrDigit).ToArray();
        return alnum.Length >= minLength && alnum.Length <= maxLength && alnum.Count(char.IsDigit) >= minDigits;
    }

    private const string MonthName =
        "(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|June?|July?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)";

    private static readonly Regex NumericDate = new(@"^(?<a>\d{1,2})[ /.\-](?<b>\d{1,2})[ /.\-](?<y>\d{4}|\d{2})$", Opts);
    private static readonly Regex YearFirstDate = new(@"^(?<y>(?:19|20)\d{2})[ /.\-](?<a>\d{1,2})[ /.\-](?<b>\d{1,2})$", Opts);
    private static readonly Regex CompactDate = new(@"^(?<a>\d{2})(?<b>\d{2})(?<y>\d{4}|\d{2})$", Opts);
    private static readonly Regex WordDate = new($@"^(?:{MonthName}\.? \d{{1,2}}(?:st|nd|rd|th)?,? \d{{2,4}}|\d{{1,2}}(?:st|nd|rd|th)? {MonthName}\.?,? \d{{2,4}})$",
        Opts | RegexOptions.IgnoreCase);

    /// <summary>A date written with spaces, slashes, dashes or dots, in numbers or with a month name; month and day must make sense.</summary>
    internal static bool IsDate(string value)
    {
        value = value.Trim();
        if (WordDate.IsMatch(value)) return true;

        // OCR often reads a 0 as the letter O and a 1 as l or I; inside a date that is the likelier reading.
        value = value.Replace('O', '0').Replace('o', '0').Replace('l', '1').Replace('I', '1');

        foreach (var rx in new[] { NumericDate, YearFirstDate, CompactDate })
        {
            var m = rx.Match(value);
            if (!m.Success) continue;
            int a = int.Parse(m.Groups["a"].Value), b = int.Parse(m.Groups["b"].Value);
            if ((a is >= 1 and <= 12 && b is >= 1 and <= 31) || (a is >= 1 and <= 31 && b is >= 1 and <= 12)) return true;
        }
        return false;
    }
}
