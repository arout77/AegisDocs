using ReceiptCapture.Core.Redaction;

namespace ReceiptCapture.Services;

/// <summary>The Windows-specific work behind the Redact tab (kept behind an interface so the view model stays simple).</summary>
public interface IRedactionService
{
    /// <summary>Renders every page to an image and reads the words on it with OCR.</summary>
    Task<List<ScannedPage>> ScanAsync(string path, IProgress<string> progress, int maxPages);

    /// <summary>Renders and OCRs a PDF again and returns the text of each page. Used to double-check a redacted file.</summary>
    Task<List<string>> ReadPageTextsAsync(string path, IProgress<string> progress);

    /// <summary>Draws opaque black boxes onto the page image and returns it as JPEG bytes.</summary>
    byte[] BurnToJpeg(ScannedPage page, IEnumerable<RedactionRegion> regions);
}
