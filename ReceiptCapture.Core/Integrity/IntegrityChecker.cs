using System.Security.Cryptography;
using ReceiptCapture.Core.Storage;

namespace ReceiptCapture.Core.Integrity;

public sealed record IntegrityProgress(string CurrentPath, int Done, int Total);

public enum VerifyStatus { Matches, Changed, Missing, Added, Unreadable, NotRecorded }

/// <summary>What was found for one file when a folder was checked against a manifest.</summary>
public sealed record VerifyItem(
    string Path, VerifyStatus Status, string? ExpectedSha256, string? ActualSha256, long? ExpectedSize, long? ActualSize, string Detail);

public sealed record VerifyResult(IReadOnlyList<VerifyItem> Items)
{
    public int Count(VerifyStatus status) => Items.Count(i => i.Status == status);
    public int Matching => Count(VerifyStatus.Matches);

    /// <summary>Everything recorded is still there and unchanged, and nothing new has appeared.</summary>
    public bool AllGood => Items.All(i => i.Status is VerifyStatus.Matches or VerifyStatus.NotRecorded);
}

/// <summary>Builds a manifest from a folder and checks a folder against a manifest.</summary>
public static class IntegrityChecker
{
    public const int MaxFiles = 100_000;

    /// <summary>Fingerprints every file in <paramref name="folder"/> (and its sub-folders).</summary>
    /// <param name="exclude">Full paths to leave out, such as the manifest file itself if it is saved inside the folder.</param>
    /// <exception cref="OperationCanceledException">If <paramref name="cancel"/> fires. No partial manifest is returned.</exception>
    public static IntegrityManifest Create(
        string folder, string tool, string createdBy, DateTimeOffset now,
        IEnumerable<string>? exclude = null, string matter = "", string preparedBy = "", string notes = "",
        IProgress<IntegrityProgress>? progress = null, CancellationToken cancel = default)
    {
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"The folder {folder} doesn't exist.");

        var files = ListFiles(folder, exclude);
        var manifest = new IntegrityManifest
        {
            Tool = tool,
            CreatedAt = now,
            CreatedBy = createdBy,
            FolderName = FolderName(folder),
            Matter = matter,
            PreparedBy = preparedBy,
            Notes = notes,
        };

        for (int i = 0; i < files.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var (full, relative) = files[i];
            progress?.Report(new IntegrityProgress(relative, i, files.Count));

            try
            {
                var info = new FileInfo(full);
                manifest.Entries.Add(new ManifestEntry(relative, info.Length, Hash(full), new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                manifest.Entries.Add(new ManifestEntry(relative, 0, "", DateTimeOffset.MinValue, "couldn't be read: " + ex.Message));
            }
        }
        progress?.Report(new IntegrityProgress("", files.Count, files.Count));

        manifest.Fingerprint = manifest.ComputeFingerprint();
        manifest.AddEvent(now, createdBy, "Created", $"{manifest.FileCount} file(s) recorded.");
        return manifest;
    }

    /// <summary>Checks <paramref name="folder"/> against <paramref name="manifest"/>: what matches, changed, vanished or is new.</summary>
    public static VerifyResult Verify(
        IntegrityManifest manifest, string folder, IEnumerable<string>? exclude = null,
        IProgress<IntegrityProgress>? progress = null, CancellationToken cancel = default)
    {
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"The folder {folder} doesn't exist.");

        var present = ListFiles(folder, exclude).ToDictionary(f => f.Relative, StringComparer.OrdinalIgnoreCase);
        var recorded = manifest.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var items = new List<VerifyItem>();

        int done = 0, total = manifest.Entries.Count;
        foreach (var entry in manifest.Entries)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report(new IntegrityProgress(entry.Path, done++, total));

            if (entry.Error != null)
            {
                items.Add(new VerifyItem(entry.Path, VerifyStatus.NotRecorded, null, null, null, null,
                    "It couldn't be read when the list was made, so it was never fingerprinted."));
                continue;
            }

            if (!present.TryGetValue(entry.Path, out var file))
            {
                items.Add(new VerifyItem(entry.Path, VerifyStatus.Missing, entry.Sha256, null, entry.Size, null, "It is no longer in the folder."));
                continue;
            }

            try
            {
                long size = new FileInfo(file.Full).Length;
                if (size != entry.Size)
                {
                    // A different size can't have the same fingerprint, so don't read the whole file to find that out.
                    items.Add(new VerifyItem(entry.Path, VerifyStatus.Changed, entry.Sha256, null, entry.Size, size,
                        $"The size changed from {ByteSize.Format(entry.Size)} to {ByteSize.Format(size)}."));
                    continue;
                }

                var actual = Hash(file.Full);
                items.Add(string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase)
                    ? new VerifyItem(entry.Path, VerifyStatus.Matches, entry.Sha256, actual, entry.Size, size, "Unchanged.")
                    : new VerifyItem(entry.Path, VerifyStatus.Changed, entry.Sha256, actual, entry.Size, size, "The contents changed (same size, different fingerprint)."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                items.Add(new VerifyItem(entry.Path, VerifyStatus.Unreadable, entry.Sha256, null, entry.Size, null, "It couldn't be read now: " + ex.Message));
            }
        }

        foreach (var file in present.Values.OrderBy(f => f.Relative, StringComparer.OrdinalIgnoreCase))
        {
            if (recorded.ContainsKey(file.Relative)) continue;
            long size = 0;
            try { size = new FileInfo(file.Full).Length; } catch (IOException) { }
            items.Add(new VerifyItem(file.Relative, VerifyStatus.Added, null, null, null, size, "It wasn't in the folder when the list was made."));
        }
        progress?.Report(new IntegrityProgress("", total, total));

        return new VerifyResult(items);
    }

    // ------------------------------------------------------------------

    private static List<(string Full, string Relative)> ListFiles(string folder, IEnumerable<string>? exclude)
    {
        var skip = new HashSet<string>((exclude ?? Enumerable.Empty<string>()).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

        // Hidden and system files count too: a list that quietly left them out couldn't show they were changed.
        // Linked folders are not entered, because they could lead anywhere.
        var result = new List<(string, string)>();
        foreach (var full in FolderWalker.EnumerateFiles(folder))
        {
            if (skip.Contains(full)) continue;
            if (result.Count >= MaxFiles)
                throw new InvalidOperationException($"That folder holds more than {MaxFiles:N0} files. Make a manifest for smaller folders.");

            result.Add((full, Path.GetRelativePath(folder, full).Replace('\\', '/')));
        }
        result.Sort((x, y) => StringComparer.OrdinalIgnoreCase.Compare(x.Item2, y.Item2));
        return result;
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string FolderName(string folder)
    {
        var trimmed = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }
}
