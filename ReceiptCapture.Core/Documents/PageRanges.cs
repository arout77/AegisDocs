using System.Globalization;

namespace ReceiptCapture.Core.Documents;

/// <summary>Parses page selections like "1-3, 5, 8-" into groups of 1-based page numbers.</summary>
public static class PageRanges
{
    /// <summary>
    /// Each comma-separated item becomes one group (and so one output file when splitting).
    /// Supported items: "5", "2-4", and "8-" (page 8 to the end).
    /// </summary>
    public static bool TryParseGroups(string? spec, int pageCount, out List<List<int>> groups, out string error)
    {
        groups = new List<List<int>>();
        error = "";

        if (pageCount < 1) { error = "The document has no pages."; return false; }
        if (string.IsNullOrWhiteSpace(spec)) { error = "Enter the pages to extract, for example 1-3, 5, 8-"; return false; }

        foreach (var rawItem in spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int first, last;
            var dash = rawItem.IndexOf('-');

            if (dash < 0)
            {
                if (!TryNumber(rawItem, out first)) { error = $"\"{rawItem}\" isn't a page number."; return false; }
                last = first;
            }
            else
            {
                var left = rawItem[..dash].Trim();
                var right = rawItem[(dash + 1)..].Trim();

                if (!TryNumber(left, out first)) { error = $"\"{rawItem}\" isn't a valid range."; return false; }

                if (right.Length == 0) last = pageCount;
                else if (!TryNumber(right, out last)) { error = $"\"{rawItem}\" isn't a valid range."; return false; }
            }

            if (first < 1 || last < 1 || first > pageCount || last > pageCount)
            {
                var bad = first > pageCount || first < 1 ? first : last;
                error = $"Page {bad} doesn't exist (the document has {pageCount} pages).";
                return false;
            }
            if (first > last) { error = $"\"{rawItem}\" runs backwards."; return false; }

            groups.Add(Enumerable.Range(first, last - first + 1).ToList());
        }

        if (groups.Count == 0) { error = "Enter the pages to extract, for example 1-3, 5, 8-"; return false; }
        return true;
    }

    /// <summary>Splits a document into consecutive chunks of n pages (the last chunk may be shorter).</summary>
    public static List<List<int>> EveryN(int n, int pageCount)
    {
        if (n < 1) throw new ArgumentOutOfRangeException(nameof(n), "Chunk size must be at least 1.");
        var result = new List<List<int>>();
        for (int start = 1; start <= pageCount; start += n)
            result.Add(Enumerable.Range(start, Math.Min(n, pageCount - start + 1)).ToList());
        return result;
    }

    private static bool TryNumber(string text, out int value)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
