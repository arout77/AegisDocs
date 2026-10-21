using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using ReceiptCapture.Core;
using ReceiptCapture.Core.Documents;
using ReceiptCapture.Services;

namespace ReceiptCapture.ViewModels;

/// <summary>One file in the batch list.</summary>
public sealed class DocItem : ObservableObject
{
    private int? _pages;

    public DocItem(string filePath)
    {
        FilePath = filePath;
        try { SizeText = FormatSize(new FileInfo(filePath).Length); }
        catch (IOException) { SizeText = "?"; }
    }

    public string FilePath { get; }
    public string Name => Path.GetFileName(FilePath);
    public bool IsPdf => FilePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    public string SizeText { get; }

    public int? Pages
    {
        get => _pages;
        set { if (SetProperty(ref _pages, value)) OnPropertyChanged(nameof(PagesText)); }
    }

    public string PagesText => !IsPdf ? "n/a" : Pages?.ToString(CultureInfo.CurrentCulture) ?? "?";

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0} KB";
        return $"{bytes / 1024.0 / 1024.0:0.0} MB";
    }
}

/// <summary>Behaviour of the Documents tab: a batch list plus five tools that act on it.</summary>
public sealed class DocumentsViewModel : ObservableObject
{
    private DocItem? _selected;
    private bool _isBusy;
    private string _status = "Drop PDFs onto this tab, then pick a tool on the right. Your originals are never changed.";
    private string? _outputFolder;

    // tool options
    private string _mergeName = "Merged.pdf";
    private bool _splitByRanges = true;
    private string _splitRanges = "1-3, 5, 8-";
    private int _splitEveryN = 10;
    private string _batesPrefix = "";
    private string _batesSuffix = "";
    private int _batesDigits = 6;
    private long _batesStart = 1;
    private string _watermarkText = "CONFIDENTIAL";
    private string _renamePattern = "{n:000}_{name}";
    private long _renameStart = 1;
    private string _renamePreview = "";

    public DocumentsViewModel()
    {
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(OutputFolderText));
            UpdateRenamePreview();
        };

        AddFilesCommand = new RelayCommand(async () => await AddFilesFromDialogAsync(), () => !IsBusy);
        RemoveCommand = new RelayCommand(RemoveSelected, () => Selected != null);
        ClearCommand = new RelayCommand(() => Items.Clear(), () => Items.Count > 0 && !IsBusy);
        MoveUpCommand = new RelayCommand(() => Move(-1), () => Selected != null);
        MoveDownCommand = new RelayCommand(() => Move(+1), () => Selected != null);
        ChooseOutputCommand = new RelayCommand(ChooseOutput);
        OpenOutputCommand = new RelayCommand(OpenOutput);
        MergeCommand = new RelayCommand(async () => await MergeAsync(), () => !IsBusy);
        SplitCommand = new RelayCommand(async () => await SplitAsync(), () => !IsBusy);
        BatesCommand = new RelayCommand(async () => await BatesAsync(), () => !IsBusy);
        WatermarkCommand = new RelayCommand(async () => await WatermarkAsync(), () => !IsBusy);
        RenameCommand = new RelayCommand(async () => await RenameAsync(), () => !IsBusy);

        UpdateRenamePreview();
    }

    // ------------------------------------------------------------------
    // Hooks the view fills in
    // ------------------------------------------------------------------
    public Action<string>? ShowMessage { get; set; }
    public Func<string[]?>? PickFiles { get; set; }
    public Func<string?>? PickFolder { get; set; }

    // ------------------------------------------------------------------
    // List
    // ------------------------------------------------------------------
    public ObservableCollection<DocItem> Items { get; } = new();
    public bool IsEmpty => Items.Count == 0;

    public DocItem? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
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

    // ------------------------------------------------------------------
    // Output folder
    // ------------------------------------------------------------------
    /// <summary>Null means "a Processed folder next to the first file".</summary>
    public string OutputFolderText => _outputFolder ?? (Items.Count > 0
        ? Path.Combine(Path.GetDirectoryName(Items[0].FilePath) ?? "", "Processed")
        : "A \"Processed\" folder next to your files");

    private string ResolveOutputFolder() => _outputFolder
        ?? Path.Combine(Path.GetDirectoryName(Items[0].FilePath) ?? "", "Processed");

    private string? LastOutputFolder { get; set; }

    // ------------------------------------------------------------------
    // Tool options (bound to the text boxes on the right)
    // ------------------------------------------------------------------
    public string MergeName { get => _mergeName; set => SetProperty(ref _mergeName, value ?? ""); }

    public bool SplitByRanges
    {
        get => _splitByRanges;
        set { if (SetProperty(ref _splitByRanges, value)) OnPropertyChanged(nameof(SplitByEvery)); }
    }
    public bool SplitByEvery { get => !_splitByRanges; set => SplitByRanges = !value; }
    public string SplitRanges { get => _splitRanges; set => SetProperty(ref _splitRanges, value ?? ""); }
    public int SplitEveryN { get => _splitEveryN; set => SetProperty(ref _splitEveryN, value); }

    public string BatesPrefix { get => _batesPrefix; set => SetProperty(ref _batesPrefix, value ?? ""); }
    public string BatesSuffix { get => _batesSuffix; set => SetProperty(ref _batesSuffix, value ?? ""); }
    public int BatesDigits { get => _batesDigits; set => SetProperty(ref _batesDigits, value); }
    public long BatesStart { get => _batesStart; set => SetProperty(ref _batesStart, value); }

    public string WatermarkText { get => _watermarkText; set => SetProperty(ref _watermarkText, value ?? ""); }

    public string RenamePattern
    {
        get => _renamePattern;
        set { if (SetProperty(ref _renamePattern, value ?? "")) UpdateRenamePreview(); }
    }

    public long RenameStart
    {
        get => _renameStart;
        set { if (SetProperty(ref _renameStart, value)) UpdateRenamePreview(); }
    }

    public string RenamePreview
    {
        get => _renamePreview;
        private set => SetProperty(ref _renamePreview, value);
    }

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------
    public ICommand AddFilesCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand ChooseOutputCommand { get; }
    public ICommand OpenOutputCommand { get; }
    public ICommand MergeCommand { get; }
    public ICommand SplitCommand { get; }
    public ICommand BatesCommand { get; }
    public ICommand WatermarkCommand { get; }
    public ICommand RenameCommand { get; }

    // ------------------------------------------------------------------
    // Adding / arranging files
    // ------------------------------------------------------------------
    private async Task AddFilesFromDialogAsync()
    {
        var files = PickFiles?.Invoke();
        if (files is { Length: > 0 }) await AddFilesAsync(files);
    }

    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        if (IsBusy) return;

        var existing = new HashSet<string>(Items.Select(i => i.FilePath), StringComparer.OrdinalIgnoreCase);
        var added = new List<DocItem>();

        foreach (var path in ExpandPaths(paths))
        {
            if (existing.Add(path))
            {
                var item = new DocItem(path);
                Items.Add(item);
                added.Add(item);
            }
        }
        if (added.Count == 0) return;

        IsBusy = true;
        Status = $"Reading {added.Count} file(s)…";
        try
        {
            // Page counts can take a moment for big files, so do them off the UI thread.
            var counts = await Task.Run(() => added.Select(i => i.IsPdf ? TryCountPages(i.FilePath) : null).ToList());
            for (int i = 0; i < added.Count; i++) added[i].Pages = counts[i];

            var totalPages = Items.Sum(i => i.Pages ?? 0);
            Status = $"{Items.Count} file(s), {totalPages} pages. Pick a tool on the right.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static int? TryCountPages(string path)
    {
        try { return PdfService.GetPageCount(path); }
        catch (Exception) { return null; }     // shown as "?" in the list; the tools will explain why when they run
    }

    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                foreach (var f in Directory.EnumerateFiles(p).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    yield return f;
            }
            else if (File.Exists(p))
            {
                yield return p;
            }
        }
    }

    private void RemoveSelected()
    {
        if (Selected != null) Items.Remove(Selected);
    }

    private void Move(int direction)
    {
        var item = Selected;
        if (item == null) return;

        int from = Items.IndexOf(item);
        int to = from + direction;
        if (from < 0 || to < 0 || to >= Items.Count) return;

        Items.Move(from, to);
        Selected = item;
    }

    // ------------------------------------------------------------------
    // Output folder commands
    // ------------------------------------------------------------------
    private void ChooseOutput()
    {
        var folder = PickFolder?.Invoke();
        if (string.IsNullOrWhiteSpace(folder)) return;
        _outputFolder = folder;
        OnPropertyChanged(nameof(OutputFolderText));
    }

    private void OpenOutput()
    {
        var folder = LastOutputFolder ?? (Items.Count > 0 || _outputFolder != null ? ResolveOutputFolder() : null);
        if (folder == null || !Directory.Exists(folder))
        {
            ShowMessage?.Invoke("There's no output folder yet. Run a tool first and it will be created.");
            return;
        }
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    // ------------------------------------------------------------------
    // The five tools
    // ------------------------------------------------------------------
    private List<string>? RequirePdfs(int minimum, string needMessage)
    {
        var pdfs = Items.Where(i => i.IsPdf).Select(i => i.FilePath).ToList();
        if (pdfs.Count < minimum)
        {
            ShowMessage?.Invoke(needMessage);
            return null;
        }
        return pdfs;
    }

    private async Task MergeAsync()
    {
        var pdfs = RequirePdfs(2, "Add at least two PDFs to merge. They're joined in the order shown in the list.");
        if (pdfs == null) return;

        var name = Path.GetFileName(MergeName.Trim());
        if (name.Length == 0) name = "Merged.pdf";
        if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";

        await RunAsync("Merging", folder =>
        {
            var target = PdfService.UniquePath(folder, name);
            var pages = PdfService.Merge(pdfs, target);
            return $"Merged {pdfs.Count} files ({pages} pages) into {Path.GetFileName(target)}.";
        });
    }

    private async Task SplitAsync()
    {
        if (Selected is not { IsPdf: true } doc)
        {
            ShowMessage?.Invoke("Select the PDF you want to split in the list first.");
            return;
        }

        bool byRanges = SplitByRanges;
        string ranges = SplitRanges;
        int everyN = SplitEveryN;
        if (!byRanges && everyN < 1)
        {
            ShowMessage?.Invoke("Pages per file must be at least 1.");
            return;
        }

        await RunAsync("Splitting", folder =>
        {
            int pageCount = PdfService.GetPageCount(doc.FilePath);

            List<List<int>> groups;
            if (byRanges)
            {
                if (!PageRanges.TryParseGroups(ranges, pageCount, out groups, out var error))
                    throw new InvalidOperationException(error);
            }
            else
            {
                groups = PageRanges.EveryN(everyN, pageCount);
            }

            var outputs = PdfService.Split(doc.FilePath, groups, folder);
            return $"Split {doc.Name} into {outputs.Count} file(s).";
        });
    }

    private async Task BatesAsync()
    {
        var pdfs = RequirePdfs(1, "Add the PDFs you want to number first.");
        if (pdfs == null) return;

        var format = new BatesFormatter(BatesPrefix, BatesSuffix, BatesDigits);
        long start = BatesStart;
        if (start < 0)
        {
            ShowMessage?.Invoke("The starting number can't be negative.");
            return;
        }

        await RunAsync("Stamping Bates numbers", folder =>
        {
            var (outputs, next) = PdfService.StampBates(pdfs, folder, format, start);
            return $"Stamped {outputs.Count} file(s): {format.Label(start)} to {format.Label(next - 1)}.";
        });
    }

    private async Task WatermarkAsync()
    {
        var pdfs = RequirePdfs(1, "Add the PDFs you want to watermark first.");
        if (pdfs == null) return;

        var text = WatermarkText.Trim();
        if (text.Length == 0)
        {
            ShowMessage?.Invoke("Enter the watermark text, for example CONFIDENTIAL.");
            return;
        }

        await RunAsync("Adding watermark", folder =>
        {
            var outputs = PdfService.Watermark(pdfs, folder, text);
            return $"Watermarked {outputs.Count} file(s) with \"{text}\".";
        });
    }

    private async Task RenameAsync()
    {
        if (Items.Count == 0)
        {
            ShowMessage?.Invoke("Add the files you want to rename first.");
            return;
        }

        var plan = RenamePlanner.Plan(Items.Select(i => i.FilePath).ToList(), RenamePattern, RenameStart, DateTime.Today);

        await RunAsync("Copying with new names", folder =>
        {
            Directory.CreateDirectory(folder);
            foreach (var item in plan)
                File.Copy(item.SourcePath, PdfService.UniquePath(folder, item.NewName));
            return $"Copied {plan.Count} file(s) with new names. The originals are untouched.";
        });
    }

    /// <summary>Shared wrapper: busy flag, output folder, error reporting.</summary>
    private async Task RunAsync(string label, Func<string, string> work)
    {
        if (IsBusy) return;

        IsBusy = true;
        Status = label + "…";
        try
        {
            var folder = ResolveOutputFolder();
            var summary = await Task.Run(() => work(folder));
            LastOutputFolder = folder;
            Status = summary;
        }
        catch (Exception ex)
        {
            Status = "That didn't work. See the message for details.";
            ShowMessage?.Invoke(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateRenamePreview()
    {
        if (Items.Count == 0)
        {
            RenamePreview = "Add files to see how they'll be renamed.";
            return;
        }

        var plan = RenamePlanner.Plan(Items.Select(i => i.FilePath).ToList(), RenamePattern, RenameStart, DateTime.Today);
        var lines = plan.Take(5).Select(p => $"{Path.GetFileName(p.SourcePath)}  →  {p.NewName}").ToList();
        if (plan.Count > 5) lines.Add($"…and {plan.Count - 5} more");
        RenamePreview = string.Join(Environment.NewLine, lines);
    }
}
