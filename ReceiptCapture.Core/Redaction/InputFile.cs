namespace ReceiptCapture.Core.Redaction;

public enum InputKind { Unsupported, Pdf, Word, Image }

/// <summary>Which kinds of file the Redact tab accepts, and a few sanity checks on them.</summary>
public static class InputFile
{
    public static readonly string[] WordExtensions = { ".docx", ".doc", ".rtf" };
    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    /// <summary>Filter text for the Open dialog.</summary>
    public const string DialogFilter =
        "All supported files|*.pdf;*.docx;*.doc;*.rtf;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff" +
        "|PDF files|*.pdf" +
        "|Word documents|*.docx;*.doc;*.rtf" +
        "|Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff";

    public const string SupportedText = "a PDF, a Word document (.docx, .doc, .rtf) or an image (.png, .jpg, .tif, …)";

    public static InputKind Classify(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return InputKind.Pdf;
        if (WordExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return InputKind.Word;
        if (ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return InputKind.Image;
        return InputKind.Unsupported;
    }

    /// <summary>
    /// A password-protected .docx is not a normal zip file: Word wraps it in an older container
    /// that starts with D0 CF 11 E0. Spotting that lets us say so instead of letting Word hang on a prompt.
    /// </summary>
    public static bool IsEncryptedDocx(string path)
    {
        if (!Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var head = new byte[4];
            return stream.Read(head, 0, 4) == 4 && head[0] == 0xD0 && head[1] == 0xCF && head[2] == 0x11 && head[3] == 0xE0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
