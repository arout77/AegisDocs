using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ReceiptCapture.Core.Compliance;
using ReceiptCapture.Core.Pdf;

namespace ReceiptCapture.Core.HiddenContent;

/// <summary>
/// Looks inside a PDF for things that travel with it without being visible on its pages: author and other
/// properties, comments and markup, attached files, scripts, hidden layers, typed-in form values, page preview
/// pictures, and earlier saved versions. It reads the file's own structure directly (no PDF library), including the
/// compressed "object streams" newer PDFs keep their details in. Password-protected files can't be read this way.
/// </summary>
public static class PdfInspector
{
    public const long MaxBytes = 150L * 1024 * 1024;
    private const int MaxInflated = 32 * 1024 * 1024;

    private static readonly RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    public static HiddenInspection Inspect(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxBytes)
            return HiddenInspection.NotChecked("the file is too large (over 150 MB) for the hidden-content check");
        return Inspect(File.ReadAllBytes(path));
    }

    public static HiddenInspection Inspect(byte[] data)
    {
        if (data.Length < 8 || !Contains(data, "%PDF-"u8, 0, Math.Min(data.Length, 1024)))
            return HiddenInspection.NotChecked("this does not look like a PDF, so its hidden content was not checked");
        if (PdfEncryptionProbe.IsEncrypted(new MemoryStream(data)))
            return HiddenInspection.NotChecked("the PDF is password-protected, so its hidden content can't be read (remove the password and scan again)");

        var file = Skeleton.Read(data);
        if (file.Objects.Count == 0)
            return HiddenInspection.NotChecked("the PDF's structure could not be read, so its hidden content was not checked");

        var items = new List<HiddenItem>();
        var texts = new List<HiddenText>();

        CheckVersions(file, items);
        CheckInfo(file, items, texts);
        CheckMetadataStreams(file, items, texts);
        CheckAttachments(file, items);
        CheckAnnotations(file, items, texts);
        CheckScripts(file, items);
        CheckLayers(file, items);
        CheckForms(file, items, texts);
        CheckThumbnails(file, items);

        items.Sort((a, b) => a.Severity.CompareTo(b.Severity));
        return new HiddenInspection(items, texts);
    }

    // ------------------------------------------------------------------
    // The checks
    // ------------------------------------------------------------------
    private static void CheckVersions(Skeleton file, List<HiddenItem> items)
    {
        int saves = Regex.Matches(file.Text, "startxref").Count;
        bool linearized = file.Text.AsSpan(0, Math.Min(file.Text.Length, 2048)).Contains("/Linearized", StringComparison.Ordinal);
        int expected = linearized ? 2 : 1;
        if (saves > expected)
            items.Add(new HiddenItem(HiddenCategory.SavedVersions, FindingSeverity.Medium,
                $"The file was saved {saves - expected + 1} times by adding to it: earlier versions of pages or text may still be inside", "File structure"));
    }

    private static readonly Regex InfoRefRx = new(@"/Info\s+(\d+)\s+\d+\s+R", Opts);
    private static readonly Regex InfoKeyRx = new(@"/(Author|Creator|Producer|Title|Subject|Keywords)\s*(?=[(<])", Opts);

    private static void CheckInfo(Skeleton file, List<HiddenItem> items, List<HiddenText> texts)
    {
        var refs = InfoRefRx.Matches(file.Text);
        if (refs.Count == 0) return;
        if (!int.TryParse(refs[^1].Groups[1].Value, out int number) || !file.Objects.TryGetValue(number, out var dict)) return;
        const string where = "Document properties";

        foreach (Match m in InfoKeyRx.Matches(dict))
        {
            var value = ReadString(dict, m.Index + m.Length)?.Trim();
            if (string.IsNullOrEmpty(value)) continue;

            switch (m.Groups[1].Value)
            {
                case "Author":
                    items.Add(new HiddenItem(HiddenCategory.DocumentProperties, FindingSeverity.Medium, $"Author: {MatchMasker.MaskName(value)}", where));
                    break;
                case "Creator":
                case "Producer":
                    items.Add(new HiddenItem(HiddenCategory.DocumentProperties, FindingSeverity.Low,
                        $"{(m.Groups[1].Value == "Creator" ? "Created with" : "Made by")}: {Trim(value, 60)}", where));
                    break;
                default:
                    items.Add(new HiddenItem(HiddenCategory.DocumentProperties, FindingSeverity.Low,
                        $"{(m.Groups[1].Value == "Keywords" ? "Keywords" : m.Groups[1].Value)}: {MatchMasker.MaskName(value)}", where));
                    texts.Add(new HiddenText(where, value));
                    break;
            }
        }
    }

    private static readonly Regex XmpCreatorRx = new(@"<dc:creator>\s*<rdf:Seq>\s*<rdf:li[^>]*>(?<n>[^<]+)</rdf:li>", Opts | RegexOptions.Singleline);

    private static void CheckMetadataStreams(Skeleton file, List<HiddenItem> items, List<HiddenText> texts)
    {
        bool any = false;
        foreach (var (_, dict, stream) in file.Streams().Where(s => Regex.IsMatch(s.Dict, @"/Type\s*/Metadata\b")))
        {
            var bytes = file.ReadStream(stream, dict, 64 * 1024);
            if (bytes == null) continue;
            var xml = Encoding.UTF8.GetString(bytes);
            any = true;
            texts.Add(new HiddenText("Document properties (XMP)", MarkupText.Strip(xml)));
            var creator = XmpCreatorRx.Match(xml);
            if (creator.Success && creator.Groups["n"].Value.Trim().Length > 0)
                items.Add(new HiddenItem(HiddenCategory.DocumentProperties, FindingSeverity.Medium,
                    $"Author in the XMP metadata: {MatchMasker.MaskName(creator.Groups["n"].Value)}", "Document properties"));
        }
        if (any)
            items.Add(new HiddenItem(HiddenCategory.DocumentProperties, FindingSeverity.Low, "An XMP metadata block (tags, history, software) is stored", "Document properties"));
    }

    private static readonly Regex NameKeyRx = new(@"/(UF|F)\s*(?=[(<])", Opts);

    private static void CheckAttachments(Skeleton file, List<HiddenItem> items)
    {
        int streams = file.Objects.Values.Count(d => Regex.IsMatch(d, @"/Type\s*/EmbeddedFile\b"));
        var names = new List<string>();
        int specs = 0;
        foreach (var dict in file.Objects.Values)
        {
            if (!Regex.IsMatch(dict, @"/EF\s*<<")) continue;
            specs++;
            var name = ReadNameKey(dict);
            if (name != null) names.Add(name);
        }
        int fileAnnots = file.Objects.Values.Count(d => Regex.IsMatch(d, @"/Subtype\s*/FileAttachment\b"));

        int count = Math.Max(Math.Max(streams, specs), fileAnnots);
        if (count == 0) return;

        var shown = names.Count == 0 ? "" : ": " + string.Join(", ", names.Take(4).Select(MatchMasker.MaskFileName)) + (names.Count > 4 ? $" and {names.Count - 4} more" : "");
        items.Add(new HiddenItem(HiddenCategory.Attachments, FindingSeverity.High,
            $"{count} file{(count == 1 ? "" : "s")} attached inside the PDF{shown}", "Attachments"));
    }

    private static string? ReadNameKey(string dict)
    {
        foreach (Match m in NameKeyRx.Matches(dict))
        {
            var value = ReadString(dict, m.Index + m.Length);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }

    private static readonly HashSet<string> MarkupTypes = new(StringComparer.Ordinal)
    {
        "Text", "FreeText", "Caret", "Stamp", "Ink", "Line", "Square", "Circle", "Polygon", "PolyLine",
        "Highlight", "Underline", "StrikeOut", "Squiggly", "Sound", "Movie",
    };
    private static readonly Regex SubtypeRx = new(@"/Subtype\s*/(\w+)", Opts);
    private static readonly Regex ContentsRx = new(@"/Contents\s*(?=[(<])", Opts);
    private static readonly Regex AuthorKeyRx = new(@"/T\s*(?=[(<])", Opts);

    private static void CheckAnnotations(Skeleton file, List<HiddenItem> items, List<HiddenText> texts)
    {
        int count = 0;
        var authors = new HashSet<string>();
        var bodies = new List<string>();

        foreach (var dict in file.Objects.Values)
        {
            var sub = SubtypeRx.Match(dict);
            if (!sub.Success || !MarkupTypes.Contains(sub.Groups[1].Value)) continue;
            if (!dict.Contains("/Rect", StringComparison.Ordinal) && !Regex.IsMatch(dict, @"/Type\s*/Annot\b")) continue;

            count++;
            var contents = ContentsRx.Match(dict);
            if (contents.Success)
            {
                var text = ReadString(dict, contents.Index + contents.Length)?.Trim();
                if (!string.IsNullOrEmpty(text)) bodies.Add(text);
            }
            var author = AuthorKeyRx.Match(dict);
            if (author.Success)
            {
                var name = ReadString(dict, author.Index + author.Length)?.Trim();
                if (!string.IsNullOrEmpty(name)) authors.Add(name);
            }
        }
        if (count == 0) return;

        var byWho = authors.Count == 0 ? "" : " by " + string.Join(", ", authors.Take(4).Select(MatchMasker.MaskName)) + (authors.Count > 4 ? $" and {authors.Count - 4} more" : "");
        items.Add(new HiddenItem(HiddenCategory.Comments, FindingSeverity.Medium,
            $"{count} comment or markup annotation{(count == 1 ? "" : "s")} (notes, highlights, drawings){byWho}", "Comments and markup"));
        if (bodies.Count > 0) texts.Add(new HiddenText("Comments and markup", string.Join("\n", bodies)));
    }

    private static void CheckScripts(Skeleton file, List<HiddenItem> items)
    {
        int count = file.Objects.Values.Count(d => Regex.IsMatch(d, @"/S\s*/JavaScript\b") || Regex.IsMatch(d, @"/JS\s*[(<\d]"));
        if (count > 0)
            items.Add(new HiddenItem(HiddenCategory.Scripts, FindingSeverity.Medium,
                $"{count} JavaScript action{(count == 1 ? "" : "s")} stored in the PDF", "Scripts"));
    }

    private static void CheckLayers(Skeleton file, List<HiddenItem> items)
    {
        bool hasLayers = file.Objects.Values.Any(d => d.Contains("/OCProperties", StringComparison.Ordinal));
        if (!hasLayers) return;
        bool hiddenLayers = file.Objects.Values.Any(d => Regex.IsMatch(d, @"/OFF\s*\[\s*\d"));
        items.Add(hiddenLayers
            ? new HiddenItem(HiddenCategory.HiddenText, FindingSeverity.Medium,
                "Some content is on layers that are switched off: it isn't shown but is still in the file", "Layers")
            : new HiddenItem(HiddenCategory.Other, FindingSeverity.Low, "The PDF uses layers (optional content)", "Layers"));
    }

    private static readonly Regex FieldTypeRx = new(@"/FT\s*/(Tx|Ch)\b", Opts);
    private static readonly Regex ValueRx = new(@"/V\s*(?=[(<])", Opts);

    private static void CheckForms(Skeleton file, List<HiddenItem> items, List<HiddenText> texts)
    {
        int filled = 0;
        var values = new List<string>();
        foreach (var dict in file.Objects.Values)
        {
            if (!FieldTypeRx.IsMatch(dict)) continue;
            var v = ValueRx.Match(dict);
            if (!v.Success) continue;
            var text = ReadString(dict, v.Index + v.Length)?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            filled++;
            values.Add(text);
        }
        if (filled == 0) return;
        items.Add(new HiddenItem(HiddenCategory.Other, FindingSeverity.Medium,
            $"{filled} form field{(filled == 1 ? " has" : "s have")} typed-in values", "Form fields"));
        texts.Add(new HiddenText("Form fields", string.Join("\n", values)));
    }

    private static void CheckThumbnails(Skeleton file, List<HiddenItem> items)
    {
        if (file.Objects.Values.Any(d => Regex.IsMatch(d, @"/Thumb\s+\d+\s+\d+\s+R")))
            items.Add(new HiddenItem(HiddenCategory.PreviewImage, FindingSeverity.Medium,
                "Small preview pictures of the pages are stored (they can show pages as they were before editing)", "Page previews"));
    }

    // ------------------------------------------------------------------
    // Reading the file's structure
    // ------------------------------------------------------------------
    /// <summary>
    /// The file with every stream's binary body cut out (replaced by a marker), so what's left is plain text we can
    /// search; plus every object's dictionary, including the ones packed inside compressed object streams.
    /// </summary>
    private sealed class Skeleton
    {
        private readonly byte[] _data;
        private readonly List<(int Start, int Length)> _streams = new();
        private readonly Dictionary<int, int> _streamOf = new();       // object number -> index into _streams

        public string Text { get; private set; } = "";
        public Dictionary<int, string> Objects { get; } = new();

        private Skeleton(byte[] data) => _data = data;

        public static Skeleton Read(byte[] data)
        {
            var s = new Skeleton(data);
            s.CutStreams();
            s.IndexObjects();
            s.ExpandObjectStreams();
            return s;
        }

        private void CutStreams()
        {
            var sb = new StringBuilder(Math.Min(_data.Length, 4 * 1024 * 1024));
            int pos = 0;
            var stream = "stream"u8;
            while (pos < _data.Length)
            {
                int k = IndexOf(_data, stream, pos);
                if (k < 0) { sb.Append(Encoding.Latin1.GetString(_data, pos, _data.Length - pos)); break; }

                // "endstream" also contains "stream"; and the keyword must follow the end of a dictionary.
                bool isEnd = k >= 3 && _data[k - 3] == 'e' && _data[k - 2] == 'n' && _data[k - 1] == 'd';
                int b = k - 1;
                while (b > pos && IsWhite(_data[b])) b--;
                bool afterDict = b >= 1 && _data[b] == '>' && _data[b - 1] == '>';
                if (isEnd || !afterDict)
                {
                    sb.Append(Encoding.Latin1.GetString(_data, pos, k + 6 - pos));
                    pos = k + 6;
                    continue;
                }

                int start = k + 6;
                if (start < _data.Length && _data[start] == '\r') start++;
                if (start < _data.Length && _data[start] == '\n') start++;
                int end = IndexOf(_data, "endstream"u8, start);
                if (end < 0) { sb.Append(Encoding.Latin1.GetString(_data, pos, k + 6 - pos)); break; }

                sb.Append(Encoding.Latin1.GetString(_data, pos, k + 6 - pos));
                sb.Append("\n@@S").Append(_streams.Count).Append("@@\nendstream");
                _streams.Add((start, end - start));
                pos = end + 9;
            }
            Text = sb.ToString();
        }

        private static readonly Regex ObjRx = new(@"(?<![0-9])(\d{1,7})\s+\d{1,5}\s+obj\b", Opts);
        private static readonly Regex StreamMarkerRx = new(@"@@S(\d+)@@", Opts);

        private void IndexObjects()
        {
            foreach (Match m in ObjRx.Matches(Text))
            {
                int number = int.Parse(m.Groups[1].Value);
                int bodyStart = m.Index + m.Length;
                int end = Text.IndexOf("endobj", bodyStart, StringComparison.Ordinal);
                if (end < 0) end = Text.Length;
                var body = Text.Substring(bodyStart, end - bodyStart);

                int streamAt = body.IndexOf("stream", StringComparison.Ordinal);
                var marker = StreamMarkerRx.Match(body);
                if (marker.Success) _streamOf[number] = int.Parse(marker.Groups[1].Value);

                var dict = streamAt >= 0 ? body[..streamAt] : body;
                Objects[number] = dict.Length > 20_000 ? dict[..20_000] : dict;       // later definitions (updates) win
            }
        }

        private void ExpandObjectStreams()
        {
            long budget = 4L * MaxInflated;
            foreach (var number in Objects.Where(o => Regex.IsMatch(o.Value, @"/Type\s*/ObjStm\b")).Select(o => o.Key).ToList())
            {
                if (!_streamOf.TryGetValue(number, out var index) || budget <= 0) continue;
                var dict = Objects[number];
                var bytes = ReadStream(_streams[index], dict, MaxInflated);
                if (bytes == null) continue;
                budget -= bytes.Length;

                var n = Regex.Match(dict, @"/N\s+(\d+)");
                var first = Regex.Match(dict, @"/First\s+(\d+)");
                if (!n.Success || !first.Success) continue;
                int count = int.Parse(n.Groups[1].Value), firstOffset = int.Parse(first.Groups[1].Value);
                if (firstOffset > bytes.Length) continue;

                var header = Encoding.Latin1.GetString(bytes, 0, firstOffset).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var body = Encoding.Latin1.GetString(bytes, firstOffset, bytes.Length - firstOffset);
                for (int i = 0; i < count && 2 * i + 1 < header.Length; i++)
                {
                    if (!int.TryParse(header[2 * i], out int objNumber) || !int.TryParse(header[2 * i + 1], out int offset)) continue;
                    int next = 2 * i + 3 < header.Length && int.TryParse(header[2 * i + 3], out int nx) ? nx : body.Length;
                    if (offset < 0 || offset > body.Length || next < offset) continue;
                    var text = body.Substring(offset, Math.Min(next, body.Length) - offset);
                    Objects[objNumber] = text.Length > 20_000 ? text[..20_000] : text;
                }
            }
        }

        /// <summary>Every object that has a stream: (number, its dictionary, its stream).</summary>
        public IEnumerable<(int Number, string Dict, (int Start, int Length) Stream)> Streams()
        {
            foreach (var (number, index) in _streamOf)
                if (Objects.TryGetValue(number, out var dict))
                    yield return (number, dict, _streams[index]);
        }

        /// <summary>The stream's bytes, decompressed if it uses the common Flate filter. Null if it can't be read.</summary>
        public byte[]? ReadStream((int Start, int Length) stream, string dict, int limit)
        {
            if (stream.Length <= 0) return Array.Empty<byte>();
            try
            {
                var raw = new ReadOnlyMemory<byte>(_data, stream.Start, stream.Length);
                bool flate = dict.Contains("/FlateDecode", StringComparison.Ordinal) || dict.Contains("/Fl ", StringComparison.Ordinal);
                bool otherFilter = !flate && dict.Contains("/Filter", StringComparison.Ordinal);
                if (otherFilter) return null;
                if (!flate) return raw.Span[..Math.Min(raw.Length, limit)].ToArray();

                using var input = new MemoryStream(_data, stream.Start, stream.Length, writable: false);
                using var z = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buffer = new byte[16 * 1024];
                int read;
                while (output.Length < limit && (read = z.Read(buffer, 0, buffer.Length)) > 0)
                    output.Write(buffer, 0, read);
                return output.ToArray();
            }
            catch (InvalidDataException) { return null; }
            catch (IOException) { return null; }
        }
    }

    // ------------------------------------------------------------------
    // Small helpers
    // ------------------------------------------------------------------
    /// <summary>Reads a PDF text string — (literal) or &lt;hex&gt; — starting at the given position.</summary>
    internal static string? ReadString(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        if (i >= s.Length) return null;
        byte[] bytes;

        if (s[i] == '(')
        {
            var buf = new List<byte>();
            int depth = 1;
            i++;
            while (i < s.Length && depth > 0 && buf.Count < 8000)
            {
                char c = s[i++];
                if (c == '\\' && i < s.Length)
                {
                    char n = s[i++];
                    switch (n)
                    {
                        case 'n': buf.Add(10); break;
                        case 'r': buf.Add(13); break;
                        case 't': buf.Add(9); break;
                        case 'b': buf.Add(8); break;
                        case 'f': buf.Add(12); break;
                        case '\r': if (i < s.Length && s[i] == '\n') i++; break;
                        case '\n': break;
                        default:
                            if (n is >= '0' and <= '7')
                            {
                                int v = n - '0';
                                for (int k = 0; k < 2 && i < s.Length && s[i] is >= '0' and <= '7'; k++) v = v * 8 + (s[i++] - '0');
                                buf.Add((byte)(v & 0xFF));
                            }
                            else buf.Add((byte)n);
                            break;
                    }
                }
                else if (c == '(') { depth++; buf.Add((byte)c); }
                else if (c == ')') { depth--; if (depth > 0) buf.Add((byte)c); }
                else buf.Add((byte)c);
            }
            bytes = buf.ToArray();
        }
        else if (s[i] == '<' && (i + 1 >= s.Length || s[i + 1] != '<'))
        {
            int end = s.IndexOf('>', i);
            if (end < 0) return null;
            var hex = new string(s.Substring(i + 1, end - i - 1).Where(Uri.IsHexDigit).ToArray());
            if (hex.Length % 2 == 1) hex += "0";
            if (hex.Length > 16000) return null;
            bytes = Convert.FromHexString(hex);
        }
        else return null;

        string text;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        else text = Encoding.Latin1.GetString(bytes);
        return new string(text.Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').ToArray());
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    private static bool IsWhite(byte b) => b is 0x20 or 0x0A or 0x0D or 0x09 or 0x0C or 0x00;

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> token, int from)
    {
        int i = data.AsSpan(from).IndexOf(token);
        return i < 0 ? -1 : from + i;
    }

    private static bool Contains(byte[] data, ReadOnlySpan<byte> token, int from, int length)
        => data.AsSpan(from, length).IndexOf(token) >= 0;
}
