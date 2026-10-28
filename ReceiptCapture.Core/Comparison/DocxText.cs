using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace ReceiptCapture.Core.Comparison;

/// <summary>
/// Reads the text of a Word (.docx) file straight from its XML, without needing Word installed.
/// Gives the "final" text: tracked deletions are left out and tracked insertions are kept, so what you
/// compare is what the document reads like with all changes accepted.
/// Headers and footers are not included. Footnotes and endnotes are, after the main text.
/// </summary>
public static class DocxText
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    /// <summary>The paragraphs of the file, in reading order, with empty ones dropped.</summary>
    public static List<string> ReadParagraphs(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var paragraphs = new List<string>();

            var body = zip.GetEntry("word/document.xml")
                ?? throw new InvalidDataException("This doesn't look like a Word document (it has no document.xml).");
            paragraphs.AddRange(ParagraphsOf(Load(body)));

            foreach (var notesPart in new[] { "word/footnotes.xml", "word/endnotes.xml" })
            {
                var entry = zip.GetEntry(notesPart);
                if (entry != null) paragraphs.AddRange(ParagraphsOf(Load(entry), skipSeparators: true));
            }
            return paragraphs;
        }
        catch (InvalidDataException) when (IsNotZip(path))
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} couldn't be read as a Word document. It may be damaged, password-protected, or an older .doc file. " +
                "Open it in Word and save a copy as .docx or PDF.");
        }
    }

    /// <summary>Paragraphs from XML text. Public so it can be tested without building a zip.</summary>
    public static List<string> ParagraphsFromXml(string xml, bool skipSeparators = false)
        => ParagraphsOf(XDocument.Parse(xml), skipSeparators);

    // ------------------------------------------------------------------

    private static XDocument Load(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static bool IsNotZip(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var head = new byte[2];
            return stream.Read(head, 0, 2) != 2 || head[0] != (byte)'P' || head[1] != (byte)'K';
        }
        catch (IOException) { return true; }
    }

    private static List<string> ParagraphsOf(XDocument doc, bool skipSeparators = false)
    {
        var result = new List<string>();
        if (doc.Root == null) return result;

        foreach (var paragraph in doc.Root.Descendants(W + "p"))
        {
            // A footnote's separator lines are Word's own furniture, not text.
            if (skipSeparators && paragraph.Ancestors(W + "footnote").Concat(paragraph.Ancestors(W + "endnote"))
                    .Any(n => (string?)n.Attribute(W + "type") is "separator" or "continuationSeparator" or "continuationNotice"))
                continue;

            // Paragraphs inside a text box are reached as part of the paragraph that holds the box in some files, and on
            // their own in others. Only count a paragraph that isn't inside another paragraph's alternative content.
            if (paragraph.Ancestors(Mc + "Fallback").Any()) continue;

            var text = new StringBuilder();
            Append(paragraph, text);
            var line = Collapse(text.ToString());
            if (line.Length > 0) result.Add(line);
        }
        return result;
    }

    private static void Append(XElement element, StringBuilder text)
    {
        foreach (var child in element.Elements())
        {
            var name = child.Name;

            // Tracked deletions and moved-away text are not part of the final document.
            if (name == W + "del" || name == W + "moveFrom" || name == Mc + "Fallback") continue;
            // A nested paragraph (in a text box) is handled as its own paragraph.
            if (name == W + "p") continue;

            if (name == W + "t") text.Append(child.Value);
            else if (name == W + "tab" || name == W + "br" || name == W + "cr") text.Append(' ');
            else if (name == W + "noBreakHyphen") text.Append('-');
            else Append(child, text);
        }
    }

    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c) || c == '\u00A0') { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
