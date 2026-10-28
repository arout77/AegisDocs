namespace ReceiptCapture.Core.Security;

/// <summary>
/// Refuses to shred things that must never be shredded by accident: a whole drive, the Windows or Program Files
/// folders and anything in them, and a person's profile or main document folders themselves (what is inside
/// them may be shredded).
/// </summary>
public sealed class ShredGuard
{
    private readonly List<string> _protectedTrees;
    private readonly List<string> _protectedExact;

    /// <param name="protectedTrees">Folders that are off limits together with everything inside them.</param>
    /// <param name="protectedExact">Folders that can't be shredded as a whole, though files inside them can.</param>
    public ShredGuard(IEnumerable<string> protectedTrees, IEnumerable<string> protectedExact)
    {
        _protectedTrees = protectedTrees.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _protectedExact = protectedExact.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The usual Windows locations.</summary>
    public static ShredGuard Default { get; } = new(
        new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        },
        new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        });

    /// <summary>Returns null if the path may be shredded, or the reason it may not.</summary>
    public string? Check(string fullPath)
    {
        var path = Normalize(fullPath);

        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(root) && string.Equals(Normalize(root), path, StringComparison.OrdinalIgnoreCase))
            return "That's a whole drive. Aegis Docs won't shred a drive.";

        foreach (var tree in _protectedTrees)
        {
            if (IsSameOrInside(path, tree))
                return "That's part of Windows or your installed programs. Shredding it could stop the PC from working.";
            if (IsSameOrInside(tree, path))
                return "That folder contains Windows or your installed programs. Aegis Docs won't shred it.";
        }

        foreach (var exact in _protectedExact)
        {
            if (string.Equals(path, exact, StringComparison.OrdinalIgnoreCase))
                return "That's one of your main folders. Shred the files you want gone inside it instead of the whole folder.";
            if (IsSameOrInside(exact, path))
                return "That folder contains your main folders. Shred the files you want gone inside it instead.";
        }

        return null;
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or lies somewhere inside it.</summary>
    private static bool IsSameOrInside(string path, string folder)
    {
        if (string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
