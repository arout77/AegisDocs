using PdfSharp.Fonts;

namespace ReceiptCapture.Services;

/// <summary>
/// Tells PDFsharp where to find fonts. Stamps (Bates numbers, watermarks) are drawn in Arial,
/// which ships with every copy of Windows, so we read it straight from the Fonts folder.
/// </summary>
public sealed class WindowsFontResolver : IFontResolver
{
    private static readonly string FontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        => new FontResolverInfo(isBold ? "arialbd" : "arial");   // any requested family falls back to Arial

    public byte[]? GetFont(string faceName)
    {
        var path = Path.Combine(FontsFolder, faceName + ".ttf");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Couldn't find the font file {path}, which is needed to stamp text on PDFs.");
        return File.ReadAllBytes(path);
    }
}
