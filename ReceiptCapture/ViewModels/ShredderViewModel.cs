using System.Collections.ObjectModel;
using System.Windows.Input;
using ReceiptCapture.Core;
using ReceiptCapture.Core.Security;
using ReceiptCapture.Core.Storage;

namespace ReceiptCapture.ViewModels;

/// <summary>One file or folder waiting to be shredded.</summary>
public sealed class ShredItem : ObservableObject
{
    private string _detail = "counting…";

    public ShredItem(string path, bool isFolder)
    {
        Path = path;
        IsFolder = isFolder;
    }

    public string Path { get; }
    public bool IsFolder { get; }
    public string Name => IsFolder ? System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path : System.IO.Path.GetFileName(Path);
    public string Kind => IsFolder ? "Folder" : "File";

    public int FileCount { get; set; }
    public long Bytes { get; set; }

    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }
}

/// <summary>Behaviour of the Shredder tab: a list of files and folders to destroy for good.</summary>
public sealed class ShredderViewModel : ObservableObject
{
    private ShredItem? _selected;
    private bool _isBusy;
    private string _status = "Drop files or folders here to destroy them for good. Nothing is deleted until you press Shred.";
    private string _progressText = "";
    private int _passes = 3;
    private CancellationTokenSource? _cts;

    public ShredderViewModel()
    {
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));

        AddFilesCommand = new RelayCommand(AddFilesFromDialog, () => !IsBusy);
        AddFolderCommand = new RelayCommand(AddFolderFromDialog, () => !IsBusy);
        RemoveCommand = new RelayCommand(RemoveSelected, () => Selected != null && !IsBusy);
        ClearCommand = new RelayCommand(() => Items.Clear(), () => Items.Count > 0 && !IsBusy);
        ShredCommand = new RelayCommand(async () => await ShredAsync(), () => Items.Count > 0 && !IsBusy);
        StopCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy && _cts != null);
    }

    // Hooks the view fills in
    public Action<string>? ShowMessage { get; set; }
    public Func<string, bool>? Confirm { get; set; }
    public Func<string[]?>? PickFiles { get; set; }
    public Func<string?>? PickFolder { get; set; }

    public ObservableCollection<ShredItem> Items { get; } = new();
    public bool IsEmpty => Items.Count == 0;

    public ShredItem? Selected { get => _selected; set => SetProperty(ref _selected, value); }

    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }

    /// <summary>How many times every byte is overwritten. 1, 3 or 7.</summary>
    public int Passes { get => _passes; set => SetProperty(ref _passes, value); }
    public IReadOnlyList<int> PassChoices { get; } = new[] { 1, 3, 7 };

    public ICommand AddFilesCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ShredCommand { get; }
    public ICommand StopCommand { get; }

    // ------------------------------------------------------------------
    // Building the list
    // ------------------------------------------------------------------
    private void AddFilesFromDialog()
    {
        var files = PickFiles?.Invoke();
        if (files is { Length: > 0 }) _ = AddPathsAsync(files);
    }

    private void AddFolderFromDialog()
    {
        var folder = PickFolder?.Invoke();
        if (!string.IsNullOrWhiteSpace(folder)) _ = AddPathsAsync(new[] { folder });
    }

    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        if (IsBusy) return;

        var existing = new HashSet<string>(Items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        var refused = new List<string>();
        var added = new List<ShredItem>();

        foreach (var raw in paths)
        {
            string path;
            try { path = Path.GetFullPath(raw); }
            catch (Exception) { continue; }

            bool isFolder = Directory.Exists(path);
            if (!isFolder && !File.Exists(path)) continue;

            var refusal = ShredGuard.Default.Check(path);
            if (refusal != null) { refused.Add($"{path}\n{refusal}"); continue; }

            if (!existing.Add(path)) continue;
            var item = new ShredItem(path, isFolder);
            Items.Add(item);
            added.Add(item);
        }

        if (refused.Count > 0)
            ShowMessage?.Invoke("Some items were not added:\n\n" + string.Join("\n\n", refused));
        if (added.Count == 0) return;

        IsBusy = true;
        Status = "Looking at what's inside…";
        try
        {
            await Task.Run(() => { foreach (var item in added) Measure(item); });
            Status = Summary();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void Measure(ShredItem item)
    {
        long bytes = 0;
        int files = 0;
        try
        {
            if (item.IsFolder)
            {
                foreach (var file in FolderWalker.EnumerateFiles(item.Path))
                {
                    files++;
                    try { bytes += new FileInfo(file).Length; } catch (IOException) { }
                }
            }
            else
            {
                files = 1;
                bytes = new FileInfo(item.Path).Length;
            }
        }
        catch (Exception)
        {
            // Unreadable folders are counted as far as they could be read; the shred run reports the rest.
        }

        item.FileCount = files;
        item.Bytes = bytes;
        item.Detail = item.IsFolder ? $"{files} file(s), {ByteSize.Format(bytes)}" : ByteSize.Format(bytes);
    }

    private string Summary()
    {
        int files = Items.Sum(i => i.FileCount);
        long bytes = Items.Sum(i => i.Bytes);
        return $"{Items.Count} item(s) ready: {files} file(s), {ByteSize.Format(bytes)}. Nothing is deleted until you press Shred.";
    }

    private void RemoveSelected()
    {
        if (Selected == null) return;
        Items.Remove(Selected);
        Status = Items.Count == 0 ? "The list is empty." : Summary();
    }

    // ------------------------------------------------------------------
    // Shredding
    // ------------------------------------------------------------------
    private async Task ShredAsync()
    {
        if (IsBusy || Items.Count == 0) return;

        int files = Items.Sum(i => i.FileCount);
        long bytes = Items.Sum(i => i.Bytes);
        int folders = Items.Count(i => i.IsFolder);

        var question =
            $"Permanently destroy {files} file(s) ({ByteSize.Format(bytes)})" +
            (folders > 0 ? $" and {folders} folder(s) with everything inside them" : "") + "?\n\n" +
            "This cannot be undone. The files do not go to the Recycle Bin and no program can bring them back.";
        if (Confirm?.Invoke(question) != true) return;

        var paths = Items.Select(i => i.Path).ToList();
        var options = new ShredOptions { Passes = Passes };
        var progress = new Progress<ShredProgress>(p =>
        {
            ProgressText = p.FilesTotal == 0 ? "" : $"{p.FilesDone} of {p.FilesTotal} files done";
            if (!string.IsNullOrEmpty(p.CurrentPath)) Status = "Shredding " + Path.GetFileName(p.CurrentPath) + "…";
        });

        _cts = new CancellationTokenSource();
        IsBusy = true;
        ShredResult result;
        try
        {
            var token = _cts.Token;
            result = await Task.Run(() => FileShredder.Shred(paths, options, ShredGuard.Default, progress, token));
        }
        catch (Exception ex)
        {
            Status = "Shredding stopped because of a problem.";
            ShowMessage?.Invoke("Shredding hit a problem: " + ex.Message);
            return;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            ProgressText = "";
        }

        // Take away what is gone, keep what still exists (it failed or wasn't reached).
        foreach (var item in Items.Where(i => !(i.IsFolder ? Directory.Exists(i.Path) : File.Exists(i.Path))).ToList())
            Items.Remove(item);

        Status = result.Stopped
            ? $"Stopped. {result.FilesShredded} file(s) were shredded; the rest are still in the list."
            : result.Failures.Count == 0
                ? $"Done. {result.FilesShredded} file(s) destroyed."
                : $"Done, with problems. {result.FilesShredded} file(s) destroyed; {result.Failures.Count} item(s) could not be.";

        if (result.Failures.Count > 0)
        {
            var lines = result.Failures.Take(8).Select(f => $"{f.Path}\n    {f.Reason}");
            var more = result.Failures.Count > 8 ? $"\n\n…and {result.Failures.Count - 8} more." : "";
            ShowMessage?.Invoke(
                $"{result.FilesShredded} file(s) were destroyed. These were not:\n\n{string.Join("\n\n", lines)}{more}");
        }
    }
}
