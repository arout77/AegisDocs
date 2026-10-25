using ReceiptCapture.Core.HiddenContent;
using ReceiptCapture.Core.Redaction;

namespace ReceiptCapture.Core.Compliance;

public enum FindingSeverity { High, Medium, Low }

/// <summary>One thing the scan found. The value is already partly hidden (see <see cref="MatchMasker"/>).</summary>
public sealed record Finding(
    int Page,                    // 1-based
    int Line,                    // 1-based, counted on the page as the text reader saw it
    SensitiveKind Kind,
    FindingSeverity Severity,
    string Description,          // e.g. "Card number (Visa)"
    string MaskedValue,
    string? Area = null)         // set for text that isn't on a page ("Comments", "Document properties"); Page is 0 then
{
    /// <summary>Where it was found, in words: "Page 3, line 12" or "Comments".</summary>
    public string Location => Area ?? $"Page {Page}, line {Line}";
}

public enum ScanOutcome { Scanned, Skipped }

/// <summary>What the scan made of one file.</summary>
public sealed class DocumentScanResult
{
    private readonly string? _displayName;

    public DocumentScanResult(string filePath, ScanOutcome outcome, string? sha256 = null, int pageCount = 0,
                              int pagesWithoutText = 0, IReadOnlyList<Finding>? findings = null, string? note = null,
                              IReadOnlyList<HiddenItem>? hiddenItems = null, string? displayName = null)
    {
        FilePath = filePath;
        Outcome = outcome;
        Sha256 = sha256;
        PageCount = pageCount;
        PagesWithoutText = pagesWithoutText;
        Findings = findings ?? Array.Empty<Finding>();
        Note = note;
        HiddenItems = hiddenItems ?? Array.Empty<HiddenItem>();
        _displayName = displayName;
    }

    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>What to call the file in lists and reports: its path inside the chosen folder, or just its name.</summary>
    public string DisplayName => string.IsNullOrEmpty(_displayName) ? FileName : _displayName;
    public ScanOutcome Outcome { get; }
    public string? Sha256 { get; }
    public int PageCount { get; }

    /// <summary>Pages where the text reader found no words at all (blank pages, or pictures it couldn't read).</summary>
    public int PagesWithoutText { get; }
    public IReadOnlyList<Finding> Findings { get; }

    /// <summary>Metadata, comments, tracked changes, attachments and the like stored in the file but not shown on its pages.</summary>
    public IReadOnlyList<HiddenItem> HiddenItems { get; }

    /// <summary>For a skipped file, why. For a scanned one, a caution worth showing next to the result.</summary>
    public string? Note { get; }

    /// <summary>One line for the file list. Deliberately never says "passed", "clean" or "compliant".</summary>
    public string StatusText
    {
        get
        {
            if (Outcome == ScanOutcome.Skipped)
                return "Not scanned" + (string.IsNullOrEmpty(Note) ? "" : ": " + Note);

            int found = Findings.Count, hidden = HiddenItems.Count;
            if (found == 0 && hidden == 0) return "Nothing found by this scan";

            var parts = new List<string>();
            if (found > 0) parts.Add(found == 1 ? "1 item to review" : $"{found} items to review");
            if (hidden > 0) parts.Add(hidden == 1 ? "1 hidden-content item" : $"{hidden} hidden-content items");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Everything one run of the scanner produced, ready to be written out as a report.</summary>
public sealed class ScanReport
{
    public ScanReport(ComplianceProfile profile, IReadOnlyList<DocumentScanResult> documents,
                      DateTimeOffset generatedAt, string appVersion, int customTermCount, bool stoppedEarly = false,
                      bool checkedHiddenContent = false)
    {
        StoppedEarly = stoppedEarly;
        CheckedHiddenContent = checkedHiddenContent;
        Profile = profile;
        Documents = documents;
        GeneratedAt = generatedAt;
        AppVersion = appVersion;
        CustomTermCount = customTermCount;
    }

    public ComplianceProfile Profile { get; }
    public IReadOnlyList<DocumentScanResult> Documents { get; }
    public DateTimeOffset GeneratedAt { get; }
    public string AppVersion { get; }
    public int CustomTermCount { get; }

    /// <summary>True if the person stopped the scan before every file was done; the report says so.</summary>
    public bool StoppedEarly { get; }

    /// <summary>True if this run also looked for hidden content (metadata, comments, attachments...).</summary>
    public bool CheckedHiddenContent { get; }

    public int TotalFindings => Documents.Sum(d => d.Findings.Count);
    public int TotalHiddenItems => Documents.Sum(d => d.HiddenItems.Count);
    public int FilesScanned => Documents.Count(d => d.Outcome == ScanOutcome.Scanned);
    public int FilesSkipped => Documents.Count(d => d.Outcome == ScanOutcome.Skipped);
}
