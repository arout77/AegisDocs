namespace ReceiptCapture.Core.Pdf;

/// <summary>
/// One word for the invisible text layer, in PDF points measured from the TOP-LEFT of the page
/// (the writer flips it into PDF's bottom-left coordinates).
/// </summary>
public sealed record PdfTextWord(string Text, double Left, double Top, double Width, double Height);
