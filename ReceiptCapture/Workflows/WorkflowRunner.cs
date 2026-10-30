using ReceiptCapture.Core.Storage;
using System.Text;
using ReceiptCapture.Core;
using ReceiptCapture.Core.Compliance;
using ReceiptCapture.Core.HiddenContent;
using ReceiptCapture.Core.Pdf;
using ReceiptCapture.Core.Redaction;
using ReceiptCapture.Services;

namespace ReceiptCapture.Workflows;

/// <summary>What happened to one file. Shown in the activity list and written to the workflow log.</summary>
public sealed record WorkflowOutcome(
    string Workflow, string File, string Result, int ItemsToReview, int HiddenItems,
    string? CleanCopy, string? CleanCopySha, string? StillHidden, string? Report, string Sha256, bool Remember)
{
    public bool NeedsReview => ItemsToReview > 0;
}

/// <summary>
/// Runs one workflow on one file, with nobody there to answer questions. Originals are only ever read. Anything that
/// would need a person (a password, a decision) ends as "not scanned" with the reason, never a guess.
/// </summary>
public sealed class WorkflowRunner
{
    private const int MaxPages = 150;
    private readonly IRedactionService _service;

    public WorkflowRunner(IRedactionService service) => _service = service;

    public async Task<WorkflowOutcome?> RunAsync(
        Workflow wf, string path, string displayName, WorkflowStore store, CancellationToken cancel)
    {
        var profile = ComplianceProfiles.All.FirstOrDefault(p => p.Name == wf.ProfileName) ?? ComplianceProfiles.Hipaa;
        var terms = wf.Terms();

        if (!File.Exists(path)) return null;
        if (InputFile.Classify(path) == InputKind.Unsupported) return null;     // not a document type: not worth a log line

        var sha = await Task.Run(() => FileHasher.Sha256(path), cancel);
        if (store.WasHandled(wf.Id, sha)) return null;                          // same content as something already done

        Directory.CreateDirectory(wf.OutputFolder);

        if (InputFile.IsEncryptedDocx(path))
            return Finish(wf, displayName, "Not scanned: password-protected (remove the password first)", 0, 0, null, null, null, null, sha, true);

        try
        {
            HiddenInspection? hidden = wf.CheckHiddenContent ? await Task.Run(() => HiddenContentInspector.Inspect(path), cancel) : null;

            List<ScannedPage> pages;
            try { pages = await _service.ScanAsync(path, new Progress<string>(_ => { }), MaxPages, null); }
            catch (PdfPasswordException)
            {
                return Finish(wf, displayName, "Not scanned: the PDF needs a password", 0, 0, null, null, null, null, sha, true);
            }

            cancel.ThrowIfCancellationRequested();
            var result = ComplianceScanner.ScanPages(path, sha, pages, profile, terms, hidden, displayName);
            pages = null!;      // page pictures can go now

            var now = DateTimeOffset.Now;
            string? reportPath = null;
            if (wf.SaveReports)
            {
                var report = new ScanReport(profile, new List<DocumentScanResult> { result }, now, AppInfo.Version, terms.Length, false, wf.CheckHiddenContent);
                reportPath = UniquePath(Path.Combine(wf.OutputFolder, "Reports", Flat(displayName) + " - compliance report.html"));
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                await File.WriteAllTextAsync(reportPath, ScanReportWriter.ToHtml(report), new UTF8Encoding(false), cancel);
            }

            if (result.Outcome != ScanOutcome.Scanned)
                return Finish(wf, displayName, "Not scanned: " + (result.Note ?? result.StatusText), 0, 0, null, null, null, reportPath, sha, false);

            string? copy = null, copySha = null, still = null;
            if (wf.MakeCleanCopies && result.HiddenItems.Count > 0)
                (copy, copySha, still) = await MakeCleanCopyAsync(wf, path, displayName, cancel);

            var text = result.StatusText;
            if (copy != null) text += still == null ? "; clean copy made and re-checked" : "; clean copy made, but it still shows hidden content";
            return Finish(wf, displayName, text, result.Findings.Count, result.HiddenItems.Count, copy, copySha, still, reportPath, sha, true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Might be a file still in use: not remembered, so a later run tries again.
            return Finish(wf, displayName, "Not scanned: " + FirstLine(ex.Message), 0, 0, null, null, null, null, sha, false);
        }
    }

    private async Task<(string? Copy, string? Sha, string? Still)> MakeCleanCopyAsync(
        Workflow wf, string path, string displayName, CancellationToken cancel)
    {
        var options = new CleanOptions
        {
            DocumentProperties = wf.CleanProperties,
            Comments = wf.CleanComments,
            TrackedChanges = wf.CleanTrackedChanges,
            HiddenText = wf.CleanHiddenText,
            Attachments = wf.CleanAttachments,
            PreviewImages = wf.CleanPreviews,
            PictureMetadata = wf.CleanPictures,
        };

        bool isPdf = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        var folder = Path.Combine(wf.OutputFolder, "Clean copies");
        var destination = HiddenContentCleaner.CleanCopyPath(folder, displayName, isPdf ? ".pdf" : null);

        if (isPdf)
            await PdfRebuilder.RebuildAsync(_service, path, destination, wf.KeepPdfSearchable, MaxPages, new Progress<string>(_ => { }), cancel);
        else
            await Task.Run(() => HiddenContentCleaner.CleanFile(path, destination, options), cancel);

        var after = await Task.Run(() => HiddenContentInspector.Inspect(destination), cancel);
        var left = after.Items.Select(x => x.What).ToList();
        if (!after.WasChecked) left.Add("the copy could not be re-checked (" + after.Note + ")");
        var copySha = await Task.Run(() => FileHasher.Sha256(destination), cancel);
        return (destination, copySha, left.Count == 0 ? null : string.Join("; ", left.Take(3)) + (left.Count > 3 ? $" and {left.Count - 3} more" : ""));
    }

    private WorkflowOutcome Finish(
        Workflow wf, string file, string result, int review, int hidden,
        string? copy, string? copySha, string? still, string? report, string sha, bool remember)
    {
        var outcome = new WorkflowOutcome(wf.Name, file, result, review, hidden, copy, copySha, still, report, sha, remember);
        AppendLog(wf, outcome);
        return outcome;
    }

    // ------------------------------------------------------------------ the running record
    private static readonly string[] LogHeader =
    {
        "When", "Workflow", "File", "File SHA-256", "Result", "Items to review", "Hidden-content items",
        "Clean copy", "Clean copy SHA-256", "Still hidden in copy", "Report", "Aegis Docs version", "Run by",
    };

    private static void AppendLog(Workflow wf, WorkflowOutcome o)
    {
        try
        {
            var path = Path.Combine(wf.OutputFolder, "Workflow log.csv");
            bool fresh = !File.Exists(path);
            using var writer = new StreamWriter(path, append: true, new UTF8Encoding(fresh));
            if (fresh) writer.WriteLine(string.Join(",", LogHeader.Select(Csv)));
            writer.WriteLine(string.Join(",", new[]
            {
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"), o.Workflow, o.File, o.Sha256, o.Result,
                o.ItemsToReview.ToString(), o.HiddenItems.ToString(), o.CleanCopy ?? "", o.CleanCopySha ?? "",
                o.StillHidden ?? "", o.Report ?? "", AppInfo.Version, $"{Environment.UserName}@{Environment.MachineName}",
            }.Select(Csv)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log is a record, not a gate: the activity list still shows the result.
        }
    }

    /// <summary>CSV cell. Cells that start like a formula get a leading apostrophe so Excel never runs them.</summary>
    private static string Csv(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string Flat(string name)
    {
        var s = name.Replace('\\', '-').Replace('/', '-');
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length > 120 ? s[..120] : s;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int n = 2; ; n++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string FirstLine(string message)
    {
        var line = message.Split('\n')[0].Trim();
        return line.Length <= 140 ? line : line[..140] + "…";
    }
}
