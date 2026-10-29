namespace ReceiptCapture.Core.Redaction;

/// <summary>
/// Government forms (the I-90, tax forms, intake forms) print numbers in "comb" fields: one box per digit.
/// OCR then returns the number as nine separate one-digit words, often with the box edges read as "|" or "[",
/// so the text reads "2 1 4 1 9 2 2 0 8" and no pattern for a Social Security number can match it.
/// This puts such a row of boxes back together as one word (with a box that covers the whole row),
/// before any text is searched. Everything downstream, from the detectors to the black box that gets drawn,
/// then sees one number in one place.
/// </summary>
public static class CombFieldMerger
{
    /// <summary>What OCR reads from the edges of a box.</summary>
    private const string BorderMarks = "|[]!\u00A6\u2016";

    /// <summary>Separators that can sit between boxes (the slashes of a date, the dashes of a Social Security number).</summary>
    private const string Separators = "/-\u2013\u2014.";

    /// <summary>Fewer digits than this in a row is ordinary text (page numbers, a list), not a comb field.</summary>
    private const int MinDigits = 4;

    private enum Kind { Digit, Maybe, Border, Separator }

    private sealed record Token(int Index, WordBox Word, Kind Kind, string Text)
    {
        public double CenterY => Word.Y + Word.Height / 2;
        public double Right => Word.X + Word.Width;
    }

    public static IReadOnlyList<WordBox> Merge(IReadOnlyList<WordBox> words)
    {
        var tokens = new List<Token>();
        for (int i = 0; i < words.Count; i++)
            if (Classify(words[i].Text) is { } c)
                tokens.Add(new Token(i, words[i], c.Kind, c.Text));
        if (tokens.Count < 2 && !tokens.Any(t => t.Text.Length >= MinDigits)) return words;

        var remove = new HashSet<int>();
        var added = new List<WordBox>();

        foreach (var row in Rows(tokens))
        {
            var run = new List<Token>();
            foreach (var token in row)
            {
                // A wide gap ends a run: the next boxes belong to some other field.
                if (run.Count > 0 && token.Word.X - run[^1].Right > 3 * Math.Max(token.Word.Height, run[^1].Word.Height))
                {
                    Close(run, remove, added);
                    run.Clear();
                }
                run.Add(token);
            }
            Close(run, remove, added);
        }

        if (added.Count == 0) return words;
        var result = new List<WordBox>(words.Count);
        for (int i = 0; i < words.Count; i++)
            if (!remove.Contains(i)) result.Add(words[i]);
        result.AddRange(added);
        return result;
    }

    // ------------------------------------------------------------------

    private static void Close(List<Token> run, HashSet<int> remove, List<WordBox> added)
    {
        // A run has to start and end on a digit; separators, box edges and doubtful letters at the ends are left alone.
        int first = 0, last = run.Count - 1;
        while (first <= last && run[first].Kind != Kind.Digit) first++;
        while (last >= first && run[last].Kind != Kind.Digit) last--;
        if (first > last) return;

        var used = run.GetRange(first, last - first + 1);
        int digits = used.Where(t => t.Kind == Kind.Digit).Sum(t => t.Text.Length);
        if (digits < MinDigits) return;

        var text = new System.Text.StringBuilder();
        foreach (var t in used)
        {
            if (t.Kind == Kind.Border) continue;
            text.Append(t.Kind == Kind.Maybe ? MaybeDigit(t.Text[0]) : t.Text);
        }

        var cells = used.Where(t => t.Kind != Kind.Border).ToList();
        double left = cells.Min(t => t.Word.X), top = cells.Min(t => t.Word.Y);
        double right = cells.Max(t => t.Right), bottom = cells.Max(t => t.Word.Y + t.Word.Height);

        foreach (var t in used) remove.Add(t.Index);
        added.Add(new WordBox(text.ToString(), left, top, right - left, bottom - top, used[0].Word.Line));
    }

    /// <summary>Groups tokens into visual rows (OCR's own line numbers are no help: each box may come back as its own line).</summary>
    private static IEnumerable<List<Token>> Rows(List<Token> tokens)
    {
        var rows = new List<List<Token>>();
        foreach (var t in tokens.OrderBy(t => t.CenterY))
        {
            var row = rows.Count > 0 ? rows[^1] : null;
            if (row != null && Math.Abs(t.CenterY - row.Average(r => r.CenterY)) <= 0.6 * Math.Max(t.Word.Height, row[0].Word.Height))
                row.Add(t);
            else
                rows.Add(new List<Token> { t });
        }
        return rows.Select(r => r.OrderBy(t => t.Word.X).ToList());
    }

    private static (Kind Kind, string Text)? Classify(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        var stripped = new string(trimmed.Where(c => BorderMarks.IndexOf(c) < 0 && !char.IsWhiteSpace(c)).ToArray());

        if (stripped.Length == 0) return (Kind.Border, "");

        // Borders stuck to the ends ("|2") don't count; a border in the middle ("2|1|4") means several boxes read as one word.
        var core = trimmed.Trim(BorderMarks.ToCharArray());
        bool borderInside = core.IndexOfAny(BorderMarks.ToCharArray()) >= 0;

        if (stripped.All(c => c is >= '0' and <= '9'))
            return stripped.Length == 1 || borderInside ? (Kind.Digit, stripped) : null;

        if (stripped.Length == 1)
        {
            if (Separators.IndexOf(stripped[0]) >= 0) return (Kind.Separator, stripped);
            if ("OolI".IndexOf(stripped[0]) >= 0) return (Kind.Maybe, stripped);
        }
        return null;
    }

    /// <summary>Inside a row of digits, the likelier reading of O is 0 and of l or I is 1.</summary>
    private static char MaybeDigit(char c) => c is 'O' or 'o' ? '0' : '1';
}
