using System.Diagnostics;
using System.Text;
using System.Windows.Input;
using ReceiptCapture.Core;
using ReceiptCapture.Core.Comparison;
using ReceiptCapture.Core.Pdf;
using ReceiptCapture.Core.Redaction;
using ReceiptCapture.Core.Storage;
using ReceiptCapture.Services;

namespace ReceiptCapture.ViewModels;

/// <summary>Behaviour of the Compare tab: two documents in, a marked-up list of what changed out.</summary>
public sealed class CompareViewModel : ObservableObject
{
    private static readonly string[] Supported = { ".pdf", ".docx", ".txt" };

    private readonly IRedactionService _service;

    private string? _originalPath;
    private string? _revisedPath;
    private bool _isBusy;
    private bool _changesOnly = true;
    private bool _ignoreCase;
    private string _status = "Choose the original document and the revised one, then press Compare.";
    private string _summary = "";

    // The last comparison, kept so the report can be written in either layout without redoing the work.
    private List<TextToken>? _tokensOriginal;
    private List<TextToken>? _tokensRevised;
    private DiffResult? _diff;
    private ComparisonInfo? _info;
    private string? _openedReport;

    public CompareViewModel(IRedactionService service)
    {
        _service = service;

        ChooseOriginalCommand = new RelayCommand(() => Choose(isOriginal: true), () => !IsBusy);
        ChooseRevisedCommand = new RelayCommand(() => Choose(isOriginal: false), () => !IsBusy);
        SwapCommand = new RelayCommand(Swap, () => !IsBusy && (_originalPath != null || _revisedPath != null));
        ClearCommand = new RelayCommand(Clear, () => !IsBusy && (_originalPath != null || _revisedPath != null));
        CompareCommand = new RelayCommand(async () => await CompareAsync(), () => !IsBusy && _originalPath != null && _revisedPath != null);
        OpenReportCommand = new RelayCommand(OpenReport, () => HasResult && !IsBusy);
        SaveReportCommand = new RelayCommand(SaveReport, () => HasResult && !IsBusy);
    }

    // Hooks the view fills in
    public Action<string>? ShowMessage { get; set; }
    public Func<string?>? PickFile { get; set; }
    public Func<string, string?>? PickSavePath { get; set; }

    /// <summary>Asks for a PDF's password: (file name, last try was wrong) -> password, or null if cancelled.</summary>
    public Func<string, bool, string?>? AskPassword { get; set; }

    public string OriginalText => _originalPath == null ? "Drop or choose the original" : Path.GetFileName(_originalPath);
    public string RevisedText => _revisedPath == null ? "Drop or choose the revised version" : Path.GetFileName(_revisedPath);
    public string? OriginalFolder => _originalPath == null ? null : Path.GetDirectoryName(_originalPath);
    public string? RevisedFolder => _revisedPath == null ? null : Path.GetDirectoryName(_revisedPath);

    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public bool HasResult => _diff != null;

    /// <summary>Show only the paragraphs that changed (with a little around them) instead of the whole text.</summary>
    public bool ChangesOnly { get => _changesOnly; set => SetProperty(ref _changesOnly, value); }

    /// <summary>Treat "Fee" and "fee" as the same word.</summary>
    public bool IgnoreCase { get => _ignoreCase; set => SetProperty(ref _ignoreCase, value); }

    public ICommand ChooseOriginalCommand { get; }
    public ICommand ChooseRevisedCommand { get; }
    public ICommand SwapCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand CompareCommand { get; }
    public ICommand OpenReportCommand { get; }
    public ICommand SaveReportCommand { get; }

    // ------------------------------------------------------------------
    // Choosing the two files
    // ------------------------------------------------------------------
    private void Choose(bool isOriginal)
    {
        var path = PickFile?.Invoke();
        if (!string.IsNullOrWhiteSpace(path)) SetFile(path, isOriginal);
    }

    /// <summary>Files dropped on the tab: one fills the original first and then the revised, two fill both (first is the original).</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        if (IsBusy) return;
        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0) return;

        if (files.Count >= 2)
        {
            SetFile(files[0], isOriginal: true);
            SetFile(files[1], isOriginal: false);
        }
        else SetFile(files[0], isOriginal: _originalPath == null);   // fills the original first, after that the revised
    }

    private void SetFile(string path, bool isOriginal)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!Supported.Contains(ext))
        {
            ShowMessage?.Invoke(
                ext is ".doc" or ".rtf"
                    ? $"{Path.GetFileName(path)} is an older Word format. Open it in Word, save a copy as .docx (or PDF), and use that copy."
                    : $"{Path.GetFileName(path)} can't be compared. Choose a PDF, a Word document (.docx) or a text file (.txt).");
            return;
        }

        if (isOriginal) _originalPath = path; else _revisedPath = path;
        ForgetResult();
        RaiseFiles();
        Status = _originalPath != null && _revisedPath != null
            ? "Ready. Press Compare."
            : "Now choose the other document.";
    }

    private void Swap()
    {
        (_originalPath, _revisedPath) = (_revisedPath, _originalPath);
        ForgetResult();
        RaiseFiles();
    }

    private void Clear()
    {
        _originalPath = null;
        _revisedPath = null;
        ForgetResult();
        RaiseFiles();
        Status = "Choose the original document and the revised one, then press Compare.";
    }

    private void ForgetResult()
    {
        _tokensOriginal = _tokensRevised = null;
        _diff = null;
        _info = null;
        Summary = "";
        OnPropertyChanged(nameof(HasResult));
    }

    private void RaiseFiles()
    {
        OnPropertyChanged(nameof(OriginalText));
        OnPropertyChanged(nameof(RevisedText));
        OnPropertyChanged(nameof(OriginalFolder));
        OnPropertyChanged(nameof(RevisedFolder));
    }

    // ------------------------------------------------------------------
    // Comparing
    // ------------------------------------------------------------------
    private async Task CompareAsync()
    {
        if (_originalPath == null || _revisedPath == null) return;
        var originalPath = _originalPath;
        var revisedPath = _revisedPath;

        IsBusy = true;
        ForgetResult();
        try
        {
            var progress = new Progress<string>(text => Status = text);

            Status = $"Reading {Path.GetFileName(originalPath)}…";
            var original = await LoadAsync(originalPath, progress);
            if (original == null) { Status = "Cancelled."; return; }

            Status = $"Reading {Path.GetFileName(revisedPath)}…";
            var revised = await LoadAsync(revisedPath, progress);
            if (revised == null) { Status = "Cancelled."; return; }

            if (original.Count == 0 || revised.Count == 0)
            {
                var which = original.Count == 0 ? Path.GetFileName(originalPath) : Path.GetFileName(revisedPath);
                Status = "Nothing to compare.";
                ShowMessage?.Invoke($"No text was found in {which}. It may be blank, or a scan the reader couldn't make out.");
                return;
            }

            Status = "Comparing…";
            bool ignoreCase = IgnoreCase;
            var diff = await Task.Run(() => TextDiff.Compare(
                original.Select(t => DocumentText.Key(t.Text, ignoreCase)).ToList(),
                revised.Select(t => DocumentText.Key(t.Text, ignoreCase)).ToList()));

            var (hashOriginal, hashRevised) = await Task.Run(() => (FileHasher.Sha256(originalPath), FileHasher.Sha256(revisedPath)));

            var notes = new List<string>();
            bool pdfOriginal = Path.GetExtension(originalPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
            bool pdfRevised = Path.GetExtension(revisedPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
            if (pdfOriginal || pdfRevised)
                notes.Add("PDF text was read from the page images with the computer's text recognition (OCR), so a few differences may be reading mistakes rather than real changes." +
                          (pdfOriginal != pdfRevised ? " Comparing a PDF with a Word file is the least reliable pairing: line breaks, headers and page numbers can show up as changes." : ""));
            if (ignoreCase) notes.Add("Capital and small letters were treated as the same.");

            _tokensOriginal = original;
            _tokensRevised = revised;
            _diff = diff;
            _info = new ComparisonInfo(
                Path.GetFileName(originalPath), hashOriginal, new FileInfo(originalPath).Length,
                Path.GetFileName(revisedPath), hashRevised, new FileInfo(revisedPath).Length,
                DateTimeOffset.Now, AppInfo.Version, notes);

            var summary = ComparisonReport.Summarize(original, revised, diff);
            Summary = summary.Identical
                ? "No differences. The two documents read exactly the same."
                : $"{summary.WordsRemoved:N0} word(s) removed, {summary.WordsAdded:N0} added, in {summary.ParagraphsChanged:N0} paragraph(s). " +
                  $"The texts are about {summary.SimilarityPercent:0}% alike." +
                  (summary.TooDifferent ? " They are very different, so the marking is coarse." : "");
            Status = "Done. Open the report to see every change.";
            OnPropertyChanged(nameof(HasResult));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Status = "The comparison stopped because of a problem.";
            ShowMessage?.Invoke(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The words of one file, or null if the person cancelled at a password prompt.</summary>
    private async Task<List<TextToken>?> LoadAsync(string path, IProgress<string> progress)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var name = Path.GetFileName(path);

        if (ext == ".docx")
        {
            if (InputFile.IsEncryptedDocx(path))
                throw new InvalidOperationException($"{name} is password-protected. In Word, remove the password (File > Info > Protect Document), save a copy, and compare that copy.");
            return await Task.Run(() => DocumentText.FromParagraphs(DocxText.ReadParagraphs(path)));
        }

        if (ext == ".txt")
            return await Task.Run(() => DocumentText.FromParagraphs(File.ReadAllLines(path)));

        // A PDF. Ask for the password, and ask again if it was wrong, until it opens or the person gives up.
        string? password = null;
        while (true)
        {
            try
            {
                var inner = new Progress<string>(text => progress.Report($"{name}: {text}"));
                var pages = await _service.ReadPageTextsAsync(path, inner, password);
                return DocumentText.FromPages(pages);
            }
            catch (PdfPasswordException ex)
            {
                var entered = AskPassword?.Invoke(name, ex.WrongPassword);
                if (string.IsNullOrEmpty(entered)) return null;
                password = entered;
            }
        }
    }

    // ------------------------------------------------------------------
    // The report
    // ------------------------------------------------------------------
    private string? BuildHtml()
        => _info == null || _tokensOriginal == null || _tokensRevised == null || _diff == null
            ? null
            : ComparisonReport.ToHtml(_info, _tokensOriginal, _tokensRevised, _diff, ChangesOnly);

    private void OpenReport()
    {
        var html = BuildHtml();
        if (html == null) return;

        try
        {
            Directory.CreateDirectory(AppPaths.TempDir);
            // One scratch file per run of the program; the program empties this folder when it starts and exits.
            _openedReport ??= Path.Combine(AppPaths.TempDir, "comparison-" + Guid.NewGuid().ToString("N")[..8] + ".html");
            File.WriteAllText(_openedReport, html, new UTF8Encoding(false));
            Process.Start(new ProcessStartInfo(_openedReport) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowMessage?.Invoke("Couldn't open the report: " + ex.Message + "\n\nUse Save report… instead and open the saved file.");
        }
    }

    private void SaveReport()
    {
        var html = BuildHtml();
        if (html == null) return;

        var chosen = PickSavePath?.Invoke("Document comparison.html");
        if (string.IsNullOrWhiteSpace(chosen)) return;

        try
        {
            File.WriteAllText(chosen, html, new UTF8Encoding(false));
            Status = $"Saved {Path.GetFileName(chosen)}.";
            ShowMessage?.Invoke($"Saved {chosen}\n\nThe report contains the full text of your documents, so keep it as confidential as they are.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage?.Invoke($"Couldn't save the report: {ex.Message}");
        }
    }
}
