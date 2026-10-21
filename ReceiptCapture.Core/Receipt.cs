namespace ReceiptCapture.Core;

/// <summary>One captured receipt or invoice.</summary>
public class Receipt : ObservableObject
{
    private string _vendor = "";
    private DateTime _date = DateTime.Today;
    private decimal _total;
    private decimal? _tax;
    private string _category = Categories.Uncategorized;
    private string _notes = "";
    private bool _needsReview;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Vendor { get => _vendor; set => SetProperty(ref _vendor, value ?? ""); }
    public DateTime Date { get => _date; set => SetProperty(ref _date, value); }
    public decimal Total { get => _total; set => SetProperty(ref _total, value); }
    public decimal? Tax { get => _tax; set => SetProperty(ref _tax, value); }
    public string Category { get => _category; set => SetProperty(ref _category, string.IsNullOrWhiteSpace(value) ? Categories.Uncategorized : value.Trim()); }
    public string Notes { get => _notes; set => SetProperty(ref _notes, value ?? ""); }

    /// <summary>True until the user has looked at the extracted values and saved.</summary>
    public bool NeedsReview { get => _needsReview; set => SetProperty(ref _needsReview, value); }

    /// <summary>Path of the app's own copy of the original file.</summary>
    public string? FilePath { get; set; }

    /// <summary>SHA-256 of the original file; used to refuse duplicates.</summary>
    public string? FileHash { get; set; }

    /// <summary>Everything the OCR engine read, kept so search can use it.</summary>
    public string RawText { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public Receipt Clone()
    {
        var copy = new Receipt { Id = Id };
        copy.CopyFrom(this);
        return copy;
    }

    /// <summary>Copies every field except Id from another receipt.</summary>
    public void CopyFrom(Receipt other)
    {
        Vendor = other.Vendor;
        Date = other.Date;
        Total = other.Total;
        Tax = other.Tax;
        Category = other.Category;
        Notes = other.Notes;
        NeedsReview = other.NeedsReview;
        FilePath = other.FilePath;
        FileHash = other.FileHash;
        RawText = other.RawText;
        CreatedAt = other.CreatedAt;
    }
}
