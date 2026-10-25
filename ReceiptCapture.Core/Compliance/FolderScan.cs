using ReceiptCapture.Core.Redaction;

namespace ReceiptCapture.Core.Compliance;

/// <summary>A file found in a folder, with the path to show for it (relative to the folder that was chosen).</summary>
public sealed record FolderFile(string FullPath, string RelativePath);

public sealed record FolderListing(IReadOnlyList<FolderFile> Files, int UnsupportedFiles, bool WasCapped);

/// <summary>Finds the files in a folder that the scanner can read.</summary>
public static class FolderScan
{
    public const int DefaultMaxFiles = 2000;

    public static FolderListing Enumerate(string folder, bool includeSubfolders, int maxFiles = DefaultMaxFiles)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = true,                       // a folder we aren't allowed into is skipped, not an error
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        var files = new List<FolderFile>();
        int unsupported = 0;
        bool capped = false;

        foreach (var path in Directory.EnumerateFiles(folder, "*", options).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith("~$", StringComparison.Ordinal)) continue;                  // Word's temporary lock files
            if (InputFile.Classify(path) == InputKind.Unsupported) { unsupported++; continue; }
            if (files.Count >= maxFiles) { capped = true; break; }
            files.Add(new FolderFile(path, Path.GetRelativePath(folder, path)));
        }
        return new FolderListing(files, unsupported, capped);
    }
}
