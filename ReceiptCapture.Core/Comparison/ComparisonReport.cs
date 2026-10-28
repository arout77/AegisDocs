using System.Globalization;
using System.Net;
using System.Text;

namespace ReceiptCapture.Core.Comparison;

/// <summary>Facts about the two files that go at the top of the report.</summary>
public sealed record ComparisonInfo(
    string OriginalName, string OriginalSha256, long OriginalBytes,
    string RevisedName, string RevisedSha256, long RevisedBytes,
    DateTimeOffset GeneratedAt, string AppVersion,
    IReadOnlyList<string> Notes);

/// <summary>The headline numbers of a comparison.</summary>
public sealed record ComparisonSummary(
    int WordsUnchanged, int WordsRemoved, int WordsAdded, int ParagraphsChanged, double SimilarityPercent, bool TooDifferent)
{
    public bool Identical => WordsRemoved == 0 && WordsAdded == 0;
}

/// <summary>Writes a comparison as a web page: removed words struck through in red, added words underlined in green.</summary>
public static class ComparisonReport
{
    public const string Disclaimer =
        "This compares the words of the two documents. It does not compare formatting, images, tables' layout, headers or footers, " +
        "and it is a reading aid, not a certified legal redline. Check anything important against the documents themselves.";

    // ------------------------------------------------------------------
    // Numbers
    // ------------------------------------------------------------------
    public static ComparisonSummary Summarize(IReadOnlyList<TextToken> original, IReadOnlyList<TextToken> revised, DiffResult diff)
    {
        var blocks = BuildBlocks(original, revised, diff);
        int total = original.Count + revised.Count;
        double similarity = total == 0 ? 100 : 2.0 * diff.Unchanged / total * 100;
        return new ComparisonSummary(diff.Unchanged, diff.Removed, diff.Added, blocks.Count(b => b.Changed), similarity, diff.TooDifferent);
    }

    // ------------------------------------------------------------------
    // Page
    // ------------------------------------------------------------------
    public static string ToHtml(ComparisonInfo info, IReadOnlyList<TextToken> original, IReadOnlyList<TextToken> revised,
        DiffResult diff, bool changesOnly)
    {
        var summary = Summarize(original, revised, diff);
        var blocks = BuildBlocks(original, revised, diff);
        var sb = new StringBuilder();
        string H(string? s) => WebUtility.HtmlEncode(s ?? "");
        string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine("<title>Aegis Docs document comparison</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;color:#1b1f2a;margin:32px auto;max-width:900px;padding:0 20px;line-height:1.55}");
        sb.AppendLine("h1{font-size:24px;margin:0 0 4px}h2{font-size:18px;margin:28px 0 8px}");
        sb.AppendLine(".meta{color:#586074;margin:0 0 16px}.notice{background:#f1f3fb;border:1px solid #d5daee;border-radius:8px;padding:12px 16px;margin:16px 0}");
        sb.AppendLine("table{border-collapse:collapse;width:100%;margin:8px 0}th,td{border-bottom:1px solid #dfe3ee;padding:6px 10px;text-align:left;vertical-align:top;font-size:13.5px}");
        sb.AppendLine("th{background:#f6f7fb;font-weight:600;width:130px}code{font-family:Consolas,monospace;font-size:12.5px;word-break:break-all}");
        sb.AppendLine("p.para{margin:0 0 12px}p.gap{margin:0 0 12px;color:#8a91a5;text-align:center}.pg{display:inline-block;min-width:44px;margin-right:8px;color:#7a8196;font-size:11.5px;font-variant-numeric:tabular-nums}");
        sb.AppendLine("ins{background:#e3f4e8;color:#0b6b2e;text-decoration:underline}del{background:#fde7e7;color:#a31515;text-decoration:line-through}");
        sb.AppendLine(".small{color:#586074;font-size:12.5px}.key ins,.key del{padding:1px 6px;border-radius:4px;margin-right:6px}");
        sb.AppendLine("@media print{body{margin:0;max-width:none}.notice{break-inside:avoid}p.para{break-inside:avoid}}");
        sb.AppendLine("</style></head><body>");

        sb.AppendLine("<h1>Document comparison</h1>");
        sb.AppendLine($"<p class=\"meta\">Run on {H(info.GeneratedAt.ToString("MMMM d, yyyy 'at' h:mm tt zzz", CultureInfo.InvariantCulture))} with Aegis Docs {H(info.AppVersion)}</p>");

        sb.AppendLine($"<div class=\"notice\"><strong>How to read this.</strong> {H(Disclaimer)}</div>");
        foreach (var note in info.Notes)
            sb.AppendLine($"<div class=\"notice\">{H(note)}</div>");

        sb.AppendLine("<table>");
        sb.AppendLine($"<tr><th>Original</th><td>{H(info.OriginalName)}<br><span class=\"small\">{H(ByteSize.Format(info.OriginalBytes))} · SHA-256 <code>{H(info.OriginalSha256)}</code></span></td></tr>");
        sb.AppendLine($"<tr><th>Revised</th><td>{H(info.RevisedName)}<br><span class=\"small\">{H(ByteSize.Format(info.RevisedBytes))} · SHA-256 <code>{H(info.RevisedSha256)}</code></span></td></tr>");
        sb.AppendLine("</table>");

        sb.AppendLine("<h2>Summary</h2>");
        if (summary.Identical)
        {
            sb.AppendLine("<p><strong>No differences in the words.</strong> The two documents read exactly the same.</p>");
        }
        else
        {
            sb.AppendLine($"<p>{N(summary.WordsRemoved)} word(s) removed, {N(summary.WordsAdded)} word(s) added, {N(summary.WordsUnchanged)} unchanged. " +
                          $"{N(summary.ParagraphsChanged)} paragraph(s) contain changes. The texts are about {summary.SimilarityPercent.ToString("0", CultureInfo.InvariantCulture)}% alike.</p>");
        }
        if (summary.TooDifferent)
            sb.AppendLine("<div class=\"notice\"><strong>These documents are very different.</strong> Too much changed to line the text up word by word, so everything between the shared beginning and the shared ending is shown as removed and added.</div>");

        sb.AppendLine("<p class=\"key small\"><del>removed</del><ins>added</ins> Unmarked text is the same in both.</p>");

        sb.AppendLine(changesOnly ? "<h2>Changes</h2>" : "<h2>Full text with changes marked</h2>");
        if (changesOnly && summary.Identical)
        {
            sb.AppendLine("<p>Nothing to show.</p>");
        }
        else
        {
            var show = new bool[blocks.Count];
            for (int i = 0; i < blocks.Count; i++)
            {
                if (!changesOnly) { show[i] = true; continue; }
                if (!blocks[i].Changed) continue;
                show[i] = true;
                if (i > 0) show[i - 1] = true;                       // one paragraph of context either side
                if (i + 1 < blocks.Count) show[i + 1] = true;
            }

            bool gap = false;
            for (int i = 0; i < blocks.Count; i++)
            {
                if (!show[i]) { gap = true; continue; }
                if (gap && changesOnly) sb.AppendLine("<p class=\"gap\">· · ·</p>");
                gap = false;
                AppendBlock(sb, blocks[i], H);
            }
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    // ------------------------------------------------------------------

    private sealed class Block
    {
        public List<(string Text, DiffKind Kind)> Words { get; } = new();
        public bool Changed { get; set; }
        public int? Page { get; set; }
    }

    private static List<Block> BuildBlocks(IReadOnlyList<TextToken> original, IReadOnlyList<TextToken> revised, DiffResult diff)
    {
        var blocks = new List<Block>();
        var current = new Block();

        void Add(TextToken token, DiffKind kind, bool endsParagraph)
        {
            current.Words.Add((token.Text, kind));
            if (kind != DiffKind.Equal) current.Changed = true;
            current.Page ??= token.Page;
            if (endsParagraph)
            {
                blocks.Add(current);
                current = new Block();
            }
        }

        for (int s = 0; s < diff.Segments.Count; s++)
        {
            var seg = diff.Segments[s];
            switch (seg.Kind)
            {
                case DiffKind.Equal:
                    for (int i = 0; i < seg.NewCount; i++)
                    {
                        var token = revised[seg.NewStart + i];
                        Add(token, DiffKind.Equal, token.ParagraphEnd);
                    }
                    break;
                case DiffKind.Delete:
                {
                    // Words replaced by other words stay in the replacement's paragraph. Words that are simply gone keep their own paragraph end.
                    bool replaced = s + 1 < diff.Segments.Count && diff.Segments[s + 1].Kind == DiffKind.Insert;
                    for (int i = 0; i < seg.OldCount; i++)
                    {
                        var token = original[seg.OldStart + i];
                        Add(token, DiffKind.Delete, token.ParagraphEnd && !replaced);
                    }
                    break;
                }
                default:
                    for (int i = 0; i < seg.NewCount; i++)
                    {
                        var token = revised[seg.NewStart + i];
                        Add(token, DiffKind.Insert, token.ParagraphEnd);
                    }
                    break;
            }
        }
        if (current.Words.Count > 0) blocks.Add(current);
        return blocks;
    }

    private static void AppendBlock(StringBuilder sb, Block block, Func<string?, string> h)
    {
        sb.Append("<p class=\"para\">");
        if (block.Page is int page) sb.Append($"<span class=\"pg\">p. {page}</span>");

        DiffKind? open = null;
        bool first = true;
        foreach (var (text, kind) in block.Words)
        {
            if (open != kind)
            {
                if (open is DiffKind.Delete) sb.Append("</del>");
                else if (open is DiffKind.Insert) sb.Append("</ins>");

                if (!first) sb.Append(' ');
                if (kind == DiffKind.Delete) sb.Append("<del>");
                else if (kind == DiffKind.Insert) sb.Append("<ins>");
                open = kind;
            }
            else
            {
                sb.Append(' ');
            }
            sb.Append(h(text));
            first = false;
        }
        if (open is DiffKind.Delete) sb.Append("</del>");
        else if (open is DiffKind.Insert) sb.Append("</ins>");
        sb.AppendLine("</p>");
    }
}
