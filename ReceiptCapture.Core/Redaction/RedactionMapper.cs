namespace ReceiptCapture.Core.Redaction;

/// <summary>Turns OCR words into black-box regions covering the sensitive text found in them.</summary>
public static class RedactionMapper
{
    public static List<RedactionRegion> FindRegions(
        int page, IReadOnlyList<WordBox> words, FindOptions options, double padding = 3)
    {
        var built = PageTextBuilder.Build(words);
        var ordered = built.Words;
        var spans = built.Spans;
        var pageText = built.Text;
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
