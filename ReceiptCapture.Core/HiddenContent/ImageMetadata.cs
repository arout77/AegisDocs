using System.Text;
using ReceiptCapture.Core.Compliance;

namespace ReceiptCapture.Core.HiddenContent;

/// <summary>
/// Finds and removes the information a JPEG or PNG carries besides the picture: where and when it was taken,
/// on what device, who owns it, captions, and an older preview of the picture. Removal works on the file's
/// structure and never re-encodes the pixels, so the picture is exactly as it was.
/// </summary>
public static class ImageMetadata
{
    private const string Where = "Picture metadata";

    public static bool IsSupported(string extension)
        => extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".png", StringComparison.OrdinalIgnoreCase);

    public static HiddenInspection Inspect(byte[] data, string extension)
        => extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? InspectPng(data) : InspectJpeg(data);

    public static (byte[] Data, IReadOnlyList<string> Removed) Strip(byte[] data, string extension)
        => extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? StripPng(data) : StripJpeg(data);

    // ------------------------------------------------------------------
    // JPEG
    // ------------------------------------------------------------------
    private static readonly byte[] ExifHeader = "Exif\0\0"u8.ToArray();
    private static readonly byte[] XmpHeader = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();
    private static readonly byte[] PhotoshopHeader = "Photoshop 3.0\0"u8.ToArray();

    /// <summary>Walks the segments that come before the picture data and calls back with (marker, payload).</summary>
    private static bool WalkJpeg(byte[] d, Action<byte, int, int> segment)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return false;
        int i = 2;
        while (i + 4 <= d.Length)
        {
            if (d[i] != 0xFF) return true;                       // lost sync: stop reading, don't guess
            byte marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; }               // fill byte
            if (marker == 0xDA || marker == 0xD9) return true;   // picture data (or end) follows
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }   // markers without a length

            int length = d[i + 2] << 8 | d[i + 3];
            if (length < 2 || i + 2 + length > d.Length) return true;
            segment(marker, i, length);
            i += 2 + length;
        }
        return true;
    }

    private static HiddenInspection InspectJpeg(byte[] d)
    {
        var items = new List<HiddenItem>();
        var texts = new List<HiddenText>();
        ExifInfo? exif = null;
        bool xmp = false, iptc = false;

        bool valid = WalkJpeg(d, (marker, at, length) =>
        {
            var payload = d.AsSpan(at + 4, length - 2);
            if (marker == 0xE1 && payload.StartsWith(ExifHeader)) exif ??= ExifReader.Parse(payload[ExifHeader.Length..]);
            else if (marker == 0xE1 && payload.StartsWith(XmpHeader))
            {
                xmp = true;
                var text = Encoding.UTF8.GetString(payload[XmpHeader.Length..]);
                texts.Add(new HiddenText(Where, MarkupText.Strip(text.Length > 32_000 ? text[..32_000] : text)));
            }
            else if (marker == 0xED && payload.StartsWith(PhotoshopHeader)) iptc = true;
            else if (marker == 0xFE)
            {
                var comment = Encoding.UTF8.GetString(payload).Trim('\0', ' ', '\r', '\n');
                if (comment.Length > 0)
                {
                    items.Add(new HiddenItem(HiddenCategory.Other, FindingSeverity.Medium, "A text comment is stored in the picture", Where));
                    texts.Add(new HiddenText(Where, comment));
                }
            }
        });

        if (!valid) return HiddenInspection.NotChecked("this does not look like a JPEG, so its hidden content was not checked");

        AddExifItems(exif, items, texts);
        if (xmp) items.Add(new HiddenItem(HiddenCategory.Other, FindingSeverity.Low, "XMP metadata (editing history, tags, captions) is stored in the picture", Where));
        if (iptc) items.Add(new HiddenItem(HiddenCategory.Other, FindingSeverity.Medium, "IPTC caption and keyword data is stored in the picture", Where));
        return new HiddenInspection(items, texts);
    }

    private static (byte[], IReadOnlyList<string>) StripJpeg(byte[] d)
    {
        var drop = new List<(int At, int Length)>();
        var removed = new List<string>();
        bool exif = false, xmp = false, iptc = false, comment = false, other = false;

        WalkJpeg(d, (marker, at, length) =>
        {
            bool isExif = marker == 0xE1 && d.AsSpan(at + 4, Math.Min(length - 2, ExifHeader.Length)).SequenceEqual(ExifHeader);
            bool isXmp = marker == 0xE1 && d.AsSpan(at + 4, Math.Min(length - 2, XmpHeader.Length)).SequenceEqual(XmpHeader);
            // Keep JFIF (E0), the colour profile (E2) and Adobe's colour flag (EE): the picture needs them to look right.
            bool keep = marker is 0xE0 or 0xE2 or 0xEE || marker < 0xE0 || (marker > 0xEF && marker != 0xFE);
            if (keep) return;

            drop.Add((at, 2 + length));
            if (isExif) exif = true;
            else if (isXmp) xmp = true;
            else if (marker == 0xED) iptc = true;
            else if (marker == 0xFE) comment = true;
            else other = true;
        });

        if (exif) removed.Add("Removed camera, date and location data (EXIF)");
        if (xmp) removed.Add("Removed XMP metadata");
        if (iptc) removed.Add("Removed IPTC captions and keywords");
        if (comment) removed.Add("Removed the text comment");
        if (other) removed.Add("Removed other embedded metadata");
        if (drop.Count == 0) return (d, removed);

        using var ms = new MemoryStream(d.Length);
        int pos = 0;
        foreach (var (at, length) in drop.OrderBy(x => x.At))
        {
            ms.Write(d, pos, at - pos);
            pos = at + length;
        }
        ms.Write(d, pos, d.Length - pos);
        return (ms.ToArray(), removed);
    }

    // ------------------------------------------------------------------
    // PNG
    // ------------------------------------------------------------------
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private static bool WalkPng(byte[] d, Action<string, int, int> chunk)
    {
        if (d.Length < 8 || !d.AsSpan(0, 8).SequenceEqual(PngSignature)) return false;
        int i = 8;
        while (i + 12 <= d.Length)
        {
            uint length = (uint)(d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3]);
            if (length > int.MaxValue - 12 || i + 12 + (long)length > d.Length) return true;
            chunk(Encoding.ASCII.GetString(d, i + 4, 4), i, (int)length);
            i += 12 + (int)length;
        }
        return true;
    }

    private static readonly string[] PngMetadataChunks = { "tEXt", "zTXt", "iTXt", "eXIf", "tIME" };

    private static HiddenInspection InspectPng(byte[] d)
    {
        var items = new List<HiddenItem>();
        var texts = new List<HiddenText>();
        ExifInfo? exif = null;
        bool time = false;
        var keywords = new List<string>();

        bool valid = WalkPng(d, (type, at, length) =>
        {
            if (type == "eXIf") exif ??= ExifReader.Parse(d.AsSpan(at + 8, length));
            else if (type == "tIME") time = true;
            else if (type == "tEXt")
            {
                var data = d.AsSpan(at + 8, length);
                int zero = data.IndexOf((byte)0);
                if (zero > 0)
                {
                    keywords.Add(Encoding.Latin1.GetString(data[..zero]));
                    var text = Encoding.Latin1.GetString(data[(zero + 1)..]).Trim();
                    if (text.Length > 0) texts.Add(new HiddenText(Where, text));
                }
            }
            else if (type is "zTXt" or "iTXt")
            {
                var data = d.AsSpan(at + 8, length);
                int zero = data.IndexOf((byte)0);
                if (zero > 0) keywords.Add(Encoding.Latin1.GetString(data[..zero]));
                if (type == "iTXt" && length < 64_000)
                    texts.Add(new HiddenText(Where, MarkupText.Strip(Encoding.UTF8.GetString(data))));     // text is often stored uncompressed
            }
        });

        if (!valid) return HiddenInspection.NotChecked("this does not look like a PNG, so its hidden content was not checked");

        AddExifItems(exif, items, texts);
        if (keywords.Count > 0)
            items.Add(new HiddenItem(HiddenCategory.Other, FindingSeverity.Medium,
                $"Text notes are stored in the picture ({string.Join(", ", keywords.Distinct().Take(5))})", Where));
        if (time) items.Add(new HiddenItem(HiddenCategory.Other, FindingSeverity.Low, "The time the picture was last changed is stored", Where));
        return new HiddenInspection(items, texts);
    }

    private static (byte[], IReadOnlyList<string>) StripPng(byte[] d)
    {
        var drop = new List<(int At, int Length)>();
        var types = new HashSet<string>();
        WalkPng(d, (type, at, length) =>
        {
            if (Array.IndexOf(PngMetadataChunks, type) < 0) return;
            drop.Add((at, 12 + length));
            types.Add(type);
        });

        var removed = new List<string>();
        if (types.Contains("eXIf")) removed.Add("Removed camera, date and location data (EXIF)");
        if (types.Overlaps(new[] { "tEXt", "zTXt", "iTXt" })) removed.Add("Removed text notes and XMP metadata");
        if (types.Contains("tIME")) removed.Add("Removed the last-changed time");
        if (drop.Count == 0) return (d, removed);

        using var ms = new MemoryStream(d.Length);
        int pos = 0;
        foreach (var (at, length) in drop.OrderBy(x => x.At))
        {
            ms.Write(d, pos, at - pos);
            pos = at + length;
        }
        ms.Write(d, pos, d.Length - pos);
        return (ms.ToArray(), removed);
    }

    // ------------------------------------------------------------------
    // Shared
    // ------------------------------------------------------------------
    private static void AddExifItems(ExifInfo? exif, List<HiddenItem> items, List<HiddenText> texts)
    {
        if (exif == null) return;

        if (exif.HasGps)
            items.Add(new HiddenItem(HiddenCategory.Location, FindingSeverity.High,
                "GPS coordinates (where the picture was taken) are stored in the picture", Where));
        if (exif.Make != null || exif.Model != null)
            items.Add(new HiddenItem(HiddenCategory.Other, FindingSeverity.Low, "The make and model of the camera or phone are stored", Where));
        if (exif.HasSerialNumber)
            items.Add(new HiddenItem(HiddenCategory.Other, FindingSeverity.Medium, "A camera or lens serial number is stored", Where));
        if (exif.DateTaken != null)
            items.Add(new HiddenItem(HiddenCategory.Other, FindingSeverity.Low, "The date and time the picture was taken are stored", Where));
        if (exif.Software != null)
            items.Add(new HiddenItem(HiddenCategory.Other, FindingSeverity.Low, "The name of the editing software is stored", Where));

        foreach (var (label, value) in new[]
                 { ("Owner", exif.OwnerName), ("Author", exif.Artist), ("Copyright notice", exif.Copyright), ("Description", exif.Description), ("User comment", exif.UserComment) })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            items.Add(new HiddenItem(HiddenCategory.Other, FindingSeverity.Medium, $"{label} text is stored: {MatchMasker.MaskName(value)}", Where));
            texts.Add(new HiddenText(Where, value));
        }

        if (exif.HasThumbnail)
            items.Add(new HiddenItem(HiddenCategory.PreviewImage, FindingSeverity.Medium,
                "A small preview copy of the picture is stored inside its metadata (it can show the picture as it was before editing)", Where));
    }
}
