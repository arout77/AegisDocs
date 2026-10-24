namespace ReceiptCapture.Core.Pdf;

/// <summary>
/// Thrown when a PDF needs a password to open (or the password given was wrong).
/// The screens catch this one specifically, ask the user for the password and try again.
/// </summary>
public sealed class PdfPasswordException : Exception
{
    public PdfPasswordException(string path, bool wrongPassword)
        : base(wrongPassword
            ? $"{System.IO.Path.GetFileName(path)}: that password didn't work."
            : $"{System.IO.Path.GetFileName(path)} is password-protected.")
    {
        FilePath = path;
        WrongPassword = wrongPassword;
    }

    public string FilePath { get; }

    /// <summary>True when a password was supplied but didn't open the file.</summary>
    public bool WrongPassword { get; }
}
