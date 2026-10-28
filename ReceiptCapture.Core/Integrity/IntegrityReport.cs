using System.Globalization;
using System.Net;
using System.Text;

namespace ReceiptCapture.Core.Integrity;

/// <summary>Writes a manifest and the result of checking against one as web pages (print to PDF to keep) or CSV for a spreadsheet.</summary>
public static class IntegrityReport
{
    public const string HowItWorks =
        "Each file below has a SHA-256 fingerprint: a long code worked out from the file's contents. If even one character of a file " +
        "changes, its fingerprint changes completely. Checking a folder against this list later shows whether anything was altered, " +
        "removed or added.";

    public const string Limits =
        "This list shows what the files looked like when it was made. It does not on its own prove when that was, because anyone can make a new list. " +
        "To pin down the date, send the list fingerprint to someone independent (an email to yourself or the other side, a docket entry) at the time you make it.";

    // ------------------------------------------------------------------
    // The manifest
    // ------------------------------------------------------------------
    public static string ManifestHtml(IntegrityManifest manifest)
    {
        var sb = new StringBuilder();
        string H(string? s) => WebUtility.HtmlEncode(s ?? "");

        Begin(sb, "Aegis Docs integrity manifest");
        sb.AppendLine("<h1>Integrity manifest</h1>");
        sb.AppendLine($"<p class=\"meta\">Folder: <strong>{H(manifest.FolderName)}</strong><br>" +
                      $"Made on {H(Stamp(manifest.CreatedAt))} by {H(manifest.CreatedBy)} with {H(manifest.Tool)}</p>");

        if (!string.IsNullOrWhiteSpace(manifest.Matter) || !string.IsNullOrWhiteSpace(manifest.PreparedBy) || !string.IsNullOrWhiteSpace(manifest.Notes))
        {
            sb.AppendLine("<table class=\"facts\">");
            if (!string.IsNullOrWhiteSpace(manifest.Matter)) sb.AppendLine($"<tr><th>Matter / reference</th><td>{H(manifest.Matter)}</td></tr>");
            if (!string.IsNullOrWhiteSpace(manifest.PreparedBy)) sb.AppendLine($"<tr><th>Prepared by</th><td>{H(manifest.PreparedBy)}</td></tr>");
            if (!string.IsNullOrWhiteSpace(manifest.Notes)) sb.AppendLine($"<tr><th>Notes</th><td>{H(manifest.Notes)}</td></tr>");
            sb.AppendLine("</table>");
        }

        sb.AppendLine($"<div class=\"notice\"><strong>List fingerprint</strong><br><code class=\"big\">{H(manifest.ComputeFingerprint())}</code><br>" +
                      $"<span class=\"small\">{manifest.FileCount:N0} file(s), {H(ByteSize.Format(manifest.TotalBytes))}. This one code stands for the whole list below.</span></div>");
        sb.AppendLine($"<div class=\"notice\"><strong>How this works.</strong> {H(HowItWorks)}<br><span class=\"small\">{H(Limits)}</span></div>");

        var unreadable = manifest.Entries.Where(e => e.Error != null).ToList();
        if (unreadable.Count > 0)
        {
            sb.AppendLine("<h2>Files that could not be read</h2><ul>");
            foreach (var e in unreadable) sb.AppendLine($"<li>{H(e.Path)}: {H(e.Error)}</li>");
            sb.AppendLine("</ul>");
        }

        sb.AppendLine("<h2>Files</h2>");
        sb.AppendLine("<table><tr><th>File</th><th class=\"num\">Size</th><th>SHA-256</th></tr>");
        foreach (var e in manifest.Entries.Where(e => e.Error == null).OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine($"<tr><td>{H(e.Path)}</td><td class=\"num\">{H(ByteSize.Format(e.Size))}</td><td><code>{H(e.Sha256)}</code></td></tr>");
        sb.AppendLine("</table>");

        AppendLog(sb, manifest, H);
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    public static string ManifestCsv(IntegrityManifest manifest)
    {
        var sb = new StringBuilder();
        sb.AppendLine("File,Size (bytes),SHA-256,Last modified (UTC),Note");
        foreach (var e in manifest.Entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(string.Join(",",
                Csv(e.Path), e.Size.ToString(CultureInfo.InvariantCulture), Csv(e.Sha256),
                e.Modified == DateTimeOffset.MinValue ? "" : e.Modified.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Csv(e.Error ?? "")));
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // A check
    // ------------------------------------------------------------------
    public static string VerifyHtml(IntegrityManifest manifest, VerifyResult result, string folder, DateTimeOffset checkedAt, string checkedBy, string tool)
    {
        var sb = new StringBuilder();
        string H(string? s) => WebUtility.HtmlEncode(s ?? "");

        Begin(sb, "Aegis Docs integrity check");
        sb.AppendLine("<h1>Integrity check</h1>");
        sb.AppendLine($"<p class=\"meta\">Folder checked: <strong>{H(folder)}</strong><br>" +
                      $"Checked on {H(Stamp(checkedAt))} by {H(checkedBy)} with {H(tool)}</p>");

        if (result.AllGood)
            sb.AppendLine($"<div class=\"banner ok\"><strong>Everything matches.</strong> All {result.Matching:N0} recorded file(s) are unchanged and nothing has been added.</div>");
        else
            sb.AppendLine("<div class=\"banner bad\"><strong>Differences found.</strong> The folder is not the same as when the list was made. See the table below.</div>");

        if (!manifest.FingerprintIsIntact)
            sb.AppendLine("<div class=\"banner bad\"><strong>The list itself was edited.</strong> Its saved fingerprint no longer matches the file list inside it, " +
                          "so the list can't be relied on.</div>");

        sb.AppendLine("<table class=\"facts\">");
        sb.AppendLine($"<tr><th>List made</th><td>{H(Stamp(manifest.CreatedAt))} by {H(manifest.CreatedBy)}</td></tr>");
        sb.AppendLine($"<tr><th>List fingerprint</th><td><code>{H(manifest.ComputeFingerprint())}</code></td></tr>");
        if (!string.IsNullOrWhiteSpace(manifest.Matter)) sb.AppendLine($"<tr><th>Matter / reference</th><td>{H(manifest.Matter)}</td></tr>");
        sb.AppendLine("</table>");

        sb.AppendLine("<h2>Summary</h2>");
        sb.AppendLine("<table><tr><th>Result</th><th class=\"num\">Files</th></tr>");
        foreach (var (status, label) in new[]
        {
            (VerifyStatus.Matches, "Unchanged"), (VerifyStatus.Changed, "Changed"), (VerifyStatus.Missing, "Missing"),
            (VerifyStatus.Added, "New (not in the list)"), (VerifyStatus.Unreadable, "Couldn't be read"), (VerifyStatus.NotRecorded, "Never fingerprinted"),
        })
        {
            int n = result.Count(status);
            if (n > 0 || status == VerifyStatus.Matches)
                sb.AppendLine($"<tr><td>{H(label)}</td><td class=\"num\">{n:N0}</td></tr>");
        }
        sb.AppendLine("</table>");

        var problems = result.Items.Where(i => i.Status != VerifyStatus.Matches).ToList();
        if (problems.Count > 0)
        {
            sb.AppendLine("<h2>What differs</h2>");
            sb.AppendLine("<table><tr><th>File</th><th>Result</th><th>Details</th></tr>");
            foreach (var item in problems.OrderBy(i => i.Status).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"<tr><td>{H(item.Path)}</td><td class=\"s-{item.Status}\">{H(StatusLabel(item.Status))}</td><td>{H(item.Detail)}" +
                              (item.Status == VerifyStatus.Changed && item.ActualSha256 != null
                                  ? $"<br><span class=\"small\">was <code>{H(item.ExpectedSha256)}</code><br>now <code>{H(item.ActualSha256)}</code></span>" : "") +
                              "</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        sb.AppendLine("<h2>Unchanged files</h2>");
        var matches = result.Items.Where(i => i.Status == VerifyStatus.Matches).ToList();
        if (matches.Count == 0) sb.AppendLine("<p>None.</p>");
        else
        {
            sb.AppendLine("<table><tr><th>File</th><th>SHA-256</th></tr>");
            foreach (var item in matches.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"<tr><td>{H(item.Path)}</td><td><code>{H(item.ActualSha256)}</code></td></tr>");
            sb.AppendLine("</table>");
        }

        AppendLog(sb, manifest, H);
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    public static string VerifyCsv(VerifyResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("File,Result,Details,Expected SHA-256,Actual SHA-256");
        foreach (var item in result.Items.OrderBy(i => i.Status == VerifyStatus.Matches).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine(string.Join(",", Csv(item.Path), Csv(StatusLabel(item.Status)), Csv(item.Detail), Csv(item.ExpectedSha256 ?? ""), Csv(item.ActualSha256 ?? "")));
        return sb.ToString();
    }

    public static string StatusLabel(VerifyStatus status) => status switch
    {
        VerifyStatus.Matches => "Unchanged",
        VerifyStatus.Changed => "Changed",
        VerifyStatus.Missing => "Missing",
        VerifyStatus.Added => "New",
        VerifyStatus.Unreadable => "Couldn't be read",
        _ => "Never fingerprinted",
    };

    // ------------------------------------------------------------------

    private static void AppendLog(StringBuilder sb, IntegrityManifest manifest, Func<string?, string> h)
    {
        sb.AppendLine("<h2>Custody log</h2>");
        if (manifest.Events.Count == 0) { sb.AppendLine("<p>No entries.</p>"); return; }

        sb.AppendLine("<table><tr><th>When</th><th>Who</th><th>What</th><th>Details</th></tr>");
        foreach (var e in manifest.Events)
            sb.AppendLine($"<tr><td>{h(Stamp(e.At))}</td><td>{h(e.By)}</td><td>{h(e.Action)}</td><td>{h(e.Detail)}</td></tr>");
        sb.AppendLine("</table>");
    }

    private static void Begin(StringBuilder sb, string title)
    {
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine($"<title>{WebUtility.HtmlEncode(title)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;color:#1b1f2a;margin:32px auto;max-width:960px;padding:0 20px;line-height:1.45}");
        sb.AppendLine("h1{font-size:24px;margin:0 0 4px}h2{font-size:18px;margin:28px 0 8px}");
        sb.AppendLine(".meta{color:#586074;margin:0 0 16px}.notice{background:#f1f3fb;border:1px solid #d5daee;border-radius:8px;padding:12px 16px;margin:16px 0}");
        sb.AppendLine(".banner{border-radius:8px;padding:12px 16px;margin:16px 0}.banner.ok{background:#e3f4e8;border:1px solid #b7dfc3}.banner.bad{background:#fde7e7;border:1px solid #f0bcbc}");
        sb.AppendLine("table{border-collapse:collapse;width:100%;margin:8px 0}th,td{border-bottom:1px solid #dfe3ee;padding:6px 10px;text-align:left;vertical-align:top;font-size:13.5px}");
        sb.AppendLine("th{background:#f6f7fb;font-weight:600}td.num,th.num{text-align:right;white-space:nowrap}table.facts th{width:170px}");
        sb.AppendLine("code{font-family:Consolas,monospace;font-size:12px;word-break:break-all}code.big{font-size:14px}.small{color:#586074;font-size:12.5px}");
        sb.AppendLine(".s-Changed,.s-Missing,.s-Unreadable{color:#b42318;font-weight:600}.s-Added{color:#b54708;font-weight:600}.s-NotRecorded{color:#586074}");
        sb.AppendLine("ul{margin:6px 0 6px 20px;padding:0}li{margin:3px 0}");
        sb.AppendLine("@media print{body{margin:0;max-width:none}.notice,.banner{break-inside:avoid}tr{break-inside:avoid}h2{break-after:avoid}}");
        sb.AppendLine("</style></head><body>");
    }

    private static string Stamp(DateTimeOffset at) => at.ToString("MMMM d, yyyy 'at' h:mm tt zzz", CultureInfo.InvariantCulture);

    /// <summary>Quotes a CSV cell, and defuses text a spreadsheet would run as a formula.</summary>
    private static string Csv(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
