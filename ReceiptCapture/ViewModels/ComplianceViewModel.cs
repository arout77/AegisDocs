using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using System.Windows.Input;
using ReceiptCapture.Core;
using ReceiptCapture.Core.Compliance;
using ReceiptCapture.Core.Pdf;
using ReceiptCapture.Core.Redaction;
using ReceiptCapture.Core.Storage;
using ReceiptCapture.Services;

namespace ReceiptCapture.ViewModels;

/// <summary>One file in the Compliance tab's list, with what the scan made of it.</summary>
public sealed class ScanFileItem : ObservableObject
{
    private DocumentScanResult? _result;
    private string _status = "Not scanned yet";

    public ScanFileItem(string filePath) => FilePath = filePath;

    public string FilePath { get; }
    public string Name => Path.GetFileName(FilePath);

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
/// The Compliance tab: pick what to check against (HIPAA, card data, ...), scan files, read what was found
/// (values are partly hidden), and save a report. Files are read on this PC with the same offline reader the
/// Redact tab uses, and the page images are dropped as soon as each file has been scanned.
/// </summary>
public sealed class ComplianceViewModel : ObservableObject
{
    private const int MaxPages = 150;

    private readonly IRedactionService _service;

    private ComplianceProfile _profile = ComplianceProfiles.Hipaa;
    private ScanFileItem? _selected;
    private bool _isBusy;
    private string _status = "Add files, choose what to check them against, then press Scan files. Your originals are never changed.";
    private string _customTermsText = "";
    private ScanReport? _report;

    public ComplianceViewModel(IRedactionService service)
    {
        _service = service;

        Items.CollectionChanged += (_, _) =>
        {
            _report = null;                      // a report describes one run: a changed list needs a new run
            OnPropertyChanged(nameof(IsEmpty));
        };

        AddFilesCommand = new RelayCommand(() => AddFiles(PickFiles?.Invoke() ?? Array.Empty<string>()), () => !IsBusy);
        RemoveCommand = new RelayCommand(RemoveSelected, () => Selected != null && !IsBusy);
        ClearCommand = new RelayCommand(() => { Items.Clear(); Selected = null; }, () => Items.Count > 0 && !IsBusy);
        ScanCommand = new RelayCommand(async () => await ScanAsync(), () => Items.Count > 0 && !IsBusy);
        SaveReportCommand = new RelayCommand(SaveReport, () => _report != null && !IsBusy);
    }

    // ------------------------------------------------------------------
    // Hooks the view fills in
    // ------------------------------------------------------------------
    public Action<string>? ShowMessage { get; set; }
    public Func<string[]?>? PickFiles { get; set; }
    public Func<string, string?>? PickSavePath { get; set; }

    /// <summary>Asks for a PDF's password: (file name, last try was wrong) -> password, or null if cancelled.</summary>
    public Func<string, bool, string?>? AskPassword { get; set; }

    // ------------------------------------------------------------------
    // What the view shows
    // ------------------------------------------------------------------
    public ObservableCollection<ScanFileItem> Items { get; } = new();
    public ObservableCollection<Finding> Findings { get; } = new();
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
                ? $"{Selected.Name}: not scanned yet."
                : $"{Selected.Name}: {Selected.Result.StatusText.ToLowerInvariant()}";
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
    public ICommand RemoveCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand SaveReportCommand { get; }

    // ------------------------------------------------------------------
    // The list
    // ------------------------------------------------------------------
    /// <summary>Adds files (not folders) the scanner can read. Used by the Add button and by drag and drop.</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        if (IsBusy) return;

        int unsupported = 0, folders = 0;
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) { folders++; continue; }
            if (InputFile.Classify(path) == InputKind.Unsupported) { unsupported++; continue; }
            if (Items.Any(i => string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase))) continue;
            Items.Add(new ScanFileItem(path));
        }

        if (unsupported > 0 || folders > 0)
        {
            var parts = new List<string>();
            if (unsupported > 0) parts.Add($"{unsupported} file(s) weren't {InputFile.SupportedText}");
            if (folders > 0) parts.Add($"{folders} folder(s) were left out (add the files inside instead)");
            ShowMessage?.Invoke("Not added: " + string.Join("; ", parts) + ".");
        }

        if (Selected == null && Items.Count > 0) Selected = Items[0];
        if (Items.Count > 0) Status = $"{Items.Count} file(s) ready. Press Scan files.";
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
        if (Selected?.Result is { } result)
            foreach (var finding in result.Findings) Findings.Add(finding);

        OnPropertyChanged(nameof(FindingsHeader));
        OnPropertyChanged(nameof(FindingsNote));
    }

    // ------------------------------------------------------------------
    // Scanning
    // ------------------------------------------------------------------
    private async Task ScanAsync()
    {
        if (IsBusy || Items.Count == 0) return;

        IsBusy = true;
        var profile = _profile;
        var terms = CustomTermsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var files = Items.ToList();
        var results = new List<DocumentScanResult>();

        try
        {
            foreach (var item in files) { item.Result = null; item.Status = "Waiting…"; }
            RefreshFindings();

            for (int i = 0; i < files.Count; i++)
            {
                var item = files[i];
                var prefix = $"File {i + 1} of {files.Count}, {item.Name}: ";
                item.Status = "Scanning…";
                Status = prefix + "starting…";
                var progress = new Progress<string>(text => Status = prefix + text);

                DocumentScanResult result;
                try
                {
                    result = await ScanOneAsync(item.FilePath, profile, terms, progress);
                }
                catch (Exception ex)
                {
                    result = ComplianceScanner.Skipped(item.FilePath, FirstLine(ex.Message));
                }

                item.Result = result;
                results.Add(result);
                if (item == Selected) RefreshFindings();
            }

            _report = new ScanReport(profile, results, DateTimeOffset.Now, AppVersion(), terms.Length);
            RefreshFindings();

            var skipped = _report.FilesSkipped > 0 ? $" {_report.FilesSkipped} file(s) could not be scanned." : "";
            Status = $"Done: {_report.FilesScanned} file(s) scanned, {_report.TotalFindings} item(s) to review.{skipped} " +
                     "Save the report to keep a record.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<DocumentScanResult> ScanOneAsync(
        string path, ComplianceProfile profile, IReadOnlyList<string> terms, IProgress<string> progress)
    {
        if (!File.Exists(path)) return ComplianceScanner.Skipped(path, "the file is no longer there");
        if (InputFile.IsEncryptedDocx(path))
            return ComplianceScanner.Skipped(path, "it is password-protected (remove the password first)");

        // Fingerprint the file as it was before it is read, so the report ties to this exact version.
        var sha = await Task.Run(() => FileHasher.Sha256(path));

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
                var entered = AskPassword?.Invoke(Path.GetFileName(path), ex.WrongPassword);
                if (entered == null) return ComplianceScanner.Skipped(path, "it needs a password");
                password = entered;
            }
        }

        // Only the findings are kept; the page pictures go out of scope here.
        return ComplianceScanner.ScanPages(path, sha, pages, profile, terms);
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

    private static string AppVersion()
        => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0";

    private static string FirstLine(string message)
    {
        var line = message.Split('\n')[0].Trim();
        return line.Length <= 140 ? line : line[..140] + "…";
    }
}
