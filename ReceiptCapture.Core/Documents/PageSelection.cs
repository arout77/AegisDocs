using System.Globalization;

namespace ReceiptCapture.Core.Documents;

/// <summary>
/// Reads the page lists people type into the page tools: "3, 1, 2, 5-7" for a new order, "2, 4-6" for pages to delete or rotate.
/// All page numbers are 1-based. Errors come back as plain-language text, never as exceptions.
/// </summary>
public static class PageSelection
{
    /// <summary>
    /// A new page order. Items are "5", "2-4", "8-" (page 8 to the end) and "5-3" (backwards). Pages may repeat.
    /// </summary>
    public static bool TryParseOrder(string? spec, int pageCount, out List<int> pages, out string error)
    {
        pages = new List<int>();
        error = "";

        if (pageCount < 1) { error = "The document has no pages."; return false; }
        if (string.IsNullOrWhiteSpace(spec)) { error = "Enter the pages in the order you want them, for example 3, 1, 2, 5-7"; return false; }

        foreach (var item in Items(spec))
        {
            if (!TryItem(item, pageCount, allowBackwards: true, out var range, out error)) return false;
            pages.AddRange(range);
        }

        if (pages.Count == 0) { error = "Enter the pages in the order you want them, for example 3, 1, 2, 5-7"; return false; }
        return true;
    }

    /// <summary>
    /// A set of pages (to delete or rotate). Items are "5", "2-4" and "8-". The order doesn't matter and repeats are ignored.
    /// </summary>
    /// <param name="blankMeansAll">When true an empty entry selects every page.</param>
    public static bool TryParseSet(string? spec, int pageCount, bool blankMeansAll, out SortedSet<int> pages, out string error)
    {
        pages = new SortedSet<int>();
        error = "";

        if (pageCount < 1) { error = "The document has no pages."; return false; }
        if (string.IsNullOrWhiteSpace(spec))
        {
            if (!blankMeansAll) { error = "Enter the pages, for example 2, 4-6"; return false; }
            foreach (var p in Enumerable.Range(1, pageCount)) pages.Add(p);
            return true;
        }

        foreach (var item in Items(spec))
        {
            if (!TryItem(item, pageCount, allowBackwards: false, out var range, out error)) return false;
            foreach (var p in range) pages.Add(p);
        }

        if (pages.Count == 0) { error = "Enter the pages, for example 2, 4-6"; return false; }
        return true;
    }

    /// <summary>The pages left after deleting some. Fails if that would delete every page.</summary>
    public static bool TryKeepAfterDelete(SortedSet<int> delete, int pageCount, out List<int> keep, out string error)
    {
        keep = Enumerable.Range(1, pageCount).Where(p => !delete.Contains(p)).ToList();
        error = "";
        if (keep.Count == 0) { error = "That would delete every page. Leave at least one."; return false; }
        return true;
    }

    /// <summary>Last page first.</summary>
    public static List<int> Reverse(int pageCount) => Enumerable.Range(1, Math.Max(0, pageCount)).Reverse().ToList();

    /// <summary>A page's turn after turning it further clockwise by <paramref name="add"/> degrees: always 0, 90, 180 or 270.</summary>
    public static int Rotated(int current, int add)
    {
        int total = (current + add) % 360;
        return total < 0 ? total + 360 : total;
    }

    // ------------------------------------------------------------------

    private static IEnumerable<string> Items(string spec)
        => spec.Split(new[] { ',', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool TryItem(string item, int pageCount, bool allowBackwards, out List<int> range, out string error)
    {
        range = new List<int>();
        error = "";
        int first, last;
        var dash = item.IndexOf('-');

        if (dash < 0)
        {
            if (!TryNumber(item, out first)) { error = $"\"{item}\" isn't a page number."; return false; }
            last = first;
        }
        else
        {
            var left = item[..dash].Trim();
            var right = item[(dash + 1)..].Trim();
            if (!TryNumber(left, out first)) { error = $"\"{item}\" isn't a valid range."; return false; }
            if (right.Length == 0) last = pageCount;
            else if (!TryNumber(right, out last)) { error = $"\"{item}\" isn't a valid range."; return false; }
        }

        if (first < 1 || first > pageCount || last < 1 || last > pageCount)
        {
            var bad = first < 1 || first > pageCount ? first : last;
            error = $"Page {bad} doesn't exist (the document has {pageCount} pages).";
            return false;
        }

        if (first > last)
        {
            if (!allowBackwards) { error = $"\"{item}\" runs backwards."; return false; }
            for (int p = first; p >= last; p--) range.Add(p);
        }
        else
        {
            for (int p = first; p <= last; p++) range.Add(p);
        }
        return true;
    }

    private static bool TryNumber(string text, out int value)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
