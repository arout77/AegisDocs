using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ReceiptCapture.Core;
using ReceiptCapture.Core.Export;
using ReceiptCapture.Core.Parsing;
using ReceiptCapture.Core.Storage;
using ReceiptCapture.Services;
using CategoryList = ReceiptCapture.Core.Categories;

namespace ReceiptCapture.ViewModels;

/// <summary>
/// Everything the main window shows and does. The window itself (MainWindow.xaml) only
/// describes the layout and binds to the properties and commands below.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private const string AllCategories = "All categories";

    private readonly IReceiptStore _store;
    private readonly IOcrService _ocr;

    private Receipt? _selected;
    private Receipt _draft = new();
    private string _searchText = "";
    private string _categoryFilter = AllCategories;
    private DateTime? _fromDate;
    private DateTime? _toDate;
    private bool _isBusy;
    private string _status = "Drop receipts or invoices anywhere on this window to get started.";
    private string _summary = "";

    public MainViewModel(IReceiptStore store, IOcrService ocr)
    {
        _store = store;
        _ocr = ocr;

        List<Receipt> loaded;
        try
        {
            loaded = store.Load();
        }
        catch (Exception ex)
        {
            loaded = new List<Receipt>();
            _status = $"Couldn't read your saved receipts: {ex.Message}";
        }

        Receipts = new ObservableCollection<Receipt>(loaded);

        var categories = CategoryList.Default
            .Concat(loaded.Select(r => r.Category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        CategoryChoices = new ObservableCollection<string>(categories);
        FilterCategories = new ObservableCollection<string>(new[] { AllCategories }.Concat(categories));

        // The "view" is the filtered, sorted window onto Receipts that the grid displays.
        View = CollectionViewSource.GetDefaultView(Receipts);
        View.SortDescriptions.Add(new SortDescription(nameof(Receipt.Date), ListSortDirection.Descending));
        View.Filter = Matches;

        Receipts.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            UpdateSummary();
        };

        AddFilesCommand = new RelayCommand(async () => await AddFilesAsync(), () => !IsBusy);
        NewCommand = new RelayCommand(NewEntry);
        SaveCommand = new RelayCommand(Save, () => !IsBusy);
        DeleteCommand = new RelayCommand(Delete, () => !IsBusy);
        ExportCommand = new RelayCommand(Export);
        OpenOriginalCommand = new RelayCommand(OpenOriginal, () => !string.IsNullOrEmpty(Draft.FilePath));
        ClearFiltersCommand = new RelayCommand(ClearFilters);

        UpdateSummary();
    }

    // ------------------------------------------------------------------
    // Hooks the window fills in (keeps dialogs out of the view model)
    // ------------------------------------------------------------------
    public Action<string>? ShowMessage { get; set; }
    public Func<string, bool>? Confirm { get; set; }
    public Func<string?>? PickExportPath { get; set; }
    public Func<string[]?>? PickFiles { get; set; }

    // ------------------------------------------------------------------
    // Data the window binds to
    // ------------------------------------------------------------------
    public ObservableCollection<Receipt> Receipts { get; }
    public ICollectionView View { get; }
    public ObservableCollection<string> CategoryChoices { get; }
    public ObservableCollection<string> FilterCategories { get; }

    public bool IsEmpty => Receipts.Count == 0;

    /// <summary>The row selected in the list.</summary>
    public Receipt? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value) && value != null)
                Draft = value.Clone();   // edit a copy; Save writes it back
        }
    }

    /// <summary>The receipt shown in the edit form on the right.</summary>
    public Receipt Draft
    {
        get => _draft;
        private set
        {
            if (SetProperty(ref _draft, value))
                _ = RefreshPreviewAsync(value.FilePath);
        }
    }

    // ------------------------------------------------------------------
    // Preview picture in the Details pane (first page for PDFs)
    // ------------------------------------------------------------------
    private ImageSource? _previewImage;
    private string? _previewPath;
    private int _previewVersion;

    public ImageSource? PreviewImage
    {
        get => _previewImage;
        private set
        {
            if (SetProperty(ref _previewImage, value))
                OnPropertyChanged(nameof(HasPreview));
        }
    }

    public bool HasPreview => _previewImage != null;

    private async Task RefreshPreviewAsync(string? path)
    {
        if (path == _previewPath && _previewImage != null) return;   // same file, nothing to redo

        _previewPath = path;
        int version = ++_previewVersion;   // lets a slow load notice it has been overtaken
        PreviewImage = null;

        var image = await PreviewLoader.LoadAsync(path);
        if (version == _previewVersion) PreviewImage = image;
    }

    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value ?? "")) RefreshFilter(); }
    }

    public string CategoryFilter
    {
        get => _categoryFilter;
        set { if (SetProperty(ref _categoryFilter, value ?? AllCategories)) RefreshFilter(); }
    }

    public DateTime? FromDate
    {
        get => _fromDate;
        set { if (SetProperty(ref _fromDate, value)) RefreshFilter(); }
    }

    public DateTime? ToDate
    {
        get => _toDate;
        set { if (SetProperty(ref _toDate, value)) RefreshFilter(); }
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

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public ICommand AddFilesCommand { get; }
    public ICommand NewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand OpenOriginalCommand { get; }
    public ICommand ClearFiltersCommand { get; }

    // ------------------------------------------------------------------
    // Importing
    // ------------------------------------------------------------------
    private async Task AddFilesAsync()
    {
        var files = PickFiles?.Invoke();
        if (files is { Length: > 0 })
            await ImportFilesAsync(files);
    }

    /// <summary>Copies each file into the app's folder, reads it with OCR, and adds it to the list for review.</summary>
    public async Task ImportFilesAsync(IEnumerable<string> paths)
    {
        if (IsBusy) return;

        var all = ExpandPaths(paths).ToList();
        var files = all.Where(WindowsOcrService.IsSupported).ToList();
        var problems = new List<string>();

        if (all.Count > files.Count)
            problems.Add($"{all.Count - files.Count} file(s) skipped: only images (PNG, JPG, BMP, GIF, TIFF) and PDFs are supported.");

        if (files.Count == 0)
        {
            if (problems.Count > 0) ShowMessage?.Invoke(string.Join(Environment.NewLine, problems));
            return;
        }

        IsBusy = true;
        int imported = 0;
        Receipt? last = null;

        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                var file = files[i];
                var name = Path.GetFileName(file);
                Status = $"Reading {name} ({i + 1} of {files.Count})…";

                try
                {
                    var hash = await Task.Run(() => FileHasher.Sha256(file));
                    if (Receipts.Any(r => r.FileHash == hash))
                    {
                        problems.Add($"{name}: already imported.");
                        continue;
                    }

                    var stored = CopyIntoStore(file, hash);

                    string text = "";
                    try
                    {
                        text = await _ocr.ExtractTextAsync(stored);
                    }
                    catch (Exception ex)
                    {
                        problems.Add($"{name}: couldn't read the text automatically ({ex.Message}). You can still fill in the details by hand.");
                    }

                    var parsed = ReceiptParser.Parse(text);
                    var receipt = new Receipt
                    {
                        Vendor = parsed.Vendor ?? "",
                        Date = parsed.Date ?? DateTime.Today,
                        Total = parsed.Total ?? 0m,
                        Tax = parsed.Tax,
                        Category = parsed.Category ?? CategoryList.Uncategorized,
                        FilePath = stored,
                        FileHash = hash,
                        RawText = text,
                        NeedsReview = true,
                    };

                    EnsureCategory(receipt.Category);
                    Receipts.Add(receipt);
                    last = receipt;
                    imported++;
                }
                catch (Exception ex)
                {
                    problems.Add($"{name}: {ex.Message}");
                }
            }

            if (imported > 0)
            {
                Persist();
                View.Refresh();
                UpdateSummary();
                if (last != null) Selected = last;
            }

            Status = imported == 0
                ? "Nothing was imported."
                : $"Imported {imported} file(s). Check the details on the right, then press Save.";
        }
        finally
        {
            IsBusy = false;
        }

        if (problems.Count > 0)
            ShowMessage?.Invoke(string.Join(Environment.NewLine + Environment.NewLine, problems));
    }

    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                foreach (var f in Directory.EnumerateFiles(p)) yield return f;
            }
            else if (File.Exists(p))
            {
                yield return p;
            }
        }
    }

    private static string CopyIntoStore(string source, string hash)
    {
        Directory.CreateDirectory(AppPaths.ReceiptsDir);
        var target = Path.Combine(AppPaths.ReceiptsDir, hash[..16] + Path.GetExtension(source).ToLowerInvariant());
        File.Copy(source, target, overwrite: true);
        return target;
    }

    // ------------------------------------------------------------------
    // Edit / save / delete
    // ------------------------------------------------------------------
    private void NewEntry()
    {
        Selected = null;
        Draft = new Receipt();
        Status = "New entry. Fill in the details and press Save.";
    }

    private void Save()
    {
        var draft = Draft;
        if (string.IsNullOrWhiteSpace(draft.Vendor))
        {
            ShowMessage?.Invoke("Please enter a vendor before saving.");
            return;
        }

        draft.NeedsReview = false;

        var existing = Receipts.FirstOrDefault(r => r.Id == draft.Id);
        if (existing == null)
        {
            existing = draft.Clone();
            Receipts.Add(existing);
        }
        else
        {
            existing.CopyFrom(draft);
        }

        EnsureCategory(existing.Category);
        Persist();
        View.Refresh();     // re-sorts and re-filters now that values may have changed
        UpdateSummary();
        Status = $"Saved {existing.Vendor} ({existing.Total.ToString("C", CultureInfo.CurrentCulture)}).";

        // Batch workflow: jump straight to the next receipt that still needs a look.
        var next = View.Cast<Receipt>().FirstOrDefault(r => r.NeedsReview && r.Id != existing.Id);
        Selected = next ?? existing;
    }

    private void Delete()
    {
        var existing = Receipts.FirstOrDefault(r => r.Id == Draft.Id);
        if (existing == null)
        {
            // Never saved, so there's nothing to delete; just clear the form.
            Selected = null;
            Draft = new Receipt();
            return;
        }

        var ok = Confirm?.Invoke($"Delete the receipt from \"{existing.Vendor}\"?\n\nThis also removes the app's stored copy of the file.") == true;
        if (!ok) return;

        Receipts.Remove(existing);
        DeleteStoredFile(existing.FilePath);
        Persist();

        Selected = null;
        Draft = new Receipt();
        Status = "Deleted.";
    }

    private static void DeleteStoredFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            // Only ever delete files inside the app's own folder.
            var root = Path.GetFullPath(AppPaths.ReceiptsDir) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { /* a leftover file isn't worth interrupting the user */ }
        catch (UnauthorizedAccessException) { }
    }

    private void OpenOriginal()
    {
        var path = Draft.FilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            ShowMessage?.Invoke("The original file couldn't be found.");
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    // ------------------------------------------------------------------
    // Export
    // ------------------------------------------------------------------
    private void Export()
    {
        var items = View.Cast<Receipt>().ToList();
        if (items.Count == 0)
        {
            ShowMessage?.Invoke("There's nothing to export. (Only the receipts currently shown in the list are exported, so check your filters.)");
            return;
        }

        var path = PickExportPath?.Invoke();
        if (path == null) return;

        try
        {
            CsvExporter.Export(items, path);
            Status = $"Exported {items.Count} receipt(s) to {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage?.Invoke($"Couldn't write the file. Is it open in Excel?\n\n{ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Filtering, summary, persistence
    // ------------------------------------------------------------------
    private bool Matches(object item)
    {
        if (item is not Receipt r) return false;

        if (_categoryFilter != AllCategories &&
            !string.Equals(r.Category, _categoryFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        if (_fromDate is { } fromDate && r.Date.Date < fromDate.Date) return false;
        if (_toDate is { } toDate && r.Date.Date > toDate.Date) return false;

        var query = _searchText.Trim();
        if (query.Length > 0)
        {
            bool hit = Contains(r.Vendor, query) || Contains(r.Category, query) ||
                       Contains(r.Notes, query) || Contains(r.RawText, query) ||
                       r.Total.ToString("0.00", CultureInfo.CurrentCulture).Contains(query, StringComparison.Ordinal);
            if (!hit) return false;
        }
        return true;
    }

    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.CurrentCultureIgnoreCase);

    private void RefreshFilter()
    {
        View.Refresh();
        UpdateSummary();
    }

    private void ClearFilters()
    {
        SearchText = "";
        CategoryFilter = AllCategories;
        FromDate = null;
        ToDate = null;
    }

    private void UpdateSummary()
    {
        var shown = View.Cast<Receipt>().ToList();
        var total = shown.Sum(r => r.Total).ToString("C", CultureInfo.CurrentCulture);
        Summary = shown.Count == 1 ? $"1 receipt · {total}" : $"{shown.Count} receipts · {total}";
    }

    private void EnsureCategory(string category)
    {
        if (CategoryChoices.Contains(category, StringComparer.OrdinalIgnoreCase)) return;
        CategoryChoices.Add(category);
        FilterCategories.Add(category);
    }

    private void Persist()
    {
        try
        {
            _store.Save(Receipts);
        }
        catch (Exception ex)
        {
            ShowMessage?.Invoke($"Couldn't save your data: {ex.Message}");
        }
    }
}
