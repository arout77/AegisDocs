using System.Text;
using System.Text.RegularExpressions;

namespace ReceiptCapture.Core.Comparison;

/// <summary>One word of a document, with where it sat (for showing the result).</summary>
/// <param name="Text">The word as written.</param>
/// <param name="ParagraphEnd">True when this is the last word of its paragraph (or page, for scanned files).</param>
/// <param name="Page">The page number, when the source has pages (PDFs); otherwise null.</param>
public sealed record TextToken(string Text, bool ParagraphEnd, int? Page);

/// <summary>Turns a document's text into the words that get compared.</summary>
public static partial class DocumentText
{
    /// <summary>Words from paragraphs of text (a Word file, a text file).</summary>
    public static List<TextToken> FromParagraphs(IEnumerable<string> paragraphs)
        => Tokenize(paragraphs.Select((p, i) => (p, (int?)null)));

    /// <summary>Words from the text of each page (a PDF read with OCR). Each page ends a paragraph, because line breaks in a scan mean nothing.</summary>
    public static List<TextToken> FromPages(IReadOnlyList<string> pageTexts)
        => Tokenize(pageTexts.Select((p, i) => (p, (int?)(i + 1))));

    /// <summary>The words as they are compared: curly quotes, long dashes and non-breaking spaces made plain, so Word's autocorrect isn't a "change".</summary>
    public static string Key(string word, bool ignoreCase)
    {
        var sb = new StringBuilder(word.Length);
        foreach (var c in word)
        {
            switch (c)
            {
                case '\u2018': case '\u2019': case '\u201B': case '\u2032': sb.Append('\''); break;
                case '\u201C': case '\u201D': case '\u201F': case '\u2033': sb.Append('"'); break;
                case '\u2010': case '\u2011': case '\u2012': case '\u2013': case '\u2014': case '\u2015': case '\u2212': sb.Append('-'); break;
                case '\u00AD': case '\u200B': case '\uFEFF': break;       // soft hyphen, zero-width space, byte order mark
                default: sb.Append(ignoreCase ? char.ToLowerInvariant(c) : c); break;
            }
        }
        return sb.ToString();
    }

    private static List<TextToken> Tokenize(IEnumerable<(string Text, int? Page)> blocks)
    {
        var tokens = new List<TextToken>();
        foreach (var (text, page) in blocks)
        {
            int before = tokens.Count;
            foreach (Match m in WordPattern().Matches(text ?? ""))
                tokens.Add(new TextToken(m.Value, false, page));

            if (tokens.Count > before)
                tokens[^1] = tokens[^1] with { ParagraphEnd = true };
        }
        return tokens;
    }

    [GeneratedRegex(@"\S+")]
    private static partial Regex WordPattern();
}
