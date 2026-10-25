using ReceiptCapture.Core.Redaction;

namespace ReceiptCapture.Core.HiddenContent;

/// <summary>Picks the right inspector for a file by its type. A file that can't be inspected says so; it is never reported as clean.</summary>
public static class HiddenContentInspector
{
    public static HiddenInspection Inspect(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            switch (ext)
            {
                case ".docx":
                    if (InputFile.IsEncryptedDocx(path))
                        return HiddenInspection.NotChecked("the Word file is password-protected, so its hidden content can't be read (remove the password and scan again)");
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        return DocxInspector.Inspect(stream);

                case ".pdf":
                    return PdfInspector.Inspect(path);

                case ".jpg":
                case ".jpeg":
                case ".png":
                    return ImageMetadata.Inspect(File.ReadAllBytes(path), ext);

                case ".doc":
                case ".rtf":
                    return HiddenInspection.NotChecked("older Word formats (.doc, .rtf) can't be checked for hidden content: save the file as .docx first");

                default:
                    return HiddenInspection.NotChecked("hidden content isn't checked for this type of file");
            }
        }
        catch (InvalidDataException)
        {
            return HiddenInspection.NotChecked("the file is damaged or isn't what its name says, so its hidden content was not checked");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HiddenInspection.NotChecked("the file could not be read for the hidden-content check");
        }
    }
}
