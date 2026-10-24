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
    string MaskedValue);

public enum ScanOutcome { Scanned, Skipped }

/// <summary>What the scan made of one file.</summary>
public sealed class DocumentScanResult
{
    public DocumentScanResult(string filePath, ScanOutcome outcome, string? sha256 = null, int pageCount = 0,
                              int pagesWithoutText = 0, IReadOnlyList<Finding>? findings = null, string? note = null)
    {
        FilePath = filePath;
        Outcome = outcome;
        Sha256 = sha256;
        PageCount = pageCount;
        PagesWithoutText = pagesWithoutText;
        Findings = findings ?? Array.Empty<Finding>();
        Note = note;
    }

    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);
    public ScanOutcome Outcome { get; }
    public string? Sha256 { get; }
    public int PageCount { get; }

    /// <summary>Pages where the text reader found no words at all (blank pages, or pictures it couldn't read).</summary>
    public int PagesWithoutText { get; }
    public IReadOnlyList<Finding> Findings { get; }

    /// <summary>For a skipped file, why. For a scanned one, a caution worth showing next to the result.</summary>
    public string? Note { get; }

    /// <summary>One line for the file list. Deliberately never says "passed", "clean" or "compliant".</summary>
    public string StatusText => Outcome == ScanOutcome.Skipped
        ? "Not scanned" + (string.IsNullOrEmpty(Note) ? "" : ": " + Note)
        : Findings.Count == 0
            ? "Nothing found by this scan"
            : Findings.Count == 1 ? "1 item to review" : $"{Findings.Count} items to review";
}

/// <summary>Everything one run of the scanner produced, ready to be written out as a report.</summary>
public sealed class ScanReport
{
    public ScanReport(ComplianceProfile profile, IReadOnlyList<DocumentScanResult> documents,
                      DateTimeOffset generatedAt, string appVersion, int customTermCount)
    {
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

    public int TotalFindings => Documents.Sum(d => d.Findings.Count);
    public int FilesScanned => Documents.Count(d => d.Outcome == ScanOutcome.Scanned);
    public int FilesSkipped => Documents.Count(d => d.Outcome == ScanOutcome.Skipped);
}
