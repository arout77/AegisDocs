using ReceiptCapture.Core.Redaction;

namespace ReceiptCapture.Core.Compliance;

/// <summary>Runs a profile's checks over the text of a document and turns the hits into findings.</summary>
public static class ComplianceScanner
{
    /// <summary>Scans pages that have already been read (rendered and OCR'd).</summary>
    public static DocumentScanResult ScanPages(
        string filePath, string? sha256, IReadOnlyList<ScannedPage> pages,
        ComplianceProfile profile, IReadOnlyList<string>? customTerms = null)
        => ScanPageTexts(filePath, sha256, pages.Select(p => PageTextBuilder.Build(p.Words).Text).ToList(), profile, customTerms);

    /// <summary>Scans plain page text (page 1 first). Lines are counted by the line breaks in each page's text.</summary>
    public static DocumentScanResult ScanPageTexts(
        string filePath, string? sha256, IReadOnlyList<string> pageTexts,
        ComplianceProfile profile, IReadOnlyList<string>? customTerms = null)
    {
        var options = profile.BuildOptions(customTerms);
        var findings = new List<Finding>();
        int blank = 0;

        for (int i = 0; i < pageTexts.Count; i++)
        {
            var text = pageTexts[i];
            if (string.IsNullOrWhiteSpace(text)) { blank++; continue; }

            foreach (var match in SensitiveDataFinder.Find(text, options))
            {
                var value = text.Substring(match.Start, match.Length);
                findings.Add(new Finding(
                    Page: i + 1,
                    Line: LineOf(text, match.Start),
                    Kind: match.Kind,
                    Severity: match.Kind == SensitiveKind.CustomTerm ? FindingSeverity.Medium : profile.SeverityOf(match.Kind),
                    Description: Describe(match.Kind, value),
                    MaskedValue: MatchMasker.Mask(match.Kind, value)));
            }
        }

        string? note = null;
        if (pageTexts.Count > 0 && blank == pageTexts.Count)
            note = "no readable text was found on any page";
        else if (blank > 0)
            note = blank == 1
                ? "1 page had no readable text, so anything on it was not checked"
                : $"{blank} pages had no readable text, so anything on them was not checked";

        // Highest severity first, then in reading order.
        var ordered = findings.OrderBy(f => f.Severity).ThenBy(f => f.Page).ThenBy(f => f.Line).ToList();
        return new DocumentScanResult(filePath, ScanOutcome.Scanned, sha256, pageTexts.Count, blank, ordered, note);
    }

    /// <summary>A file that was not scanned (needs a password, couldn't be opened...), recorded so the report shows it.</summary>
    public static DocumentScanResult Skipped(string filePath, string reason)
        => new(filePath, ScanOutcome.Skipped, note: reason);

    private static string Describe(SensitiveKind kind, string value)
    {
        var name = SensitiveKindInfo.DisplayName(kind);
        if (kind != SensitiveKind.CreditCard) return name;
        var brand = CardBrand.Detect(value);
        return brand == null ? name : $"{name} ({brand})";
    }

    private static int LineOf(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }
}
