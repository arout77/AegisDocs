using System.Security.Cryptography;

namespace ReceiptCapture.Core.Security;

/// <summary>How thoroughly to overwrite a file before deleting it.</summary>
public sealed record ShredOptions
{
    /// <summary>How many times every byte is overwritten with random data (1 to 35). Three is a sensible default.</summary>
    public int Passes { get; init; } = 3;

    public static ShredOptions Default { get; } = new();
}

/// <summary>Progress while shredding: which file is being worked on, and how many are done out of the total.</summary>
public sealed record ShredProgress(string CurrentPath, int FilesDone, int FilesTotal);

/// <summary>One thing that could not be shredded, and why in plain words.</summary>
public sealed record ShredFailure(string Path, string Reason);

/// <summary>What a shredding run did.</summary>
public sealed record ShredResult(
    int FilesShredded,
    int FoldersRemoved,
    long BytesOverwritten,
    IReadOnlyList<ShredFailure> Failures,
    bool Stopped)
{
    public bool Succeeded => Failures.Count == 0 && !Stopped;
}

/// <summary>
/// Overwrites files with random data, empties them, gives them a meaningless name, and deletes them.
///
/// Be honest about what this can do: it overwrites the file's data where the drive stores it today. On a
/// traditional hard disk that is a strong guarantee. On a solid-state drive (most laptops) the drive may keep
/// older copies of the data in places no program can reach, so full-disk encryption (BitLocker) is the only
/// dependable protection there. Backups, cloud-synced copies, and the Recycle Bin are separate copies this
/// never touches.
/// </summary>
public static class FileShredder
{
    public const int MaxPasses = 35;
    private const int ChunkSize = 1024 * 1024;

    /// <summary>
    /// Shreds every file and folder in <paramref name="paths"/> (folders with everything inside them).
    /// A problem with one file is recorded and the run carries on with the rest.
    /// Stopping through <paramref name="cancel"/> takes effect between files, never in the middle of one,
    /// so a file is never left half overwritten.
    /// </summary>
    public static ShredResult Shred(
        IEnumerable<string> paths,
        ShredOptions? options = null,
        ShredGuard? guard = null,
        IProgress<ShredProgress>? progress = null,
        CancellationToken cancel = default)
    {
        options ??= ShredOptions.Default;
        if (options.Passes < 1 || options.Passes > MaxPasses)
            throw new ArgumentOutOfRangeException(nameof(options), $"Passes must be between 1 and {MaxPasses}.");
        guard ??= ShredGuard.Default;

        var failures = new List<ShredFailure>();
        var files = new List<string>();
        var folders = new List<string>();          // deepest first, filled below
        var links = new List<string>();            // symbolic links / junctions: removed, never followed

        foreach (var raw in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path;
            try { path = Path.GetFullPath(raw); }
            catch (Exception ex) { failures.Add(new ShredFailure(raw, $"Not a usable path ({ex.Message})")); continue; }

            var refusal = guard.Check(path);
            if (refusal != null) { failures.Add(new ShredFailure(path, refusal)); continue; }

            if (IsLink(path)) { links.Add(path); continue; }
            if (File.Exists(path)) { files.Add(path); continue; }
            if (Directory.Exists(path)) { CollectFolder(path, files, folders, links, failures); continue; }

            failures.Add(new ShredFailure(path, "It doesn't exist (it may already be gone)."));
        }

        int total = files.Count;
        int done = 0, shredded = 0, removedFolders = 0;
        long bytes = 0;
        bool stopped = false;

        foreach (var file in files)
        {
            if (cancel.IsCancellationRequested) { stopped = true; break; }
            progress?.Report(new ShredProgress(file, done, total));

            try
            {
                long length = new FileInfo(file).Length;
                ShredOneFile(file, options.Passes);
                bytes += length * options.Passes;
                shredded++;
            }
            catch (Exception ex)
            {
                failures.Add(new ShredFailure(file, Explain(ex)));
            }
            done++;
        }
        progress?.Report(new ShredProgress("", done, total));

        // Links are only unhooked: the thing they point at is somebody else's file.
        foreach (var link in links)
        {
            try
            {
                if (Directory.Exists(link) && !File.Exists(link)) Directory.Delete(link, recursive: false);
                else File.Delete(link);
            }
            catch (Exception ex) { failures.Add(new ShredFailure(link, Explain(ex))); }
        }

        if (!stopped)
        {
            foreach (var folder in folders)
            {
                try
                {
                    if (!Directory.Exists(folder)) continue;
                    var target = RenameAway(folder, isFolder: true);
                    Directory.Delete(target, recursive: false);
                    removedFolders++;
                }
                catch (Exception ex)
                {
                    failures.Add(new ShredFailure(folder, "The folder wasn't removed: " + Explain(ex)));
                }
            }
        }

        return new ShredResult(shredded, removedFolders, bytes, failures, stopped);
    }

    /// <summary>Overwrites a file in place, keeping its size, without deleting it. Mainly for the tests and for <see cref="Shred"/>.</summary>
    public static void OverwriteInPlace(string path, int passes)
    {
        if (passes < 1 || passes > MaxPasses) throw new ArgumentOutOfRangeException(nameof(passes));

        File.SetAttributes(path, FileAttributes.Normal);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        long length = stream.Length;
        if (length == 0) return;

        var buffer = new byte[(int)Math.Min(ChunkSize, length)];
        for (int pass = 0; pass < passes; pass++)
        {
            stream.Position = 0;
            long remaining = length;
            while (remaining > 0)
            {
                int count = (int)Math.Min(buffer.Length, remaining);
                RandomNumberGenerator.Fill(buffer.AsSpan(0, count));
                stream.Write(buffer, 0, count);
                remaining -= count;
            }
            stream.Flush(flushToDisk: true);
        }
    }

    // ------------------------------------------------------------------

    private static void ShredOneFile(string path, int passes)
    {
        OverwriteInPlace(path, passes);

        // Empty it too, so even the length is gone, then give it a meaningless name before deleting it.
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.SetLength(0);
            stream.Flush(flushToDisk: true);
        }

        var renamed = RenameAway(path, isFolder: false);
        File.Delete(renamed);
    }

    /// <summary>Renames to a random name in the same folder so the original file name doesn't linger. Falls back to the old name.</summary>
    private static string RenameAway(string path, bool isFolder)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory)) return path;

            for (int attempt = 0; attempt < 5; attempt++)
            {
                var candidate = Path.Combine(directory, Convert.ToHexString(RandomNumberGenerator.GetBytes(8)));
                if (File.Exists(candidate) || Directory.Exists(candidate)) continue;

                if (isFolder) Directory.Move(path, candidate);
                else File.Move(path, candidate);
                return candidate;
            }
        }
        catch (Exception)
        {
            // A rename is a nicety. If it can't be done the delete still goes ahead under the old name.
        }
        return path;
    }

    private static void CollectFolder(string folder, List<string> files, List<string> foldersDeepestFirst,
        List<string> links, List<ShredFailure> failures)
    {
        var stack = new Stack<string>();
        var found = new List<string>();
        stack.Push(folder);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            found.Add(current);

            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    if (IsLink(entry)) links.Add(entry);
                    else if (Directory.Exists(entry)) stack.Push(entry);
                    else files.Add(entry);
                }
            }
            catch (Exception ex)
            {
                failures.Add(new ShredFailure(current, "Couldn't read this folder: " + Explain(ex)));
            }
        }

        // Longest paths first, so a folder is only removed after everything inside it.
        foldersDeepestFirst.AddRange(found.OrderByDescending(f => f.Length));
    }

    private static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception) { return false; }
    }

    private static string Explain(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Access was denied. Check that you may change this file and that it isn't read-only or locked.",
        DirectoryNotFoundException or FileNotFoundException => "It disappeared while it was being shredded.",
        IOException io when io.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
            => "Another program has it open. Close that program and try again.",
        IOException io => "Windows reported a problem: " + io.Message,
        _ => ex.Message,
    };
}
