using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ReceiptCapture.Core.Compliance;

namespace ReceiptCapture.Core.HiddenContent;

/// <summary>
/// Looks inside a .docx (which is a zip of XML files) for things that travel with the document without being
/// visible on its pages: author names, comments, tracked changes (including deleted text), hidden text,
/// embedded files, a preview picture of the first page.
/// </summary>
public static class DocxInspector
{
    internal static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    internal static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Ext = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";
    private static readonly XNamespace Cust = "http://schemas.openxmlformats.org/officeDocument/2006/custom-properties";

    private static readonly Regex BodyPartRx = new(
        @"^word/(document|footnotes|endnotes|header\d*|footer\d*)\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsBodyPart(string name) => BodyPartRx.IsMatch(name);

    internal static readonly string[] TrackChangeMarkers = { "ins", "del", "moveFrom", "moveTo" };
    internal static readonly string[] FormattingChanges =
        { "rPrChange", "pPrChange", "sectPrChange", "tblPrChange", "tblGridChange", "tcPrChange", "trPrChange", "tblPrExChange", "numberingChange" };

    public static HiddenInspection Inspect(Stream docx)
    {
        using var zip = new ZipArchive(docx, ZipArchiveMode.Read, leaveOpen: true);
        var items = new List<HiddenItem>();
        var texts = new List<HiddenText>();

        InspectCoreProperties(zip, items, texts);
        InspectExtendedProperties(zip, items);
        InspectCustomProperties(zip, items, texts);
        InspectComments(zip, items, texts);
        InspectBody(zip, items, texts);
        InspectParts(zip, items);

        return new HiddenInspection(items, texts);
    }

    // ------------------------------------------------------------------
    // Properties
    // ------------------------------------------------------------------
    private static void InspectCoreProperties(ZipArchive zip, List<HiddenItem> items, List<HiddenText> texts)
    {
        var doc = Load(zip, "docProps/core.xml");
        if (doc?.Root == null) return;
        const string where = "Document properties";

        void Names(XName name, string label)
        {
            var value = doc.Root.Element(name)?.Value.Trim();
            if (!string.IsNullOrEmpty(value))
                items.Add(new HiddenItem(HiddenCategory.DocumentProperties, FindingSeverity.Medium, $"{label}: {MatchMasker.MaskName(value)}", where));
        }
        Names(Dc + "creator", "Author");
        Names(Cp + "lastModifiedBy", "Last saved by");

        void Descriptive(XName name, string label, FindingSeverity severity)
        {
            var value = doc.Root.Element(name)?.Value.Trim();
            if (string.IsNullOrEmpty(value)) return;
            items.Add(new HiddenItem(HiddenCategory.DocumentProperties, severity, $"{label}: {MatchMasker.MaskName(value)}", where));
            texts.Add(new HiddenText(where, value));
        }
        Descriptive(Dc + "title", "Title", FindingSeverity.Low);
        Descriptive(Dc + "subject", "Subject", FindingSeverity.Low);
        Descriptive(Cp + "keywords", "Keywords", FindingSeverity.Low);
        Descriptive(Cp + "category", "Category", FindingSeverity.Low);
        Descriptive(Dc + "description", "Comments field", FindingSeverity.Medium);
    }

    private static void InspectExtendedProperties(ZipArchive zip, List<HiddenItem> items)
    {
        var doc = Load(zip, "docProps/app.xml");
        if (doc?.Root == null) return;
        foreach (var (name, label) in new[] { ("Company", "Company"), ("Manager", "Manager") })
        {
            var value = doc.Root.Element(Ext + name)?.Value.Trim();
            if (!string.IsNullOrEmpty(value))
                items.Add(new HiddenItem(HiddenCategory.DocumentProperties, FindingSeverity.Medium, $"{label}: {MatchMasker.MaskName(value)}", "Document properties"));
        }
    }

    private static void InspectCustomProperties(ZipArchive zip, List<HiddenItem> items, List<HiddenText> texts)
    {
        var doc = Load(zip, "docProps/custom.xml");
        var props = doc?.Root?.Elements(Cust + "property").ToList();
        if (props == null || props.Count == 0) return;

        items.Add(new HiddenItem(HiddenCategory.DocumentProperties, FindingSeverity.Medium,
            $"{props.Count} custom propert{(props.Count == 1 ? "y" : "ies")} stored in the file", "Document properties"));
        foreach (var p in props)
        {
            var value = string.Concat(p.Descendants().Where(e => !e.HasElements).Select(e => e.Value)).Trim();
            if (value.Length > 0) texts.Add(new HiddenText("Document properties", value));
        }
    }

    // ------------------------------------------------------------------
    // Comments
    // ------------------------------------------------------------------
    private static void InspectComments(ZipArchive zip, List<HiddenItem> items, List<HiddenText> texts)
    {
        var doc = Load(zip, "word/comments.xml");
        var comments = doc?.Root?.Elements(W + "comment").ToList();
        if (comments == null || comments.Count == 0) return;

        var authors = comments.Select(c => (string?)c.Attribute(W + "author")).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().ToList();
        items.Add(new HiddenItem(HiddenCategory.Comments, FindingSeverity.Medium,
            $"{comments.Count} comment{(comments.Count == 1 ? "" : "s")}{ByAuthors(authors)}", "Comments"));

        foreach (var c in comments)
        {
            var text = ParagraphText(c, W + "t");
            if (text.Length > 0) texts.Add(new HiddenText("Comments", text));
        }
    }

    // ------------------------------------------------------------------
    // Tracked changes and hidden text
    // ------------------------------------------------------------------
    private static void InspectBody(ZipArchive zip, List<HiddenItem> items, List<HiddenText> texts)
    {
        int insertions = 0, deletions = 0, formatting = 0, hiddenRuns = 0;
        var authors = new HashSet<string>();
        var deletedTexts = new List<string>();
        var hiddenTexts = new List<string>();

        foreach (var entry in zip.Entries.Where(e => IsBodyPart(e.FullName)))
        {
            var doc = LoadEntry(entry);
            if (doc?.Root == null) continue;

            foreach (var e in doc.Descendants())
            {
                if (e.Name.Namespace != W) continue;
                var local = e.Name.LocalName;

                if (local is "ins" or "moveTo") { insertions++; AddAuthor(e); }
                else if (local is "del" or "moveFrom")
                {
                    deletions++;
                    AddAuthor(e);
                    var text = string.Concat(e.Descendants().Where(d => d.Name == W + "delText" || (local == "moveFrom" && d.Name == W + "t")).Select(d => d.Value));
                    if (text.Trim().Length > 0 && local == "del") deletedTexts.Add(text.Trim());
                }
                else if (Array.IndexOf(FormattingChanges, local) >= 0) formatting++;
                else if (local == "r" && IsHiddenRun(e))
                {
                    hiddenRuns++;
                    var text = string.Concat(e.Elements().Where(d => d.Name == W + "t" || d.Name == W + "delText").Select(d => d.Value));
                    if (text.Trim().Length > 0) hiddenTexts.Add(text.Trim());
                }
            }
        }

        void AddAuthor(XElement e)
        {
            var a = (string?)e.Attribute(W + "author");
            if (!string.IsNullOrWhiteSpace(a)) authors.Add(a);
        }

        var byWho = ByAuthors(authors.ToList());
        if (deletions > 0)
            items.Add(new HiddenItem(HiddenCategory.TrackedChanges, FindingSeverity.High,
                $"{deletions} tracked deletion{(deletions == 1 ? "" : "s")}: the deleted text can still be read{byWho}", "Tracked changes"));
        if (insertions > 0)
            items.Add(new HiddenItem(HiddenCategory.TrackedChanges, FindingSeverity.Medium,
                $"{insertions} tracked insertion{(insertions == 1 ? "" : "s")}{(deletions == 0 ? byWho : "")}", "Tracked changes"));
        if (formatting > 0)
            items.Add(new HiddenItem(HiddenCategory.TrackedChanges, FindingSeverity.Low,
                $"{formatting} tracked formatting change{(formatting == 1 ? "" : "s")}", "Tracked changes"));
        if (deletedTexts.Count > 0) texts.Add(new HiddenText("Deleted text (tracked changes)", string.Join("\n", deletedTexts)));

        if (hiddenRuns > 0)
            items.Add(new HiddenItem(HiddenCategory.HiddenText, FindingSeverity.Medium,
                $"{hiddenRuns} piece{(hiddenRuns == 1 ? "" : "s")} of text formatted as hidden", "Hidden text"));
        if (hiddenTexts.Count > 0) texts.Add(new HiddenText("Hidden text", string.Join("\n", hiddenTexts)));
    }

    /// <summary>A run is hidden when its own formatting has a "vanish" switch that is not turned off.</summary>
    internal static bool IsHiddenRun(XElement run)
    {
        var vanish = run.Element(W + "rPr")?.Element(W + "vanish");
        if (vanish == null) return false;
        var val = ((string?)vanish.Attribute(W + "val"))?.ToLowerInvariant();
        return val is not ("0" or "false" or "off");
    }

    // ------------------------------------------------------------------
    // Other parts of the package
    // ------------------------------------------------------------------
    private static void InspectParts(ZipArchive zip, List<HiddenItem> items)
    {
        var embedded = zip.Entries
            .Where(e => e.FullName.StartsWith("word/embeddings/", StringComparison.OrdinalIgnoreCase) && e.Length > 0 && !e.FullName.EndsWith("/"))
            .Select(e => e.Name).ToList();
        if (embedded.Count > 0)
        {
            var shown = string.Join(", ", embedded.Take(4).Select(MatchMasker.MaskFileName)) + (embedded.Count > 4 ? $" and {embedded.Count - 4} more" : "");
            items.Add(new HiddenItem(HiddenCategory.Attachments, FindingSeverity.High,
                $"{embedded.Count} embedded file{(embedded.Count == 1 ? "" : "s")} or object{(embedded.Count == 1 ? "" : "s")}: {shown}", "Attachments"));
        }

        if (zip.Entries.Any(e => e.FullName.StartsWith("docProps/thumbnail.", StringComparison.OrdinalIgnoreCase)))
            items.Add(new HiddenItem(HiddenCategory.PreviewImage, FindingSeverity.Medium,
                "A small picture of the first page is stored in the file", "Preview image"));

        if (zip.Entries.Any(e => e.FullName.Equals("word/vbaProject.bin", StringComparison.OrdinalIgnoreCase)))
            items.Add(new HiddenItem(HiddenCategory.Scripts, FindingSeverity.High, "Macros (VBA code) are stored in the file", "Scripts"));

        if (zip.Entries.Any(e => e.FullName.StartsWith("customXml/", StringComparison.OrdinalIgnoreCase) && e.Name.StartsWith("item", StringComparison.OrdinalIgnoreCase)
                                 && e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
            items.Add(new HiddenItem(HiddenCategory.Other, FindingSeverity.Low, "Custom XML data is attached to the file (document-management systems and forms can keep client or matter details there)", "Custom XML"));
    }

    // ------------------------------------------------------------------
    // Helpers shared with the cleaner
    // ------------------------------------------------------------------
    internal static XDocument? Load(ZipArchive zip, string name)
    {
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
        return entry == null ? null : LoadEntry(entry);
    }

    private static XDocument? LoadEntry(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            return XDocument.Load(stream);
        }
        catch (System.Xml.XmlException) { return null; }
    }

    private static string ParagraphText(XElement container, XName textName)
    {
        var paragraphs = container.Descendants(W + "p")
            .Select(p => string.Concat(p.Descendants(textName).Select(t => t.Value)).Trim())
            .Where(t => t.Length > 0);
        return string.Join("\n", paragraphs);
    }

    private static string ByAuthors(IReadOnlyList<string?> authors)
    {
        var names = authors.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => MatchMasker.MaskName(a!)).Take(4).ToList();
        if (names.Count == 0) return "";
        return " by " + string.Join(", ", names) + (authors.Count > 4 ? $" and {authors.Count - 4} more" : "");
    }
}
