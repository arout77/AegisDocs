using ReceiptCapture.Core.Redaction;

namespace ReceiptCapture.Services;

public interface IRedactionService
{
    /// <summary>
    /// Renders every page to an image and reads the words on it with OCR. Accepts PDFs, Word documents
    /// and pictures. Throws <see cref="ReceiptCapture.Core.Pdf.PdfPasswordException"/> when a PDF needs
    /// a password (or the one given was wrong) so the caller can ask and try again.
    /// </summary>
    Task<List<ScannedPage>> ScanAsync(string path, IProgress<string> progress, int maxPages, string? password = null);

    /// <summary>
    /// Renders and OCRs a PDF again and returns the text of each page. Used to double-check a redacted file and to compare documents.
    /// Throws <see cref="ReceiptCapture.Core.Pdf.PdfPasswordException"/> when the PDF needs a password (or the one given was wrong).
    /// </summary>
    Task<List<string>> ReadPageTextsAsync(string path, IProgress<string> progress, string? password = null);

    /// <summary>Draws opaque black boxes onto the page image and returns it as JPEG bytes.</summary>
    byte[] BurnToJpeg(ScannedPage page, IEnumerable<RedactionRegion> regions);
}
