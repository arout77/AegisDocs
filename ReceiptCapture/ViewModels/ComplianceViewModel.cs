using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using ReceiptCapture.Core;
using ReceiptCapture.Core.Compliance;
using ReceiptCapture.Core.HiddenContent;
using ReceiptCapture.Core.Pdf;
using ReceiptCapture.Core.Redaction;
using ReceiptCapture.Core.Storage;
using ReceiptCapture.Services;

namespace ReceiptCapture.ViewModels;

/// <summary>One file in the Compliance tab's list, with what the scan made of it.</summary>
public sealed class ScanFileItem : ObservableObject
{
    private readonly string? _relativePath;
    private DocumentScanResult? _result;
    private string _status = "Not scanned yet";

    /// <param name="relativePath">For a file that came from a folder: its path inside it, shown in lists and reports.</param>
    public ScanFileItem(string filePath, string? relativePath = null)
    {
        FilePath = filePath;
        _relativePath = relativePath;
    }

    public string FilePath { get; }
    public string Name => Path.GetFileName(FilePath);
    public string DisplayName => string.IsNullOrEmpty(_relativePath) ? Name : _relativePath;

    public DocumentScanResult? Result
    {
        get => _result;
        set
        {
            if (SetProperty(ref _result, value))
                Status = value?.StatusText ?? "Not scanned yet";
        }
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }
}

/// <summary>
/// The Compliance tab: pick what to check against (HIPAA, card data, ...), scan files or whole folders, read what was
/// found (values are partly hidden), look at hidden content (comments, metadata, attachments...), make clean copies,
/// and save a report. Files are read on this PC with the same offline reader the Redact tab uses, and the page
/// images are dropped as soon as each file has been scanned.
/// </summary>
public sealed class ComplianceViewModel : ObservableObject
{
    private const int MaxPages = 150;
    private const int MaxFiles = FolderScan.DefaultMaxFiles;

    private readonly IRedactionService _service;

    private ComplianceProfile _profile = ComplianceProfiles.Hipaa;
    private ScanFileItem? _selected;
    private bool _isBusy;
    private string _status = "Add files or a folder, choose what to check them against, then press Scan files. Your originals are never changed.";
    private string _customTermsText = "";
    private ScanReport? _report;
    private CancellationTokenSource? _cts;

    private bool _includeSubfolders = true;
    private bool _checkHiddenContent = true;
    private bool _cleanProperties = true, _cleanComments = true, _cleanTracked = true, _cleanHiddenText = true,
                 _cleanAttachments = true, _cleanPreviews = true, _cleanPictures = true, _keepPdfSearchable = true;

    public ComplianceViewModel(IRedactionService service)
    {
        _service = service;

        Items.CollectionChanged += (_, _) =>
        {
            _report = null;                      // a report describes one run: a changed list needs a new run
            OnPropertyChanged(nameof(IsEmpty));
        };

        AddFilesCommand = new RelayCommand(() => AddFiles(PickFiles?.Invoke() ?? Array.Empty<string>()), () => !IsBusy);
        AddFolderCommand = new RelayCommand(() =>
        {
            var folder = PickFolder?.Invoke("Choose a folder to check");
            if (!string.IsNullOrWhiteSpace(folder)) AddFiles(new[] { folder });
        }, () => !IsBusy);
        RemoveCommand = new RelayCommand(RemoveSelected, () => Selected != null && !IsBusy);
        ClearCommand = new RelayCommand(() => { Items.Clear(); Selected = null; }, () => Items.Count > 0 && !IsBusy);
        ScanCommand = new RelayCommand(async () => await ScanAsync(), () => Items.Count > 0 && !IsBusy);
        CancelCommand = new RelayCommand(StopRequested, () => IsBusy);
        SaveReportCommand = new RelayCommand(SaveReport, () => _report != null && !IsBusy);
        CleanCommand = new RelayCommand(async () => await CleanAsync(), () => !IsBusy && HasHiddenContentToClean);
    }

    // ------------------------------------------------------------------
    // Hooks the view fills in
    // ------------------------------------------------------------------
    public Action<string>? ShowMessage { get; set; }
    public Func<string[]?>? PickFiles { get; set; }

    /// <summary>Asks for a folder (the title is the dialog's heading); null if cancelled.</summary>
    public Func<string, string?>? PickFolder { get; set; }
    public Func<string, string?>? PickSavePath { get; set; }

    /// <summary>Asks a yes/no question.</summary>
    public Func<string, bool>? Confirm { get; set; }

    /// <summary>Asks for a PDF's password: (file name, last try was wrong) -> password, or null if cancelled.</summary>
    public Func<string, bool, string?>? AskPassword { get; set; }

    // ------------------------------------------------------------------
    // What the view shows
    // ------------------------------------------------------------------
    public ObservableCollection<ScanFileItem> Items { get; } = new();
    public ObservableCollection<Finding> Findings { get; } = new();
    public ObservableCollection<HiddenItem> HiddenItems { get; } = new();
    public bool IsEmpty => Items.Count == 0;

    public IReadOnlyList<ComplianceProfile> Profiles => ComplianceProfiles.All;

    public ComplianceProfile SelectedProfile
    {
        get => _profile;
        set
        {
            if (value == null || !SetProperty(ref _profile, value)) return;
            OnPropertyChanged(nameof(ProfileSummary));
            OnPropertyChanged(nameof(LooksForText));
            OnPropertyChanged(nameof(CannotFindText));

            // Results belong to the profile they were made with, so they're cleared rather than left to mislead.
            if (Items.Any(i => i.Result != null))
            {
                foreach (var item in Items) item.Result = null;
                _report = null;
                RefreshFindings();
                Status = "You changed what to check, so the earlier results were cleared. Press Scan files to check again.";
            }
        }
    }

    public string ProfileSummary => _profile.Summary;
    public string LooksForText => string.Join("\n", _profile.Checks.Select(c => "• " + c.Explanation));
    public string CannotFindText => string.Join("\n", _profile.NotChecked.Select(n => "• " + n));

    public string CustomTermsText
    {
        get => _customTermsText;
        set => SetProperty(ref _customTermsText, value ?? "");
    }

    public bool IncludeSubfolders { get => _includeSubfolders; set => SetProperty(ref _includeSubfolders, value); }
    public bool CheckHiddenContent { get => _checkHiddenContent; set => SetProperty(ref _checkHiddenContent, value); }

    // What a clean copy removes
    public bool CleanProperties { get => _cleanProperties; set => SetProperty(ref _cleanProperties, value); }
    public bool CleanComments { get => _cleanComments; set => SetProperty(ref _cleanComments, value); }
    public bool CleanTrackedChanges { get => _cleanTracked; set => SetProperty(ref _cleanTracked, value); }
    public bool CleanHiddenText { get => _cleanHiddenText; set => SetProperty(ref _cleanHiddenText, value); }
    public bool CleanAttachments { get => _cleanAttachments; set => SetProperty(ref _cleanAttachments, value); }
    public bool CleanPreviews { get => _cleanPreviews; set => SetProperty(ref _cleanPreviews, value); }
    public bool CleanPictures { get => _cleanPictures; set => SetProperty(ref _cleanPictures, value); }
    public bool KeepPdfSearchable { get => _keepPdfSearchable; set => SetProperty(ref _keepPdfSearchable, value); }

    public ScanFileItem? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value)) RefreshFindings();
        }
    }

    public string FindingsHeader
    {
        get
        {
            if (Selected == null) return "Select a file to see what was found in it.";
            return Selected.Result == null
                ? $"{Selected.DisplayName}: not scanned yet."
                : $"{Selected.DisplayName}: {Selected.Result.StatusText.ToLowerInvariant()}";
        }
    }

    public string FindingsNote
    {
        get
        {
            var note = Selected?.Result?.Note;
            if (string.IsNullOrEmpty(note) || Selected?.Result?.Outcome == ScanOutcome.Skipped) return "";
            return "Note: " + char.ToUpperInvariant(note[0]) + note[1..] + ".";
        }
    }

    public string FoundTabHeader => $"Items found ({Findings.Count})";
    public string HiddenTabHeader => $"Hidden content ({HiddenItems.Count})";

    private bool HasHiddenContentToClean => Items.Any(i => i.Result is { Outcome: ScanOutcome.Scanned } r && r.HiddenItems.Count > 0);

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public ICommand AddFilesCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SaveReportCommand { get; }
    public ICommand CleanCommand { get; }

    // ------------------------------------------------------------------
    // The list
    // ------------------------------------------------------------------
    /// <summary>Adds files and folders (used by the Add buttons and by drag and drop). A folder adds the readable files inside it.</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        if (IsBusy) return;

        int unsupported = 0, added = 0;
        bool capped = false;
        var problems = new List<string>();

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                FolderListing listing;
                try { listing = FolderScan.Enumerate(path, IncludeSubfolders, Math.Max(0, MaxFiles - Items.Count)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"couldn't read the folder {Path.GetFileName(path.TrimEnd('\\', '/'))} ({ex.Message})");
                    continue;
                }

                var rootName = Path.GetFileName(path.TrimEnd('\\', '/'));
                foreach (var file in listing.Files)
                    if (TryAdd(file.FullPath, Path.Combine(rootName, file.RelativePath))) added++;
                unsupported += listing.UnsupportedFiles;
                capped |= listing.WasCapped;
                continue;
            }

            if (InputFile.Classify(path) == InputKind.Unsupported) { unsupported++; continue; }
            if (Items.Count >= MaxFiles) { capped = true; continue; }
            if (TryAdd(path, null)) added++;
        }

        if (problems.Count > 0 || capped || unsupported > 0)
        {
            var parts = new List<string>(problems);
            if (capped) parts.Add($"only the first {MaxFiles} files are kept in one run, so the rest were left out");
            if (unsupported > 0) parts.Add($"{unsupported} file(s) weren't {InputFile.SupportedText}");
            ShowMessage?.Invoke("Note: " + string.Join("; ", parts) + ".");
        }

        if (Selected == null && Items.Count > 0) Selected = Items[0];
        if (Items.Count > 0) Status = $"{Items.Count} file(s) ready. Press Scan files.";
    }

    private bool TryAdd(string fullPath, string? relativePath)
    {
        if (Items.Count >= MaxFiles) return false;
        if (Items.Any(i => string.Equals(i.FilePath, fullPath, StringComparison.OrdinalIgnoreCase))) return false;
        Items.Add(new ScanFileItem(fullPath, relativePath));
        return true;
    }

    private void RemoveSelected()
    {
        if (Selected == null) return;
        var index = Items.IndexOf(Selected);
        Items.Remove(Selected);
        Selected = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
    }

    private void RefreshFindings()
    {
        Findings.Clear();
        HiddenItems.Clear();
        if (Selected?.Result is { } result)
        {
            foreach (var finding in result.Findings) Findings.Add(finding);
            foreach (var item in result.HiddenItems) HiddenItems.Add(item);
        }

        OnPropertyChanged(nameof(FindingsHeader));
        OnPropertyChanged(nameof(FindingsNote));
        OnPropertyChanged(nameof(FoundTabHeader));
        OnPropertyChanged(nameof(HiddenTabHeader));
    }

    private void StopRequested()
    {
        _cts?.Cancel();
        Status = "Stopping after the file in progress…";
    }

    // ------------------------------------------------------------------
    // Scanning
    // ------------------------------------------------------------------
    private async Task ScanAsync()
    {
        if (IsBusy || Items.Count == 0) return;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var profile = _profile;
        var checkHidden = CheckHiddenContent;
        var terms = CustomTermsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var files = Items.ToList();
        var results = new List<DocumentScanResult>();
        bool stopped = false;

        try
        {
            foreach (var item in files) { item.Result = null; item.Status = "Waiting…"; }
            RefreshFindings();

            for (int i = 0; i < files.Count; i++)
            {
                var item = files[i];

                if (token.IsCancellationRequested)
                {
                    stopped = true;
                    var skipped = ComplianceScanner.Skipped(item.FilePath, "the scan was stopped before this file", item.DisplayName);
                    item.Result = skipped;
                    results.Add(skipped);
                    continue;
                }

                var prefix = $"File {i + 1} of {files.Count}, {item.DisplayName}: ";
                item.Status = "Scanning…";
                Status = prefix + "starting…";
                var progress = new Progress<string>(text => Status = prefix + text);

                DocumentScanResult result;
                try
                {
                    result = await ScanOneAsync(item, profile, terms, checkHidden, progress);
                }
                catch (Exception ex)
                {
                    result = ComplianceScanner.Skipped(item.FilePath, FirstLine(ex.Message), item.DisplayName);
                }

                item.Result = result;
                results.Add(result);
                if (item == Selected) RefreshFindings();
            }

            _report = new ScanReport(profile, results, DateTimeOffset.Now, AppInfo.Version, terms.Length, stopped, checkHidden);
            RefreshFindings();

            var skippedCount = _report.FilesSkipped > 0 ? $" {_report.FilesSkipped} file(s) were not scanned." : "";
            var hiddenCount = checkHidden ? $", {_report.TotalHiddenItems} hidden-content item(s)" : "";
            Status = (stopped ? "Stopped: " : "Done: ") +
                     $"{_report.FilesScanned} file(s) scanned, {_report.TotalFindings} item(s) to review{hiddenCount}.{skippedCount} " +
                     "Save the report to keep a record.";
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task<DocumentScanResult> ScanOneAsync(
        ScanFileItem item, ComplianceProfile profile, IReadOnlyList<string> terms, bool checkHidden, IProgress<string> progress)
    {
        var path = item.FilePath;
        if (!File.Exists(path)) return ComplianceScanner.Skipped(path, "the file is no longer there", item.DisplayName);
        if (InputFile.IsEncryptedDocx(path))
            return ComplianceScanner.Skipped(path, "it is password-protected (remove the password first)", item.DisplayName);

        // Fingerprint the file as it was before it is read, so the report ties to this exact version.
        var sha = await Task.Run(() => FileHasher.Sha256(path));

        // What's stored in the file besides what its pages show: looked at directly, not through the page reader.
        HiddenInspection? hidden = checkHidden ? await Task.Run(() => HiddenContentInspector.Inspect(path)) : null;

        // A password-protected PDF: ask, and ask again if it was wrong, until it opens or the person gives up.
        string? password = null;
        List<ScannedPage> pages;
        while (true)
        {
            try
            {
                pages = await _service.ScanAsync(path, progress, MaxPages, password);
                break;
            }
            catch (PdfPasswordException ex)
            {
                var entered = AskPassword?.Invoke(item.Name, ex.WrongPassword);
                if (entered == null) return ComplianceScanner.Skipped(path, "it needs a password", item.DisplayName);
                password = entered;
            }
        }

        // Only the findings are kept; the page pictures go out of scope here.
        return ComplianceScanner.ScanPages(path, sha, pages, profile, terms, hidden, item.DisplayName);
    }

    // ------------------------------------------------------------------
    // Clean copies
    // ------------------------------------------------------------------
    private CleanOptions BuildCleanOptions() => new()
    {
        DocumentProperties = CleanProperties,
        Comments = CleanComments,
        TrackedChanges = CleanTrackedChanges,
        HiddenText = CleanHiddenText,
        Attachments = CleanAttachments,
        PreviewImages = CleanPreviews,
        PictureMetadata = CleanPictures,
    };

    private async Task CleanAsync()
    {
        if (IsBusy) return;

        var targets = Items.Where(i => i.Result is { Outcome: ScanOutcome.Scanned } r && r.HiddenItems.Count > 0).ToList();
        if (targets.Count == 0)
        {
            ShowMessage?.Invoke("No hidden content was found in the scanned files, so there is nothing to clean.");
            return;
        }

        var folder = PickFolder?.Invoke("Choose a folder for the clean copies");
        if (string.IsNullOrWhiteSpace(folder)) return;

        var options = BuildCleanOptions();
        int pdfs = targets.Count(t => Path.GetExtension(t.FilePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase));
        var question = new StringBuilder($"Make clean copies of {targets.Count} file(s) in:\n{folder}\n\nYour originals are not changed.");
        if (options.TrackedChanges) question.Append("\n\nIn Word files, all tracked changes are accepted: deleted text is removed for good.");
        if (options.HiddenText) question.Append(options.TrackedChanges ? "\nText formatted as hidden is deleted." : "\n\nText formatted as hidden is deleted from Word files.");
        if (pdfs > 0)
            question.Append($"\n\n{pdfs} PDF(s) are rebuilt from pictures of their pages, which removes everything hidden in them. This can take a while." +
                            (KeepPdfSearchable ? " Their text stays searchable." : " They will not be searchable."));
        question.Append("\n\nContinue?");
        if (Confirm?.Invoke(question.ToString()) != true) return;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var log = new List<CleanupLogEntry>();
        int made = 0, verifiedClean = 0;
        var notClean = new List<string>();
        var problems = new List<string>();

        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var item = targets[i];
                if (token.IsCancellationRequested)
                {
                    log.Add(new CleanupLogEntry(item.FilePath, null, null, null, Array.Empty<string>(), Array.Empty<string>(), "Not cleaned: stopped before this file"));
                    problems.Add($"{item.DisplayName}: stopped before this file");
                    continue;
                }

                var prefix = $"Cleaning {i + 1} of {targets.Count}, {item.DisplayName}: ";
                Status = prefix + "starting…";
                var progress = new Progress<string>(text => Status = prefix + text);

                try
                {
                    var isPdf = Path.GetExtension(item.FilePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
                    var destination = HiddenContentCleaner.CleanCopyPath(folder, item.DisplayName, isPdf ? ".pdf" : null);
                    var sourceSha = await Task.Run(() => FileHasher.Sha256(item.FilePath));

                    IReadOnlyList<string> removed;
                    if (isPdf)
                    {
                        await RebuildPdfAsync(item, destination, progress);
                        removed = new[] { "Rebuilt every page as a picture: metadata, comments, attachments, scripts, layers and earlier versions are not in the copy" };
                    }
                    else
                    {
                        removed = (await Task.Run(() => HiddenContentCleaner.CleanFile(item.FilePath, destination, options))).Removed;
                    }

                    // Look at the copy again, the same way the original was looked at.
                    var after = await Task.Run(() => HiddenContentInspector.Inspect(destination));
                    var stillThere = after.Items.Select(x => x.What).ToList();
                    if (!after.WasChecked) stillThere.Add("the copy could not be re-checked (" + after.Note + ")");
                    var outputSha = await Task.Run(() => FileHasher.Sha256(destination));

                    made++;
                    if (stillThere.Count == 0) verifiedClean++;
                    else notClean.Add($"{item.DisplayName}: {string.Join("; ", stillThere.Take(3))}{(stillThere.Count > 3 ? $" and {stillThere.Count - 3} more" : "")}");

                    log.Add(new CleanupLogEntry(item.FilePath, destination, sourceSha, outputSha, removed, stillThere, "Clean copy made"));
                }
                catch (Exception ex)
                {
                    var reason = FirstLine(ex.Message);
                    problems.Add($"{item.DisplayName}: {reason}");
                    log.Add(new CleanupLogEntry(item.FilePath, null, null, null, Array.Empty<string>(), Array.Empty<string>(), "Not cleaned: " + reason));
                }
            }

            // The record of this run goes in the same folder as the copies.
            string logNote;
            try
            {
                var logPath = UniquePath(Path.Combine(folder, "Cleanup log.csv"));
                await File.WriteAllTextAsync(logPath, CleanupLogWriter.ToCsv(log, DateTimeOffset.Now, AppInfo.Version), new UTF8Encoding(false));
                logNote = $"\n\nA record of this run was saved as {Path.GetFileName(logPath)} in the same folder.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logNote = "\n\nThe record of this run could not be saved: " + ex.Message;
            }

            var message = new StringBuilder($"Made {made} clean cop{(made == 1 ? "y" : "ies")} in {folder}.");
            if (made > 0)
                message.Append($"\n\nThe copies were checked again: {verifiedClean} show no hidden content" +
                               (notClean.Count > 0 ? $", and {notClean.Count} still show something:\n" + string.Join("\n", notClean.Take(8)) : "."));
            if (problems.Count > 0)
                message.Append($"\n\n{problems.Count} file(s) could not be cleaned:\n" + string.Join("\n", problems.Take(8)));
            message.Append(logNote);
            message.Append("\n\nThis removes what the check can find. Look over the copies before you share them.");

            Status = $"Made {made} clean cop{(made == 1 ? "y" : "ies")}.";
            ShowMessage?.Invoke(message.ToString());
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Rebuilds a PDF from pictures of its pages (nothing is blacked out), the same way the Redact tab saves its copies.</summary>
    private async Task RebuildPdfAsync(ScanFileItem item, string destination, IProgress<string> progress)
    {
        string? password = null;
        List<ScannedPage> pages;
        while (true)
        {
            try
            {
                pages = await _service.ScanAsync(item.FilePath, progress, MaxPages, password);
                break;
            }
            catch (PdfPasswordException ex)
            {
                var entered = AskPassword?.Invoke(item.Name, ex.WrongPassword);
                if (entered == null) throw new InvalidOperationException("it needs a password");
                password = entered;
            }
        }

        var images = new List<PdfPageImage>();
        var nothingCovered = new List<RedactionRegion>();
        for (int i = 0; i < pages.Count; i++)
        {
            progress.Report($"rebuilding page {i + 1} of {pages.Count}…");
            await Task.Yield();
            var page = pages[i];
            var jpeg = _service.BurnToJpeg(page, nothingCovered);
            var layer = KeepPdfSearchable ? OcrTextLayer.Build(page, nothingCovered) : null;
            images.Add(new PdfPageImage(jpeg, page.PixelWidth, page.PixelHeight, page.WidthPoints, page.HeightPoints, 3, layer));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        try
        {
            await Task.Run(() =>
            {
                using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
                ImagePdfWriter.Write(file, images);
            });
        }
        catch
        {
            try { if (File.Exists(destination)) File.Delete(destination); } catch (IOException) { }
            throw;
        }
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

    // ------------------------------------------------------------------
    // Saving the report
    // ------------------------------------------------------------------
    private void SaveReport()
    {
        if (_report == null) return;

        var chosen = PickSavePath?.Invoke("Compliance check report.html");
        if (string.IsNullOrWhiteSpace(chosen)) return;

        try
        {
            bool csv = chosen.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
            var text = csv ? ScanReportWriter.ToCsv(_report) : ScanReportWriter.ToHtml(_report);
            File.WriteAllText(chosen, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Status = $"Saved {Path.GetFileName(chosen)}.";
            ShowMessage?.Invoke(
                $"Saved {chosen}\n\nFound values are partly hidden in the report, but it still lists your file names and where things were found, " +
                "so keep it as confidential as the documents themselves.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage?.Invoke($"Couldn't save the report: {ex.Message}");
        }
    }

    private static string FirstLine(string message)
    {
        var line = message.Split('\n')[0].Trim();
        return line.Length <= 140 ? line : line[..140] + "…";
    }
}
