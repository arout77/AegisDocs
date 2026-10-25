using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ReceiptCapture.Core.HiddenContent;

/// <summary>
/// Writes a copy of a .docx with the chosen hidden content taken out. The original is never touched. Parts that are
/// removed are also removed from the package's content-type list and relationship files, so Word still opens the copy.
/// </summary>
public static class DocxCleaner
{
    private static readonly XNamespace W = DocxInspector.W;
    private static readonly XNamespace R = DocxInspector.R;
    private static readonly XNamespace Cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Ext = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";
    private static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static CleanResult Clean(Stream input, Stream output, CleanOptions options)
    {
        // The package is small enough to hold in memory, which keeps reading and writing simple.
        var parts = new List<(string Name, byte[] Data)>();
        using (var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true))
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith("/")) continue;
                using var ms = new MemoryStream();
                using (var s = entry.Open()) s.CopyTo(ms);
                parts.Add((entry.FullName, ms.ToArray()));
            }
        }

        var removed = DecideRemovedParts(parts.Select(p => p.Name), options);
        var summary = new List<string>();

        // Which relationship ids (per part) point at parts we are about to delete: the markup using them must go too.
        var deadRelIds = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, data) in parts.Where(p => p.Name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            var sourcePart = SourcePartOf(name);
            var ids = ReadRels(data).Where(r => !r.External && removed.Contains(Resolve(sourcePart, r.Target))).Select(r => r.Id).ToHashSet();
            if (ids.Count > 0) deadRelIds[sourcePart] = ids;
        }

        int comments = 0, deletedPassages = 0, acceptedChanges = 0, hiddenRuns = 0, objects = 0;
        var cleaned = new List<(string Name, byte[] Data)>();

        foreach (var (name, data) in parts)
        {
            if (removed.Contains(name) || IsRelsOfRemovedPart(name, removed)) continue;

            byte[] result = data;
            if (DocxInspector.IsBodyPart(name))
            {
                deadRelIds.TryGetValue(name, out var dead);
                result = CleanBodyPart(data, options, dead, ref comments, ref deletedPassages, ref acceptedChanges, ref hiddenRuns, ref objects);
            }
            else if (name.Equals("docProps/core.xml", StringComparison.OrdinalIgnoreCase) && options.DocumentProperties)
                result = ClearCoreProperties(data);
            else if (name.Equals("docProps/app.xml", StringComparison.OrdinalIgnoreCase) && options.DocumentProperties)
                result = ClearExtendedProperties(data);
            else if (name.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
                result = DropContentTypeOverrides(data, removed);
            else if (name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                result = DropRelationships(data, SourcePartOf(name), removed);

            cleaned.Add((name, result));
        }

        // Describe what was done, in the order a person would expect.
        int commentParts = removed.Count(r => r.StartsWith("word/comments", StringComparison.OrdinalIgnoreCase));
        if (options.Comments && (commentParts > 0 || comments > 0)) summary.Add("Removed all comments");
        if (options.TrackedChanges && acceptedChanges + deletedPassages > 0)
            summary.Add($"Accepted all tracked changes ({deletedPassages} deleted passage{(deletedPassages == 1 ? "" : "s")} removed for good)");
        if (options.HiddenText && hiddenRuns > 0) summary.Add($"Deleted {hiddenRuns} piece{(hiddenRuns == 1 ? "" : "s")} of hidden text");
        if (options.DocumentProperties) summary.Add("Cleared author, title and other document properties");
        if (options.DocumentProperties && removed.Any(r => r.StartsWith("customXml/", StringComparison.OrdinalIgnoreCase)))
            summary.Add("Removed custom XML data (where document-management systems keep their details)");
        if (options.Attachments && removed.Any(r => r.StartsWith("word/embeddings/", StringComparison.OrdinalIgnoreCase)))
            summary.Add($"Removed embedded files and objects ({Math.Max(objects, 1)} in the text)");
        if (options.PreviewImages && removed.Any(r => r.StartsWith("docProps/thumbnail.", StringComparison.OrdinalIgnoreCase)))
            summary.Add("Removed the first-page preview picture");

        using var outZip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var (name, data) in cleaned.OrderBy(p => p.Name.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            var entry = outZip.CreateEntry(name, CompressionLevel.Optimal);
            using var s = entry.Open();
            s.Write(data);
        }

        return new CleanResult(summary);
    }

    // ------------------------------------------------------------------
    // Which parts go
    // ------------------------------------------------------------------
    private static HashSet<string> DecideRemovedParts(IEnumerable<string> names, CleanOptions o)
    {
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (o.Comments && System.Text.RegularExpressions.Regex.IsMatch(name, @"^word/comments\w*\.xml$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                removed.Add(name);
            if ((o.Comments || o.DocumentProperties) && name.Equals("word/people.xml", StringComparison.OrdinalIgnoreCase))
                removed.Add(name);
            if (o.PreviewImages && name.StartsWith("docProps/thumbnail.", StringComparison.OrdinalIgnoreCase))
                removed.Add(name);
            if (o.Attachments && name.StartsWith("word/embeddings/", StringComparison.OrdinalIgnoreCase))
                removed.Add(name);
            if (o.DocumentProperties && name.Equals("docProps/custom.xml", StringComparison.OrdinalIgnoreCase))
                removed.Add(name);
            // Custom XML: where document-management systems and forms keep their details (matter, client, status...).
            if (o.DocumentProperties && name.StartsWith("customXml/", StringComparison.OrdinalIgnoreCase))
                removed.Add(name);
        }
        return removed;
    }

    /// <summary>"word/_rels/comments.xml.rels" belongs to "word/comments.xml": if that part goes, so does its relationship file.</summary>
    private static bool IsRelsOfRemovedPart(string name, HashSet<string> removed)
    {
        if (!name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) return false;
        var source = SourcePartOf(name);
        return source.Length > 0 && removed.Contains(source);
    }

    // ------------------------------------------------------------------
    // Body parts
    // ------------------------------------------------------------------
    private static byte[] CleanBodyPart(byte[] data, CleanOptions o, HashSet<string>? deadRelIds,
        ref int comments, ref int deletedPassages, ref int acceptedChanges, ref int hiddenRuns, ref int objects)
    {
        XDocument doc;
        try { doc = Parse(data); }
        catch (XmlException) { return data; }
        if (doc.Root == null) return data;

        bool changed = false;

        if (o.Comments)
        {
            var marks = doc.Descendants().Where(e => e.Name.Namespace == W &&
                e.Name.LocalName is "commentRangeStart" or "commentRangeEnd" or "commentReference").ToList();
            foreach (var m in marks) m.Remove();
            comments += marks.Count;
            changed |= marks.Count > 0;
        }

        if (o.TrackedChanges)
        {
            // Rows that were deleted while tracking: accepting the change removes the whole row.
            foreach (var row in doc.Descendants(W + "tr").Where(r => r.Element(W + "trPr")?.Element(W + "del") != null).ToList())
            { row.Remove(); deletedPassages++; changed = true; }

            // Markers inside formatting blocks (a tracked paragraph mark, say) are simply dropped.
            var markerParents = new[] { "rPr", "trPr", "numPr" };
            var markers = doc.Descendants().Where(e => e.Name.Namespace == W && Array.IndexOf(DocxInspector.TrackChangeMarkers, e.Name.LocalName) >= 0
                                                       && e.Parent != null && Array.IndexOf(markerParents, e.Parent.Name.LocalName) >= 0).ToList();
            foreach (var m in markers) m.Remove();
            acceptedChanges += markers.Count;
            changed |= markers.Count > 0;

            // Deleted and moved-away text goes for good.
            var deletions = doc.Descendants().Where(e => e.Name.Namespace == W && e.Name.LocalName is "del" or "moveFrom").ToList();
            foreach (var d in deletions) d.Remove();
            deletedPassages += deletions.Count;
            changed |= deletions.Count > 0;

            // Inserted and moved-in text stays, without the wrapper.
            var insertions = doc.Descendants().Where(e => e.Name.Namespace == W && e.Name.LocalName is "ins" or "moveTo").ToList();
            foreach (var i in insertions) i.ReplaceWith(i.Nodes().ToList());
            acceptedChanges += insertions.Count;
            changed |= insertions.Count > 0;

            // Records of what the formatting used to be.
            var history = doc.Descendants().Where(e => e.Name.Namespace == W &&
                (Array.IndexOf(DocxInspector.FormattingChanges, e.Name.LocalName) >= 0 ||
                 e.Name.LocalName is "cellIns" or "cellDel" or "cellMerge" or "moveFromRangeStart" or "moveFromRangeEnd" or "moveToRangeStart" or "moveToRangeEnd")).ToList();
            foreach (var h in history) h.Remove();
            acceptedChanges += history.Count;
            changed |= history.Count > 0;
        }

        if (o.HiddenText)
        {
            var runs = doc.Descendants(W + "r").Where(DocxInspector.IsHiddenRun).ToList();
            foreach (var r in runs) r.Remove();
            hiddenRuns += runs.Count;
            changed |= runs.Count > 0;
        }

        if (o.DocumentProperties)
        {
            // Content controls bound to the custom XML we're removing keep their text but lose the link to it.
            var bindings = doc.Descendants(W + "dataBinding").ToList();
            foreach (var b in bindings) b.Remove();
            changed |= bindings.Count > 0;
        }

        if (deadRelIds is { Count: > 0 })
        {
            // Embedded objects: drop the markup that points at a part we removed, or Word would call the file damaged.
            var broken = doc.Descendants(W + "object")
                .Where(obj => obj.DescendantsAndSelf().Attributes().Any(a => a.Name.Namespace == R && deadRelIds.Contains(a.Value))).ToList();
            foreach (var obj in broken) obj.Remove();
            objects += broken.Count;
            changed |= broken.Count > 0;
        }

        return changed ? Serialize(doc) : data;
    }

    // ------------------------------------------------------------------
    // Properties
    // ------------------------------------------------------------------
    private static byte[] ClearCoreProperties(byte[] data)
    {
        XDocument doc;
        try { doc = Parse(data); } catch (XmlException) { return data; }
        if (doc.Root == null) return data;

        foreach (var name in new[] { Dc + "creator", Cp + "lastModifiedBy", Dc + "title", Dc + "subject", Cp + "keywords", Cp + "category", Dc + "description" })
        {
            var el = doc.Root.Element(name);
            if (el != null) el.Value = "";
        }
        return Serialize(doc);
    }

    private static byte[] ClearExtendedProperties(byte[] data)
    {
        XDocument doc;
        try { doc = Parse(data); } catch (XmlException) { return data; }
        if (doc.Root == null) return data;

        foreach (var name in new[] { "Company", "Manager", "HyperlinkBase" })
        {
            var el = doc.Root.Element(Ext + name);
            if (el != null) el.Value = "";
        }
        return Serialize(doc);
    }

    // ------------------------------------------------------------------
    // Keeping the package consistent
    // ------------------------------------------------------------------
    private static byte[] DropContentTypeOverrides(byte[] data, HashSet<string> removed)
    {
        XDocument doc;
        try { doc = Parse(data); } catch (XmlException) { return data; }
        if (doc.Root == null) return data;

        var dead = doc.Root.Elements(Ct + "Override")
            .Where(e => removed.Contains(((string?)e.Attribute("PartName") ?? "").TrimStart('/'))).ToList();
        if (dead.Count == 0) return data;
        foreach (var e in dead) e.Remove();
        return Serialize(doc);
    }

    private static byte[] DropRelationships(byte[] data, string sourcePart, HashSet<string> removed)
    {
        XDocument doc;
        try { doc = Parse(data); } catch (XmlException) { return data; }
        if (doc.Root == null) return data;

        var dead = doc.Root.Elements(Rel + "Relationship")
            .Where(r => !string.Equals((string?)r.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)
                        && removed.Contains(Resolve(sourcePart, (string?)r.Attribute("Target") ?? ""))).ToList();
        if (dead.Count == 0) return data;
        foreach (var r in dead) r.Remove();
        return Serialize(doc);
    }

    private sealed record Relationship(string Id, string Target, bool External);

    private static IEnumerable<Relationship> ReadRels(byte[] data)
    {
        XDocument doc;
        try { doc = Parse(data); } catch (XmlException) { yield break; }
        foreach (var r in doc.Root?.Elements(Rel + "Relationship") ?? Enumerable.Empty<XElement>())
            yield return new Relationship(
                (string?)r.Attribute("Id") ?? "",
                (string?)r.Attribute("Target") ?? "",
                string.Equals((string?)r.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The part a relationships file describes: "word/_rels/document.xml.rels" is about "word/document.xml"; "_rels/.rels" is about the package itself ("").</summary>
    private static string SourcePartOf(string relsName)
    {
        var norm = relsName.Replace('\\', '/');
        int slash = norm.LastIndexOf('/');
        var folder = slash < 0 ? "" : norm[..slash];
        var file = slash < 0 ? norm : norm[(slash + 1)..];
        if (!folder.EndsWith("_rels", StringComparison.OrdinalIgnoreCase) && folder.Length > 0 || folder.Length == 0) return norm;

        var parent = folder.Length == 5 ? "" : folder[..^6];       // strip "/_rels"
        var source = file.EndsWith(".rels", StringComparison.OrdinalIgnoreCase) ? file[..^5] : file;
        return source.Length == 0 ? "" : (parent.Length == 0 ? source : parent + "/" + source);
    }

    /// <summary>Turns a relationship target into a package path, relative to the part that holds the relationship.</summary>
    private static string Resolve(string sourcePart, string target)
    {
        if (target.StartsWith("/")) return target.TrimStart('/');
        int slash = sourcePart.LastIndexOf('/');
        var segments = new List<string>();
        if (slash >= 0) segments.AddRange(sourcePart[..slash].Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var piece in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (piece == ".") continue;
            if (piece == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); }
            else segments.Add(piece);
        }
        return string.Join("/", segments);
    }

    private static XDocument Parse(byte[] data)
    {
        using var ms = new MemoryStream(data);
        return XDocument.Load(ms, LoadOptions.PreserveWhitespace);
    }

    private static byte[] Serialize(XDocument doc)
    {
        using var ms = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
        using (var writer = XmlWriter.Create(ms, settings))
            doc.Save(writer);
        return ms.ToArray();
    }
}
