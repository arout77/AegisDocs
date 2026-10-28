using System.Windows.Input;
using ReceiptCapture.Core;
using ReceiptCapture.Core.Integrity;

namespace ReceiptCapture.ViewModels;

/// <summary>Behaviour of the Integrity tab: make a fingerprint list of a folder, and later check the folder against it.</summary>
public sealed class IntegrityViewModel : ObservableObject
{
    private const string ReportFilter = "Web page (opens in any browser)|*.html|Spreadsheet (CSV)|*.csv";
    private const string ManifestFilter = "Integrity manifest (*.json)|*.json";

    private bool _isBusy;
    private string _status = "Make a fingerprint list of a folder now, and check the folder against it later.";
    private CancellationTokenSource? _cts;

    // ---- make ----
    private string? _createFolder;
    private string _matter = "";
    private string _preparedBy = "";
    private string _notes = "";
    private IntegrityManifest? _created;
    private string? _createdSavedAt;

    // ---- check ----
    private string? _manifestPath;
    private IntegrityManifest? _loaded;
    private string? _checkFolder;
    private VerifyResult? _result;
    private DateTimeOffset _checkedAt;

    public IntegrityViewModel()
    {
        StopCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy && _cts != null);

        ChooseCreateFolderCommand = new RelayCommand(() => SetCreateFolder(PickFolder?.Invoke("Choose the folder to fingerprint")), () => !IsBusy);
        CreateCommand = new RelayCommand(async () => await CreateAsync(), () => !IsBusy && CreateFolder != null);
        SaveManifestCommand = new RelayCommand(() => SaveManifest(), () => Created != null && !IsBusy);
        SaveCreatedReportCommand = new RelayCommand(SaveCreatedReport, () => Created != null && !IsBusy);
        CopyFingerprintCommand = new RelayCommand(() => { if (Created != null) CopyToClipboard?.Invoke(Created.ComputeFingerprint()); }, () => Created != null);

        ChooseManifestCommand = new RelayCommand(() => LoadManifest(PickManifest?.Invoke()), () => !IsBusy);
        ChooseCheckFolderCommand = new RelayCommand(() => SetCheckFolder(PickFolder?.Invoke("Choose the folder to check")), () => !IsBusy);
        CheckCommand = new RelayCommand(async () => await CheckAsync(), () => !IsBusy && Loaded != null && CheckFolder != null);
        SaveCheckReportCommand = new RelayCommand(SaveCheckReport, () => Result != null && !IsBusy);
        RecordCheckCommand = new RelayCommand(RecordCheck, () => Result != null && Loaded != null && !IsBusy);
    }

    // Hooks the view fills in
    public Action<string>? ShowMessage { get; set; }
    public Func<string, bool>? Confirm { get; set; }
    /// <summary>Shows a folder picker with the given title.</summary>
    public Func<string, string?>? PickFolder { get; set; }
    public Func<string?>? PickManifest { get; set; }
    /// <summary>(title, suggested file name, filter) -> chosen path, or null.</summary>
    public Func<string, string, string, string?>? PickSavePath { get; set; }
    public Action<string>? CopyToClipboard { get; set; }

    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public ICommand StopCommand { get; }

    private static string Who => $"{Environment.UserName}@{Environment.MachineName}";
    private static string Tool => "Aegis Docs " + AppInfo.Version;

    // ------------------------------------------------------------------
    // Make a manifest
    // ------------------------------------------------------------------
    public string? CreateFolder { get => _createFolder; private set { if (SetProperty(ref _createFolder, value)) OnPropertyChanged(nameof(CreateFolderText)); } }
    public string CreateFolderText => CreateFolder ?? "No folder chosen yet";
    public string Matter { get => _matter; set => SetProperty(ref _matter, value ?? ""); }
    public string PreparedBy { get => _preparedBy; set => SetProperty(ref _preparedBy, value ?? ""); }
    public string Notes { get => _notes; set => SetProperty(ref _notes, value ?? ""); }

    public IntegrityManifest? Created
    {
        get => _created;
        private set
        {
            if (!SetProperty(ref _created, value)) return;
            OnPropertyChanged(nameof(HasCreated));
            OnPropertyChanged(nameof(CreatedSummary));
            OnPropertyChanged(nameof(CreatedFingerprint));
        }
    }
    public bool HasCreated => Created != null;
    public string CreatedFingerprint => Created?.ComputeFingerprint() ?? "";
    public string CreatedSummary => Created == null ? "" :
        $"{Created.FileCount:N0} file(s), {ByteSize.Format(Created.TotalBytes)} fingerprinted." +
        (Created.Entries.Any(e => e.Error != null) ? $" {Created.Entries.Count(e => e.Error != null)} could not be read." : "") +
        (_createdSavedAt != null ? $" Saved as {Path.GetFileName(_createdSavedAt)}." : " Not saved yet: press Save manifest…");

    public ICommand ChooseCreateFolderCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand SaveManifestCommand { get; }
    public ICommand SaveCreatedReportCommand { get; }
    public ICommand CopyFingerprintCommand { get; }

    private void SetCreateFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        CreateFolder = folder;
        Created = null;
        _createdSavedAt = null;
        Status = "Folder chosen. Add a matter reference if you like, then press Make manifest.";
    }

    private async Task CreateAsync()
    {
        if (CreateFolder == null) return;
        var folder = CreateFolder;
        var matter = Matter.Trim();
        var preparedBy = PreparedBy.Trim();
        var notes = Notes.Trim();

        var progress = new Progress<IntegrityProgress>(p =>
            Status = p.Total == 0 ? "Reading the folder…" : $"Fingerprinting file {Math.Min(p.Done + 1, p.Total)} of {p.Total}…");

        _cts = new CancellationTokenSource();
        IsBusy = true;
        Created = null;
        _createdSavedAt = null;
        try
        {
            var token = _cts.Token;
            Status = "Reading the folder…";
            var manifest = await Task.Run(() =>
                IntegrityChecker.Create(folder, Tool, Who, DateTimeOffset.Now, matter: matter, preparedBy: preparedBy, notes: notes, progress: progress, cancel: token));
            Created = manifest;
            Status = "Done. Save the manifest, and keep the list fingerprint somewhere independent.";
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped. No manifest was made.";
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = "The manifest could not be made.";
            ShowMessage?.Invoke(ex.Message);
            return;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
        }

        SaveManifest();      // the next thing anyone wants is to keep it
    }

    private bool SaveManifest()
    {
        if (Created == null) return false;
        var folderName = string.IsNullOrWhiteSpace(Created.FolderName) ? "folder" : Created.FolderName;
        var path = PickSavePath?.Invoke("Save the manifest", $"{folderName} integrity manifest.json", ManifestFilter);
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            Created.Save(path);
            _createdSavedAt = path;
            OnPropertyChanged(nameof(CreatedSummary));
            Status = $"Saved {Path.GetFileName(path)}.";

            bool insideFolder = CreateFolder != null && IsInside(path, CreateFolder);
            ShowMessage?.Invoke(
                $"Saved {path}\n\nTo make this list worth more later, send the list fingerprint (Copy fingerprint) to someone independent now, for example in an email. " +
                "It fixes the date the list was made." +
                (insideFolder ? "\n\nYou saved it inside the folder it describes. That's fine: checks ignore the manifest file itself." : ""));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage?.Invoke($"Couldn't save the manifest: {ex.Message}");
            return false;
        }
    }

    private void SaveCreatedReport()
    {
        if (Created == null) return;
        var path = PickSavePath?.Invoke("Save the report", $"{Created.FolderName} integrity manifest.html", ReportFilter);
        if (string.IsNullOrWhiteSpace(path)) return;

        WriteText(path, path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            ? IntegrityReport.ManifestCsv(Created)
            : IntegrityReport.ManifestHtml(Created));
    }

    // ------------------------------------------------------------------
    // Check a folder
    // ------------------------------------------------------------------
    public string ManifestText => _manifestPath == null ? "No manifest chosen yet" : Path.GetFileName(_manifestPath);
    public string ManifestDetails => Loaded == null ? "" :
        $"Made {Loaded.CreatedAt:MMMM d, yyyy 'at' h:mm tt} by {Loaded.CreatedBy}. {Loaded.FileCount:N0} file(s), folder \"{Loaded.FolderName}\"." +
        (string.IsNullOrWhiteSpace(Loaded.Matter) ? "" : $" Matter: {Loaded.Matter}.") +
        (Loaded.FingerprintIsIntact ? "" : " WARNING: this list has been edited since it was saved.");
    public string CheckFolderText => CheckFolder ?? "No folder chosen yet";

    public IntegrityManifest? Loaded
    {
        get => _loaded;
        private set
        {
            if (!SetProperty(ref _loaded, value)) return;
            OnPropertyChanged(nameof(ManifestDetails));
            OnPropertyChanged(nameof(ManifestText));
        }
    }

    public string? CheckFolder { get => _checkFolder; private set { if (SetProperty(ref _checkFolder, value)) OnPropertyChanged(nameof(CheckFolderText)); } }

    public VerifyResult? Result
    {
        get => _result;
        private set
        {
            if (!SetProperty(ref _result, value)) return;
            OnPropertyChanged(nameof(HasChecked));
            OnPropertyChanged(nameof(CheckSummary));
            OnPropertyChanged(nameof(CheckPassed));
        }
    }
    public bool HasChecked => Result != null;
    public bool CheckPassed => Result?.AllGood == true;

    public string CheckSummary
    {
        get
        {
            if (Result == null) return "";
            if (Result.AllGood) return $"Everything matches. All {Result.Matching:N0} file(s) are unchanged and nothing has been added.";

            var parts = new List<string>();
            void Add(VerifyStatus s, string label) { int n = Result.Count(s); if (n > 0) parts.Add($"{n:N0} {label}"); }
            Add(VerifyStatus.Changed, "changed");
            Add(VerifyStatus.Missing, "missing");
            Add(VerifyStatus.Added, "new");
            Add(VerifyStatus.Unreadable, "couldn't be read");
            return $"Differences found: {string.Join(", ", parts)}. {Result.Matching:N0} file(s) are unchanged.";
        }
    }

    public ICommand ChooseManifestCommand { get; }
    public ICommand ChooseCheckFolderCommand { get; }
    public ICommand CheckCommand { get; }
    public ICommand SaveCheckReportCommand { get; }
    public ICommand RecordCheckCommand { get; }

    private void LoadManifest(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var manifest = IntegrityManifest.Load(path);
            _manifestPath = path;
            Loaded = manifest;
            Result = null;
            Status = CheckFolder == null ? "Manifest loaded. Now choose the folder to check." : "Manifest loaded. Press Check.";
            if (!manifest.FingerprintIsIntact)
                ShowMessage?.Invoke("This manifest has been edited since it was saved: its fingerprint no longer matches the list of files inside it. " +
                                    "Don't rely on it. If you have the original, use that.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ShowMessage?.Invoke(ex.Message);
        }
    }

    private void SetCheckFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        CheckFolder = folder;
        Result = null;
        Status = Loaded == null ? "Folder chosen. Now choose the manifest." : "Folder chosen. Press Check.";
    }

    private async Task CheckAsync()
    {
        if (Loaded == null || CheckFolder == null) return;
        var manifest = Loaded;
        var folder = CheckFolder;
        var exclude = _manifestPath == null ? Array.Empty<string>() : new[] { _manifestPath };

        var progress = new Progress<IntegrityProgress>(p =>
            Status = p.Total == 0 ? "Checking…" : $"Checking file {Math.Min(p.Done + 1, p.Total)} of {p.Total}…");

        _cts = new CancellationTokenSource();
        IsBusy = true;
        Result = null;
        try
        {
            var token = _cts.Token;
            Status = "Checking…";
            _checkedAt = DateTimeOffset.Now;
            Result = await Task.Run(() => IntegrityChecker.Verify(manifest, folder, exclude, progress, token));
            Status = Result.AllGood ? "Done. Everything matches." : "Done. Differences were found.";
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped. The check was not finished.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "The check could not be completed.";
            ShowMessage?.Invoke(ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    private void SaveCheckReport()
    {
        if (Result == null || Loaded == null || CheckFolder == null) return;
        var path = PickSavePath?.Invoke("Save the report", "Integrity check.html", ReportFilter);
        if (string.IsNullOrWhiteSpace(path)) return;

        WriteText(path, path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            ? IntegrityReport.VerifyCsv(Result)
            : IntegrityReport.VerifyHtml(Loaded, Result, CheckFolder, _checkedAt, Who, Tool));
    }

    private void RecordCheck()
    {
        if (Result == null || Loaded == null || _manifestPath == null) return;

        if (Confirm?.Invoke(
                "Add a line about this check to the manifest's custody log and save the manifest file again?\n\n" +
                "Only the log changes. The list of files and the list fingerprint stay exactly as they were.") != true)
            return;

        try
        {
            Loaded.AddEvent(_checkedAt, Who, Result.AllGood ? "Checked: all matched" : "Checked: differences found", CheckSummary);
            Loaded.Save(_manifestPath);
            Status = "Recorded in the manifest's custody log.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage?.Invoke($"Couldn't update the manifest: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Files dropped onto the tab
    // ------------------------------------------------------------------
    public void AddPaths(IEnumerable<string> paths, bool checkMode)
    {
        if (IsBusy) return;
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                if (checkMode) SetCheckFolder(path); else SetCreateFolder(path);
            }
            else if (checkMode && File.Exists(path) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                LoadManifest(path);
            }
        }
    }

    // ------------------------------------------------------------------
    private void WriteText(string path, string text)
    {
        try
        {
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
            Status = $"Saved {Path.GetFileName(path)}.";
            ShowMessage?.Invoke($"Saved {path}\n\nThe report lists your file names, so keep it as confidential as the folder itself.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage?.Invoke($"Couldn't save the report: {ex.Message}");
        }
    }

    private static bool IsInside(string file, string folder)
    {
        var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(file).StartsWith(full, StringComparison.OrdinalIgnoreCase);
    }
}
