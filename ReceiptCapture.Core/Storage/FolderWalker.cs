namespace ReceiptCapture.Core.Storage;

/// <summary>
/// Lists every file under a folder. Unlike the framework's own recursive listing it includes hidden and system files and
/// files that only look like links (such as OneDrive placeholders), so nothing in the folder is silently left out.
/// It never goes into a linked folder (a shortcut or junction), because that could lead anywhere, including in circles.
/// </summary>
public static class FolderWalker
{
    public static IEnumerable<string> EnumerateFiles(string folder)
    {
        var pending = new Stack<string>();
        pending.Push(folder);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            List<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(current, "*", options).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) == 0) pending.Push(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }
}
