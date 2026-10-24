using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using ReceiptCapture.Core;
using ReceiptCapture.Core.Pdf;
using ReceiptCapture.Core.Redaction;
using ReceiptCapture.Services;

namespace ReceiptCapture.ViewModels;

/// <summary>
/// The Redact tab: open a PDF, Word document or picture, review what was found (and add boxes by
/// hand), then save a PDF copy in which the approved areas are blacked out for good.
/// </summary>
public sealed class RedactionViewModel : ObservableObject
{
    private const int MaxPages = 150;

    private readonly IRedactionService _service;

    private List<ScannedPage> _pages = new();
    private string? _filePath;
    private int _currentPage;
    private bool _isBusy;
    private string _status = "Open a PDF, Word document or picture to find sensitive information in it. Your original is never changed.";
    private string _summary = "";
    private RedactionRegion? _selectedRegion;
    private bool _makeSearchable = true;
    private bool _sourceWasProtected;

    private bool _findSsn = true;
    private bool _findPhone = true;
    private bool _findEmail = true;
    private bool _findCards = true;
    private bool _findDob = true;
    private bool _findAddress = true;
    private bool _findEin = true;
    private bool _findBank = true;
    private bool _findRouting = true;
    private bool _findLicense = true;
    private bool _findPassport = true;
    private string _customTermsText = "";

    // zoom: "fit" tracks the window size; otherwise a fixed factor where 1.0 = actual size (100%)
    private const double MinZoom = 0.2;
    private const double MaxZoom = 4.0;
    private bool _fitToWindow = true;
    private double _manualZoom = 1.0;
    private double _fitZoom = 1.0;
    private double _viewportWidth;
    private double _viewportHeight;

    public RedactionViewModel(IRedactionService service)
    {
        _service = service;

        OpenCommand = new RelayCommand(async () => await OpenFromDialogAsync(), () => !IsBusy);
        PrevPageCommand = new RelayCommand(() => GoToPage(_currentPage - 1), () => HasDocument && _currentPage > 0);
        NextPageCommand = new RelayCommand(() => GoToPage(_currentPage + 1), () => HasDocument && _currentPage < _pages.Count - 1);
        ApproveAllCommand = new RelayCommand(() => SetAllApproved(true), () => Regions.Count > 0);
        ApproveNoneCommand = new RelayCommand(() => SetAllApproved(false), () => Regions.Count > 0);
        ApplyCommand = new RelayCommand(async () => await ApplyAsync(), () => HasDocument && !IsBusy);
        ZoomInCommand = new RelayCommand(() => ZoomBy(1.25), () => HasDocument);
        ZoomOutCommand = new RelayCommand(() => ZoomBy(0.8), () => HasDocument);
        Zoom100Command = new RelayCommand(() => SetManualZoom(1.0), () => HasDocument);
        ZoomFitCommand = new RelayCommand(FitToWindow, () => HasDocument);
    }

    // ------------------------------------------------------------------
    // Hooks the view fills in
    // ------------------------------------------------------------------
    public Action<string>? ShowMessage { get; set; }
    public Func<string?>? PickFile { get; set; }
    public Func<string, string?>? PickSavePath { get; set; }

    /// <summary>
    /// Asks for a PDF's password. Receives the file name and whether the previous try was wrong;
    /// returns the password, or null if the person cancelled.
    /// </summary>
    public Func<string, bool, string?>? AskPassword { get; set; }

    // ------------------------------------------------------------------
    // What the view shows
    // ------------------------------------------------------------------
    public ObservableCollection<RedactionRegion> Regions { get; } = new();
    public ObservableCollection<RedactionRegion> CurrentRegions { get; } = new();

    public bool HasDocument => _pages.Count > 0;
    public bool NoDocument => !HasDocument;

    public byte[]? CurrentPagePng => HasDocument ? _pages[_currentPage].Png : null;
    public double PageWidth => HasDocument ? _pages[_currentPage].PixelWidth : 1;
    public double PageHeight => HasDocument ? _pages[_currentPage].PixelHeight : 1;
    public string PageLabel => HasDocument ? $"Page {_currentPage + 1} of {_pages.Count}" : "";

    /// <summary>
    /// Keep the saved copy searchable: the words that were NOT covered by a box are added back as an
    /// invisible text layer. Turn it off for a copy that is pure pictures with no text at all.
    /// </summary>
    public bool MakeSearchable
    {
        get => _makeSearchable;
        set => SetProperty(ref _makeSearchable, value);
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

    /// <summary>Selecting an item in the list jumps to its page.</summary>
    public RedactionRegion? SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (SetProperty(ref _selectedRegion, value) && value != null && value.Page != _currentPage)
                GoToPage(value.Page);
        }
    }

    // ------------------------------------------------------------------
    // What to look for
    // ------------------------------------------------------------------
    public bool FindSsn { get => _findSsn; set { if (SetProperty(ref _findSsn, value)) RebuildAutoRegions(); } }
    public bool FindPhone { get => _findPhone; set { if (SetProperty(ref _findPhone, value)) RebuildAutoRegions(); } }
    public bool FindEmail { get => _findEmail; set { if (SetProperty(ref _findEmail, value)) RebuildAutoRegions(); } }
    public bool FindCards { get => _findCards; set { if (SetProperty(ref _findCards, value)) RebuildAutoRegions(); } }
    public bool FindDob { get => _findDob; set { if (SetProperty(ref _findDob, value)) RebuildAutoRegions(); } }
    public bool FindAddress { get => _findAddress; set { if (SetProperty(ref _findAddress, value)) RebuildAutoRegions(); } }
    public bool FindEin { get => _findEin; set { if (SetProperty(ref _findEin, value)) RebuildAutoRegions(); } }
    public bool FindBank { get => _findBank; set { if (SetProperty(ref _findBank, value)) RebuildAutoRegions(); } }
    public bool FindRouting { get => _findRouting; set { if (SetProperty(ref _findRouting, value)) RebuildAutoRegions(); } }
    public bool FindLicense { get => _findLicense; set { if (SetProperty(ref _findLicense, value)) RebuildAutoRegions(); } }
    public bool FindPassport { get => _findPassport; set { if (SetProperty(ref _findPassport, value)) RebuildAutoRegions(); } }

    public string CustomTermsText
    {
        get => _customTermsText;
        set { if (SetProperty(ref _customTermsText, value ?? "")) RebuildAutoRegions(); }
    }

    public ICommand OpenCommand { get; }
    public ICommand PrevPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand ApproveAllCommand { get; }
    public ICommand ApproveNoneCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand Zoom100Command { get; }
    public ICommand ZoomFitCommand { get; }

    // ------------------------------------------------------------------
    // Opening and scanning
    // ------------------------------------------------------------------
    private async Task OpenFromDialogAsync()
    {
        var path = PickFile?.Invoke();
        if (!string.IsNullOrEmpty(path)) await OpenAsync(path);
    }

    public async Task OpenAsync(string path)
    {
        if (IsBusy) return;
        if (InputFile.Classify(path) == InputKind.Unsupported)
        {
            ShowMessage?.Invoke($"Choose {InputFile.SupportedText}.");
            return;
        }

        IsBusy = true;
        Status = "Reading the document…";
        try
        {
            var progress = new Progress<string>(text => Status = text);

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
                    if (entered == null)
                    {
                        Status = "Cancelled. That file needs its password to be opened.";
                        return;
                    }
                    password = entered;
                    Status = "Trying that password…";
                }
            }

            _pages = pages;
            _filePath = path;
            _sourceWasProtected = password != null;
            _currentPage = 0;
            _fitToWindow = true;

            foreach (var region in Regions) region.PropertyChanged -= OnRegionChanged;
            Regions.Clear();
            _selectedRegion = null;
            OnPropertyChanged(nameof(SelectedRegion));

            RebuildAutoRegions();
            OnPropertyChanged(nameof(HasDocument));
            OnPropertyChanged(nameof(NoDocument));
            RefreshPage();

            Status = Regions.Count == 0
                ? $"{Path.GetFileName(path)}: nothing sensitive was detected. Drag on a page to box anything yourself."
                : $"{Path.GetFileName(path)}: found {Regions.Count} item(s). Review them, then save the redacted copy.";
        }
        catch (Exception ex)
        {
            Status = "Couldn't open that file.";
            ShowMessage?.Invoke(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------------
    // Regions
    // ------------------------------------------------------------------
    private FindOptions BuildOptions() => new()
    {
        Ssn = FindSsn,
        Phone = FindPhone,
        Email = FindEmail,
        CreditCard = FindCards,
        DateOfBirth = FindDob,
        Address = FindAddress,
        Ein = FindEin,
        BankAccount = FindBank,
        RoutingNumber = FindRouting,
        DriversLicense = FindLicense,
        Passport = FindPassport,
        CustomTerms = CustomTermsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    };

    /// <summary>Re-runs the finder on the words already read (no new OCR). Hand-drawn boxes are kept.</summary>
    private void RebuildAutoRegions()
    {
        if (_pages.Count == 0) return;

        var options = BuildOptions();

        foreach (var old in Regions.Where(r => !r.IsManual).ToList())
        {
            old.PropertyChanged -= OnRegionChanged;
            Regions.Remove(old);
        }

        for (int i = 0; i < _pages.Count; i++)
        {
            foreach (var region in RedactionMapper.FindRegions(i, _pages[i].Words, options))
            {
                region.PropertyChanged += OnRegionChanged;
                Regions.Add(region);
            }
        }

        RefreshCurrentRegions();
        UpdateSummary();
    }

    /// <summary>Called by the view when the user drags a box on the page (coordinates are page-image pixels).</summary>
    public void AddManualRegion(double x, double y, double width, double height)
    {
        if (!HasDocument) return;
        var page = _pages[_currentPage];

        double left = Math.Clamp(x, 0, page.PixelWidth);
        double top = Math.Clamp(y, 0, page.PixelHeight);
        double right = Math.Clamp(x + width, 0, page.PixelWidth);
        double bottom = Math.Clamp(y + height, 0, page.PixelHeight);
        if (right - left < 4 || bottom - top < 4) return;

        var region = new RedactionRegion(_currentPage, null, "", left, top, right - left, bottom - top);
        region.PropertyChanged += OnRegionChanged;
        Regions.Add(region);
        RefreshCurrentRegions();
        UpdateSummary();
    }

    /// <summary>Right-clicking a hand-drawn box removes it.</summary>
    public void RemoveRegion(RedactionRegion region)
    {
        if (!region.IsManual) return;
        region.PropertyChanged -= OnRegionChanged;
        Regions.Remove(region);
        RefreshCurrentRegions();
        UpdateSummary();
    }

    private void SetAllApproved(bool approved)
    {
        foreach (var region in Regions) region.Approved = approved;
    }

    private void OnRegionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RedactionRegion.Approved)) UpdateSummary();
    }

    private void UpdateSummary()
    {
        int approved = Regions.Count(r => r.Approved);
        Summary = Regions.Count == 0
            ? "Nothing to redact yet."
            : $"{approved} of {Regions.Count} item(s) will be blacked out.";
    }

    // ------------------------------------------------------------------
    // Zoom
    // ------------------------------------------------------------------
    /// <summary>The scale applied to the page image (pixels are drawn at this factor).</summary>
    public double EffectiveScale => CurrentZoom * BaseScale;

    public string ZoomPercent => $"{Math.Round(CurrentZoom * 100)}%";

    private double CurrentZoom => _fitToWindow ? _fitZoom : _manualZoom;

    /// <summary>Scale at which the page appears at its real size (100%) on screen: the image is rendered at a higher resolution than that.</summary>
    private double BaseScale => HasDocument
        ? _pages[_currentPage].WidthPoints / 0.75 / _pages[_currentPage].PixelWidth
        : 1;

    /// <summary>Called by the view whenever the viewing area changes size.</summary>
    public void SetViewport(double width, double height)
    {
        _viewportWidth = width;
        _viewportHeight = height;
        RecomputeFit();
    }

    private void RecomputeFit()
    {
        if (HasDocument && _viewportWidth > 0 && _viewportHeight > 0)
        {
            var page = _pages[_currentPage];
            double pageWidth = page.WidthPoints / 0.75;     // page size on screen at 100%, in 1/96-inch units
            double pageHeight = page.HeightPoints / 0.75;
            const double margin = 48;                       // breathing room around the page
            _fitZoom = Math.Clamp(Math.Min((_viewportWidth - margin) / pageWidth, (_viewportHeight - margin) / pageHeight), MinZoom, MaxZoom);
        }
        NotifyZoom();
    }

    private void ZoomBy(double factor) => SetManualZoom(CurrentZoom * factor);

    private void SetManualZoom(double zoom)
    {
        _manualZoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _fitToWindow = false;
        NotifyZoom();
    }

    private void FitToWindow()
    {
        _fitToWindow = true;
        RecomputeFit();
    }

    private void NotifyZoom()
    {
        OnPropertyChanged(nameof(EffectiveScale));
        OnPropertyChanged(nameof(ZoomPercent));
    }

    // ------------------------------------------------------------------
    // Paging
    // ------------------------------------------------------------------
    private void GoToPage(int index)
    {
        if (!HasDocument) return;
        _currentPage = Math.Clamp(index, 0, _pages.Count - 1);
        RefreshPage();
    }

    private void RefreshPage()
    {
        RecomputeFit();
        RefreshCurrentRegions();
        OnPropertyChanged(nameof(CurrentPagePng));
        OnPropertyChanged(nameof(PageWidth));
        OnPropertyChanged(nameof(PageHeight));
        OnPropertyChanged(nameof(PageLabel));
    }

    private void RefreshCurrentRegions()
    {
        CurrentRegions.Clear();
        foreach (var region in Regions.Where(r => r.Page == _currentPage))
            CurrentRegions.Add(region);
    }

    // ------------------------------------------------------------------
    // Saving the redacted copy
    // ------------------------------------------------------------------
    private async Task ApplyAsync()
    {
        if (IsBusy || _pages.Count == 0 || _filePath == null) return;

        var approved = Regions.Where(r => r.Approved).ToList();
        if (approved.Count == 0)
        {
            ShowMessage?.Invoke("Nothing is selected to redact yet. Tick items in the list or drag a box on the page.");
            return;
        }

        var suggestedName = Path.GetFileNameWithoutExtension(_filePath) + "_REDACTED.pdf";
        var chosen = PickSavePath?.Invoke(suggestedName);
        if (string.IsNullOrWhiteSpace(chosen)) return;

        string outputPath = chosen;
        if (string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(_filePath), StringComparison.OrdinalIgnoreCase))
        {
            ShowMessage?.Invoke("Please save the redacted copy under a different name so your original is kept.");
            return;
        }

        IsBusy = true;
        try
        {
            var images = new List<PdfPageImage>();
            var layerTexts = new List<string>();     // what the invisible text layer says, page by page
            for (int i = 0; i < _pages.Count; i++)
            {
                Status = $"Blacking out page {i + 1} of {_pages.Count}…";
                await Task.Yield();   // let the window repaint between pages

                var page = _pages[i];
                int pageIndex = i;
                var onThisPage = approved.Where(r => r.Page == pageIndex).ToList();
                var jpeg = _service.BurnToJpeg(page, onThisPage);

                // Searchable copy: the words left uncovered go back in as invisible text.
                // Any word that touches a box is left out, so redacted text can't hide in there.
                var layer = MakeSearchable ? OcrTextLayer.Build(page, onThisPage) : null;
                layerTexts.Add(layer == null ? "" : OcrTextLayer.PlainText(layer));
                images.Add(new PdfPageImage(jpeg, page.PixelWidth, page.PixelHeight, page.WidthPoints, page.HeightPoints, 3, layer));
            }

            Status = "Saving…";
            await Task.Run(() =>
            {
                using var file = File.Create(outputPath);
                ImagePdfWriter.Write(file, images);
            });

            // Check our own work: read the saved file back and look for anything we were meant to remove.
            var progress = new Progress<string>(text => Status = "Verifying " + text);
            var pageTexts = await _service.ReadPageTextsAsync(outputPath, progress);

            var redactedTexts = approved.Where(r => !r.IsManual).Select(r => r.MatchedText).ToList();
            var leaks = new List<string>();
            for (int i = 0; i < pageTexts.Count; i++)
                foreach (var leak in RedactionVerifier.FindLeaks(pageTexts[i], redactedTexts))
                    leaks.Add($"page {i + 1}: {leak}");

            // ...and the hidden text layer, which the picture check above can't see.
            for (int i = 0; i < layerTexts.Count; i++)
                foreach (var leak in RedactionVerifier.FindLeaks(layerTexts[i], redactedTexts))
                    leaks.Add($"page {i + 1} (hidden text): {leak}");

            var manualCount = approved.Count(r => r.IsManual);
            var manualNote = manualCount > 0
                ? $"\n\nThe {manualCount} hand-drawn box(es) can't be checked automatically, so look over those pages yourself."
                : "";
            if (_sourceWasProtected)
                manualNote += "\n\nThe original was password-protected, but this redacted copy is not. Protect it again before sending it if you need to.";
            if (MakeSearchable)
                manualNote += "\n\nThe copy is searchable: the text that was not covered can be searched and copied.";

            if (leaks.Count == 0)
            {
                Status = $"Saved {Path.GetFileName(outputPath)}. Verified: no redacted text was found in it.";
                ShowMessage?.Invoke(
                    $"Saved {outputPath}\n\nVerified: reading the saved file back found none of the {redactedTexts.Count} detected item(s) you redacted.{manualNote}");
            }
            else
            {
                Status = $"Saved, but {leaks.Count} item(s) still show in the output. Don't share it yet.";
                var list = string.Join("\n", leaks.Take(10)) + (leaks.Count > 10 ? $"\n…and {leaks.Count - 10} more" : "");
                ShowMessage?.Invoke(
                    $"Saved {outputPath}\n\nWARNING: these items can still be read in the saved file, so don't share it yet:\n\n{list}\n\nDraw boxes over them (or tick them in the list) and save again.{manualNote}");
            }
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
}
