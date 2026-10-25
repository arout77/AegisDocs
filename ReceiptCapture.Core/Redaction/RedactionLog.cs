using System.Globalization;
using System.Net;
using System.Text;

namespace ReceiptCapture.Core.Redaction;

public sealed record RedactionLogKindRow(string Item, int Redacted, int LeftVisible);
public sealed record RedactionLogPageRow(int Page, int Redacted, int LeftVisible, int HandDrawn);

/// <summary>
/// A record of one redaction: which file, which copy, who and when, what was blacked out and what was found
/// but deliberately left visible, and what the check of the saved copy said. It lists kinds and counts only,
/// never the redacted values, so the log itself is safe to file next to the redacted copy.
/// </summary>
public sealed class RedactionLog
{
    public string SourceName { get; init; } = "";
    public string? SourceSha256 { get; init; }
    public string OutputName { get; init; } = "";
    public string? OutputSha256 { get; init; }
    public string? UserName { get; init; }
    public string AppVersion { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public int PageCount { get; init; }
    public IReadOnlyList<RedactionLogKindRow> Kinds { get; init; } = Array.Empty<RedactionLogKindRow>();
    public IReadOnlyList<RedactionLogPageRow> Pages { get; init; } = Array.Empty<RedactionLogPageRow>();
    public int HandDrawnBoxes { get; init; }
    public bool SearchableCopy { get; init; }
    public bool SourceWasProtected { get; init; }

    /// <summary>How many detected items the check of the saved copy looked for (hand-drawn boxes can't be checked).</summary>
    public int ItemsChecked { get; init; }

    /// <summary>How many of them could still be read in the saved copy. Zero is the goal; the log says what it means.</summary>
    public int ItemsStillReadable { get; init; }

    public int TotalRedacted => Kinds.Sum(k => k.Redacted) + HandDrawnBoxes;
    public int TotalLeftVisible => Kinds.Sum(k => k.LeftVisible);

    /// <summary>Builds the log from every region the person saw (ticked or not).</summary>
    public static RedactionLog Create(
        string sourcePath, string? sourceSha256, string outputPath, string? outputSha256,
        DateTimeOffset createdAt, string appVersion, string? userName, int pageCount,
        IEnumerable<RedactionRegion> allRegions, bool searchableCopy, bool sourceWasProtected,
        int itemsChecked, int itemsStillReadable)
    {
        var regions = allRegions.ToList();
        var kinds = regions.Where(r => !r.IsManual)
            .GroupBy(r => r.Description)
            .Select(g => new RedactionLogKindRow(g.Key, g.Count(r => r.Approved), g.Count(r => !r.Approved)))
            .OrderByDescending(k => k.Redacted).ThenBy(k => k.Item)
            .ToList();

        var pages = regions.GroupBy(r => r.Page).OrderBy(g => g.Key)
            .Select(g => new RedactionLogPageRow(g.Key + 1,
                g.Count(r => r.Approved && !r.IsManual), g.Count(r => !r.Approved && !r.IsManual), g.Count(r => r.Approved && r.IsManual)))
            .ToList();

        return new RedactionLog
        {
            SourceName = Path.GetFileName(sourcePath),
            SourceSha256 = sourceSha256,
            OutputName = Path.GetFileName(outputPath),
            OutputSha256 = outputSha256,
            UserName = userName,
            AppVersion = appVersion,
            CreatedAt = createdAt,
            PageCount = pageCount,
            Kinds = kinds,
            Pages = pages,
            HandDrawnBoxes = regions.Count(r => r.IsManual && r.Approved),
            SearchableCopy = searchableCopy,
            SourceWasProtected = sourceWasProtected,
            ItemsChecked = itemsChecked,
            ItemsStillReadable = itemsStillReadable,
        };
    }

    /// <summary>Where the log is saved: next to the redacted copy, "copy name - redaction log.html".</summary>
    public static string LogPathFor(string outputPath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? "",
                        Path.GetFileNameWithoutExtension(outputPath) + " - redaction log.html");
}

public static class RedactionLogWriter
{
    public static string ToHtml(RedactionLog log)
    {
        string H(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>Redaction log</title><style>");
        sb.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;color:#1b1f2a;margin:32px auto;max-width:900px;padding:0 20px;line-height:1.45}");
        sb.AppendLine("h1{font-size:24px;margin:0 0 4px}h2{font-size:18px;margin:26px 0 8px}.meta{color:#586074;margin:0 0 16px}");
        sb.AppendLine(".notice{background:#f1f3fb;border:1px solid #d5daee;border-radius:8px;padding:12px 16px;margin:16px 0}");
        sb.AppendLine("table{border-collapse:collapse;width:100%;margin:8px 0}th,td{border-bottom:1px solid #dfe3ee;padding:6px 10px;text-align:left;vertical-align:top;font-size:13.5px}");
        sb.AppendLine("th{background:#f6f7fb;font-weight:600}td.num{text-align:right}code{font-family:Consolas,monospace;font-size:12.5px;word-break:break-all}.small{color:#586074;font-size:12.5px}");
        sb.AppendLine("@media print{body{margin:0;max-width:none}tr{break-inside:avoid}}</style></head><body>");

        sb.AppendLine("<h1>Redaction log</h1>");
        sb.AppendLine($"<p class=\"meta\">Saved {H(log.CreatedAt.ToString("MMMM d, yyyy 'at' h:mm tt zzz", CultureInfo.InvariantCulture))}" +
                      (string.IsNullOrWhiteSpace(log.UserName) ? "" : $" by {H(log.UserName)}") +
                      $" with Aegis Docs {H(log.AppVersion)}</p>");

        sb.AppendLine("<div class=\"notice\">This log records what was redacted, by kind and page. It does <strong>not</strong> contain the redacted text itself. " +
                      "It is a record of the steps taken, not a certificate: look over the redacted copy yourself before you share it.</div>");

        sb.AppendLine("<h2>Files</h2><table><tr><th></th><th>Name</th><th>SHA-256 fingerprint</th></tr>");
        sb.AppendLine($"<tr><td>Original</td><td>{H(log.SourceName)}</td><td><code>{H(log.SourceSha256 ?? "not recorded")}</code></td></tr>");
        sb.AppendLine($"<tr><td>Redacted copy</td><td>{H(log.OutputName)}</td><td><code>{H(log.OutputSha256 ?? "not recorded")}</code></td></tr></table>");
        sb.AppendLine($"<p class=\"small\">{log.PageCount} page(s). The copy was rebuilt from pictures of the pages, so the original text, fonts and file properties are not in it." +
                      (log.SearchableCopy ? " It is searchable: the text that was not covered can be searched and copied." : " It is not searchable.") +
                      (log.SourceWasProtected ? " The original was password-protected; the copy is not." : "") + "</p>");

        sb.AppendLine("<h2>Summary</h2>");
        sb.AppendLine($"<p><strong>{log.TotalRedacted}</strong> item(s) redacted" +
                      (log.HandDrawnBoxes > 0 ? $", including {log.HandDrawnBoxes} hand-drawn box(es)" : "") +
                      $". <strong>{log.TotalLeftVisible}</strong> detected item(s) were deliberately left visible.</p>");

        if (log.Kinds.Count > 0)
        {
            sb.AppendLine("<table><tr><th>Kind of item</th><th>Redacted</th><th>Found but left visible</th></tr>");
            foreach (var k in log.Kinds)
                sb.AppendLine($"<tr><td>{H(k.Item)}</td><td class=\"num\">{k.Redacted}</td><td class=\"num\">{k.LeftVisible}</td></tr>");
            sb.AppendLine("</table>");
        }

        if (log.Pages.Count > 0)
        {
            sb.AppendLine("<h2>By page</h2><table><tr><th>Page</th><th>Redacted</th><th>Hand-drawn boxes</th><th>Found but left visible</th></tr>");
            foreach (var p in log.Pages)
                sb.AppendLine($"<tr><td class=\"num\">{p.Page}</td><td class=\"num\">{p.Redacted}</td><td class=\"num\">{p.HandDrawn}</td><td class=\"num\">{p.LeftVisible}</td></tr>");
            sb.AppendLine("</table>");
        }

        sb.AppendLine("<h2>Check of the saved copy</h2>");
        if (log.ItemsChecked == 0 && log.HandDrawnBoxes == 0)
            sb.AppendLine("<p>No detected items were redacted, so there was nothing to check for.</p>");
        else if (log.ItemsStillReadable == 0)
            sb.AppendLine($"<p>After saving, the copy was read back and checked for the {log.ItemsChecked} detected item(s) that were redacted. None could be read in it.</p>");
        else
            sb.AppendLine($"<p><strong>After saving, {log.ItemsStillReadable} of the {log.ItemsChecked} redacted item(s) could still be read in the copy.</strong> " +
                          "The copy should not be shared until they are covered.</p>");
        if (log.HandDrawnBoxes > 0)
            sb.AppendLine("<p class=\"small\">Hand-drawn boxes can't be checked automatically, so the pages they are on need a look by a person.</p>");
        sb.AppendLine("<p class=\"small\">The check can only find what the text reader can read, so it can't prove nothing sensitive remains.</p>");

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}
