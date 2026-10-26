using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace ReceiptCapture.Core.Meetings;

/// <summary>
/// Writes a meeting's transcript as a Word (.docx) file, with no extra software needed. The file has the meeting's title,
/// date and length, the speakers, optionally the person's notes, and then the transcript as one paragraph per speaker turn.
/// It deliberately carries no author name or other personal properties.
/// </summary>
public static class TranscriptDocx
{
    public const string Disclaimer =
        "Made on this computer with automatic speech recognition. It can contain mistakes, and speaker names were added " +
        "by hand or suggested from the recording. Please check it before relying on it.";

    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static void Write(string path, Meeting meeting, Transcript transcript, bool includeNotes, CultureInfo? culture = null)
    {
        var bytes = Build(meeting, transcript, includeNotes, culture);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    public static byte[] Build(Meeting meeting, Transcript transcript, bool includeNotes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;

        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(zip, "[Content_Types].xml", ContentTypes);
            AddEntry(zip, "_rels/.rels", RootRelationships);
            AddEntry(zip, "word/_rels/document.xml.rels", DocumentRelationships);
            AddEntry(zip, "word/styles.xml", Styles);
            AddEntry(zip, "word/document.xml", BuildDocument(meeting, transcript, includeNotes, culture));
        }

        return memory.ToArray();
    }

    private static string BuildDocument(Meeting meeting, Transcript transcript, bool includeNotes, CultureInfo culture)
    {
        var body = new StringBuilder();

        body.Append(Paragraph("Title", Run(string.IsNullOrWhiteSpace(meeting.Title) ? "Meeting transcript" : meeting.Title)));
        body.Append(Paragraph(null, Run("Recorded: ", bold: true) + Run(meeting.StartedAt.ToString("f", culture))));
        body.Append(Paragraph(null, Run("Length: ", bold: true) + Run(meeting.DurationText)));

        var speakers = SpeakerLabels.Names(transcript);
        if (speakers.Count > 0)
            body.Append(Paragraph(null, Run("Speakers: ", bold: true) + Run(string.Join(", ", speakers))));

        if (includeNotes && !string.IsNullOrWhiteSpace(meeting.Notes))
        {
            body.Append(Paragraph("Heading1", Run("Notes")));
            foreach (var line in meeting.Notes.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                body.Append(Paragraph(null, Run(line)));
        }

        body.Append(Paragraph("Heading1", Run("Transcript")));
        foreach (var turn in TranscriptFormatter.ToTurns(transcript.Segments))
        {
            var runs = Run(TranscriptFormatter.TimeStamp(turn.StartSeconds) + " ", color: "6B7280");
            if (turn.Speaker != null) runs += Run(turn.Speaker + ": ", bold: true);
            runs += Run(turn.Text);
            body.Append(Paragraph(null, runs));
        }

        body.Append(Paragraph(null, Run(Disclaimer, italic: true, color: "6B7280", sizeHalfPoints: 18), spaceBefore: 360));

        // US Letter with one-inch margins.
        const string section = "<w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/>" +
                               "<w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/></w:sectPr>";

        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
               $"<w:document xmlns:w=\"{W}\"><w:body>{body}{section}</w:body></w:document>";
    }

    private static string Paragraph(string? style, string runs, int spaceBefore = 0)
    {
        var props = new StringBuilder();
        if (style != null) props.Append($"<w:pStyle w:val=\"{style}\"/>");
        if (spaceBefore > 0) props.Append($"<w:spacing w:before=\"{spaceBefore}\"/>");
        return "<w:p>" + (props.Length > 0 ? $"<w:pPr>{props}</w:pPr>" : "") + runs + "</w:p>";
    }

    private static string Run(string text, bool bold = false, bool italic = false, string? color = null, int sizeHalfPoints = 0)
    {
        var props = new StringBuilder();
        if (bold) props.Append("<w:b/>");
        if (italic) props.Append("<w:i/>");
        if (color != null) props.Append($"<w:color w:val=\"{color}\"/>");
        if (sizeHalfPoints > 0) props.Append($"<w:sz w:val=\"{sizeHalfPoints}\"/>");

        return "<w:r>" + (props.Length > 0 ? $"<w:rPr>{props}</w:rPr>" : "") +
               $"<w:t xml:space=\"preserve\">{Escape(text)}</w:t></w:r>";
    }

    /// <summary>Escapes text for XML and drops the characters XML 1.0 can't hold at all (stray control characters).</summary>
    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!XmlConvert.IsXmlChar(c) && !char.IsSurrogate(c)) continue;

            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private const string ContentTypes =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
        "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
        "</Types>";

    private const string RootRelationships =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
        "</Relationships>";

    private const string DocumentRelationships =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
        "</Relationships>";

    private const string Styles =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        $"<w:styles xmlns:w=\"{W}\">" +
        "<w:docDefaults>" +
        "<w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Calibri\"/><w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/></w:rPr></w:rPrDefault>" +
        "<w:pPrDefault><w:pPr><w:spacing w:after=\"120\" w:line=\"264\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault>" +
        "</w:docDefaults>" +
        "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Title\"><w:name w:val=\"Title\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/>" +
        "<w:pPr><w:spacing w:after=\"160\"/></w:pPr><w:rPr><w:b/><w:sz w:val=\"40\"/><w:szCs w:val=\"40\"/></w:rPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/>" +
        "<w:pPr><w:keepNext/><w:spacing w:before=\"320\" w:after=\"120\"/><w:outlineLvl w:val=\"0\"/></w:pPr><w:rPr><w:b/><w:sz w:val=\"28\"/><w:szCs w:val=\"28\"/></w:rPr></w:style>" +
        "</w:styles>";
}
