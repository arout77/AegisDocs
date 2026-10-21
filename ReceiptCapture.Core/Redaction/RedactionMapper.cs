using System.Text;

namespace ReceiptCapture.Core.Redaction;

/// <summary>Turns OCR words into black-box regions covering the sensitive text found in them.</summary>
public static class RedactionMapper
{
    public static List<RedactionRegion> FindRegions(
        int page, IReadOnlyList<WordBox> words, FindOptions options, double padding = 3)
    {
        var ordered = words.Where(w => !string.IsNullOrWhiteSpace(w.Text))
                           .OrderBy(w => w.Line).ThenBy(w => w.X)
                           .ToList();

        // Rebuild the page text, remembering where each word sits inside it.
        var text = new StringBuilder();
        var spans = new List<(int Start, int End)>();
        int previousLine = int.MinValue;
        foreach (var word in ordered)
        {
            if (text.Length > 0) text.Append(word.Line == previousLine ? ' ' : '\n');
            int start = text.Length;
            text.Append(word.Text);
            spans.Add((start, text.Length));
            previousLine = word.Line;
        }

        var pageText = text.ToString();
        var regions = new List<RedactionRegion>();

        foreach (var match in SensitiveDataFinder.Find(pageText, options))
        {
            var hitWords = Enumerable.Range(0, ordered.Count)
                .Where(i => spans[i].Start < match.End && match.Start < spans[i].End)
                .Select(i => ordered[i])
                .ToList();

            // A match that runs over a line break gets one box per line.
            foreach (var line in hitWords.GroupBy(w => w.Line))
            {
                double left = Math.Max(0, line.Min(w => w.X) - padding);
                double top = Math.Max(0, line.Min(w => w.Y) - padding);
                double right = line.Max(w => w.X + w.Width) + padding;
                double bottom = line.Max(w => w.Y + w.Height) + padding;

                regions.Add(new RedactionRegion(
                    page, match.Kind, pageText.Substring(match.Start, match.Length),
                    left, top, right - left, bottom - top));
            }
        }
        return regions;
    }
}
