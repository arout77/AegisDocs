using System.Globalization;
using System.Net;
using System.Text;

namespace ReceiptCapture.Core.Compliance;

/// <summary>Writes a scan report as a web page (opens anywhere, prints to PDF) or as a CSV for a spreadsheet.</summary>
public static class ScanReportWriter
{
    /// <summary>The wording that goes at the top of every report and next to the Scan button. Keep it honest.</summary>
    public const string Disclaimer =
        "This check helps you review documents for the items listed below. It is a first pass, not a guarantee: " +
        "it can miss things and it can flag things that are harmless. It does not certify that a document, or your " +
        "organization, meets any law or standard. Have a person review the documents, and ask your compliance " +
        "officer or counsel where it matters.";

    public const string ValuesHiddenNote =
        "Found values are partly hidden in this report on purpose, so the report does not become another copy of the sensitive information.";

    // ------------------------------------------------------------------
    // HTML
    // ------------------------------------------------------------------
    public static string ToHtml(ScanReport report)
    {
        var sb = new StringBuilder();
        string H(string? s) => WebUtility.HtmlEncode(s ?? "");

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine("<title>Aegis Docs compliance check report</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;color:#1b1f2a;margin:32px auto;max-width:960px;padding:0 20px;line-height:1.45}");
        sb.AppendLine("h1{font-size:24px;margin:0 0 4px}h2{font-size:18px;margin:28px 0 8px}h3{font-size:15px;margin:22px 0 6px;word-break:break-all}");
        sb.AppendLine(".meta{color:#586074;margin:0 0 16px}.notice{background:#f1f3fb;border:1px solid #d5daee;border-radius:8px;padding:12px 16px;margin:16px 0}");
        sb.AppendLine("table{border-collapse:collapse;width:100%;margin:8px 0}th,td{border-bottom:1px solid #dfe3ee;padding:6px 10px;text-align:left;vertical-align:top;font-size:13.5px}");
        sb.AppendLine("th{background:#f6f7fb;font-weight:600}td.num{text-align:right}code{font-family:Consolas,monospace;font-size:13px}");
        sb.AppendLine(".sev-High{color:#b42318;font-weight:600}.sev-Medium{color:#b54708;font-weight:600}.sev-Low{color:#586074}");
        sb.AppendLine(".small{color:#586074;font-size:12.5px}ul{margin:6px 0 6px 20px;padding:0}li{margin:3px 0}");
        sb.AppendLine("@media print{body{margin:0;max-width:none}.notice{break-inside:avoid}h3{break-after:avoid}tr{break-inside:avoid}}");
        sb.AppendLine("</style></head><body>");

        sb.AppendLine("<h1>Compliance check report</h1>");
        sb.AppendLine($"<p class=\"meta\">Checked against: <strong>{H(report.Profile.Name)}</strong><br>" +
                      $"Run on {H(report.GeneratedAt.ToString("MMMM d, yyyy 'at' h:mm tt zzz", CultureInfo.InvariantCulture))} " +
                      $"with Aegis Docs {H(report.AppVersion)}</p>");

        sb.AppendLine($"<div class=\"notice\"><strong>How to read this report.</strong> {H(Disclaimer)}<br><span class=\"small\">{H(ValuesHiddenNote)}</span></div>");

        // Summary
        sb.AppendLine("<h2>Summary</h2>");
        sb.AppendLine($"<p>{report.FilesScanned} file(s) scanned" +
                      (report.FilesSkipped > 0 ? $", {report.FilesSkipped} not scanned" : "") +
                      $". {report.TotalFindings} item(s) to review in total.</p>");
        sb.AppendLine("<table><tr><th>File</th><th>Pages</th><th>High</th><th>Medium</th><th>Low</th><th>Result</th></tr>");
        foreach (var d in report.Documents)
        {
            int Count(FindingSeverity s) => d.Findings.Count(f => f.Severity == s);
            sb.AppendLine($"<tr><td>{H(d.FileName)}</td><td class=\"num\">{(d.Outcome == ScanOutcome.Scanned ? d.PageCount.ToString(CultureInfo.InvariantCulture) : "–")}</td>" +
                          $"<td class=\"num\">{Count(FindingSeverity.High)}</td><td class=\"num\">{Count(FindingSeverity.Medium)}</td><td class=\"num\">{Count(FindingSeverity.Low)}</td>" +
                          $"<td>{H(d.StatusText)}</td></tr>");
        }
        sb.AppendLine("</table>");

        // Per file
        sb.AppendLine("<h2>Details</h2>");
        foreach (var d in report.Documents)
        {
            sb.AppendLine($"<h3>{H(d.FileName)}</h3>");
            if (d.Outcome == ScanOutcome.Scanned && d.Sha256 != null)
                sb.AppendLine($"<p class=\"small\">SHA-256 of the file as scanned: <code>{H(d.Sha256)}</code></p>");

            if (d.Outcome == ScanOutcome.Skipped)
            {
                sb.AppendLine($"<p>{H(d.StatusText)}. This file was <strong>not</strong> checked.</p>");
                continue;
            }

            if (!string.IsNullOrEmpty(d.Note))
                sb.AppendLine($"<p><strong>Note:</strong> {H(char.ToUpperInvariant(d.Note[0]) + d.Note[1..])}.</p>");

            if (d.Findings.Count == 0)
            {
                sb.AppendLine("<p>Nothing was found by this scan. That is not proof the document has no sensitive information.</p>");
                continue;
            }

            sb.AppendLine("<table><tr><th>Severity</th><th>Page</th><th>Line</th><th>Type</th><th>Value (partly hidden)</th></tr>");
            foreach (var f in d.Findings)
                sb.AppendLine($"<tr><td class=\"sev-{f.Severity}\">{f.Severity}</td><td class=\"num\">{f.Page}</td><td class=\"num\">{f.Line}</td>" +
                              $"<td>{H(f.Description)}</td><td><code>{H(f.MaskedValue)}</code></td></tr>");
            sb.AppendLine("</table>");
        }

        // What was checked
        sb.AppendLine("<h2>What this check looks for</h2><ul>");
        foreach (var c in report.Profile.Checks)
            sb.AppendLine($"<li>{H(c.Explanation)} <span class=\"small\">({c.Severity})</span></li>");
        if (report.CustomTermCount > 0)
            sb.AppendLine($"<li>{report.CustomTermCount} word(s) or name(s) you added</li>");
        sb.AppendLine("</ul>");

        sb.AppendLine("<h2>What this check cannot find</h2><ul>");
        foreach (var n in report.Profile.NotChecked)
            sb.AppendLine($"<li>{H(n)}</li>");
        sb.AppendLine("<li>Text the reader can't make out: poor scans, handwriting, very small print, or pages that are blank to it.</li>");
        sb.AppendLine("</ul>");

        sb.AppendLine($"<p class=\"small\">{H(report.Profile.Summary)}</p>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // CSV
    // ------------------------------------------------------------------
    public static string ToCsv(ScanReport report)
    {
        var sb = new StringBuilder();
        sb.Append('\uFEFF');     // lets Excel read the bullets in hidden values as text
        sb.AppendLine(string.Join(",", new[] { "File", "Result", "Severity", "Page", "Line", "Type", "Value (partly hidden)", "Checked against", "Run on" }.Select(Cell)));

        var run = report.GeneratedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        foreach (var d in report.Documents)
        {
            if (d.Outcome == ScanOutcome.Skipped || d.Findings.Count == 0)
            {
                sb.AppendLine(string.Join(",", new[] { d.FileName, d.StatusText, "", "", "", "", "", report.Profile.Name, run }.Select(Cell)));
                continue;
            }
            foreach (var f in d.Findings)
                sb.AppendLine(string.Join(",", new[]
                {
                    d.FileName, "Item to review", f.Severity.ToString(),
                    f.Page.ToString(CultureInfo.InvariantCulture), f.Line.ToString(CultureInfo.InvariantCulture),
                    f.Description, f.MaskedValue, report.Profile.Name, run,
                }.Select(Cell)));
        }
        return sb.ToString();
    }

    /// <summary>Quotes a CSV cell, and stops one that starts with = + - @ from being run as a formula in Excel.</summary>
    public static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
