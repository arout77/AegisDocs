using System.Text;

namespace ReceiptCapture.Core.Redaction;

/// <summary>Rebuilds a page's text from OCR words, remembering where each word sits inside it.</summary>
public static class PageTextBuilder
{
    public sealed record Built(string Text, IReadOnlyList<WordBox> Words, IReadOnlyList<(int Start, int End)> Spans);

    public static Built Build(IReadOnlyList<WordBox> words)
    {
        var ordered = CombFieldMerger.Merge(words).Where(w => !string.IsNullOrWhiteSpace(w.Text))
                           .OrderBy(w => w.Line).ThenBy(w => w.X)
                           .ToList();

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
        return new Built(text.ToString(), ordered, spans);
    }
}
