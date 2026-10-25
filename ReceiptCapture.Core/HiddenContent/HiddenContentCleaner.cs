using System.Text;
using ReceiptCapture.Core.Redaction;

namespace ReceiptCapture.Core.HiddenContent;

/// <summary>What happened when one file was cleaned (kept for the cleanup log).</summary>
public sealed record CleanupLogEntry(
    string Source, string? Output, string? SourceSha256, string? OutputSha256,
    IReadOnlyList<string> Removed, IReadOnlyList<string> StillThere, string Outcome);

/// <summary>Makes clean copies of Word files and JPEG/PNG pictures. (PDFs are cleaned by rebuilding their pages, which the app does.)</summary>
public static class HiddenContentCleaner
{
    /// <summary>True for the file types cleaned here, by editing the file's own structure.</summary>
    public static bool CanClean(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".docx", StringComparison.OrdinalIgnoreCase) || ImageMetadata.IsSupported(ext);
    }

    /// <summary>Writes a cleaned copy. The source is only read. If anything goes wrong the partly written copy is deleted.</summary>
    public static CleanResult CleanFile(string source, string destination, CleanOptions options)
    {
        var ext = Path.GetExtension(source).ToLowerInvariant();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        try
        {
            if (ext == ".docx")
            {
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
                return DocxCleaner.Clean(input, output, options);
            }

            if (!ImageMetadata.IsSupported(ext)) throw new NotSupportedException("This kind of file can't be cleaned this way.");

            var original = File.ReadAllBytes(source);
            if (!options.PictureMetadata)
            {
                File.WriteAllBytes(destination, original);
                return new CleanResult(Array.Empty<string>());
            }
            var (cleaned, removed) = ImageMetadata.Strip(original, ext);
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
                output.Write(cleaned);
            return new CleanResult(removed, removed.Count == 0 ? "there was no metadata to remove" : null);
        }
        catch
        {
            try { if (File.Exists(destination)) File.Delete(destination); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>
    /// Where a clean copy goes: inside the output folder, keeping the file's folder structure, named "name_CLEAN.ext".
    /// An existing file is never overwritten ("name_CLEAN (2).ext").
    /// </summary>
    public static string CleanCopyPath(string outputFolder, string relativePath, string? newExtension = null)
    {
        var root = Path.GetFullPath(outputFolder);
        var relFolder = Path.GetDirectoryName(relativePath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(relativePath);
        var ext = newExtension ?? Path.GetExtension(relativePath);

        var folder = Path.GetFullPath(Path.Combine(root, relFolder));
        // A relative path that climbs out of the output folder is flattened into it instead.
        if (!folder.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(folder, root, StringComparison.OrdinalIgnoreCase))
            folder = root;

        var candidate = Path.Combine(folder, $"{stem}_CLEAN{ext}");
        for (int n = 2; File.Exists(candidate); n++)
            candidate = Path.Combine(folder, $"{stem}_CLEAN ({n}){ext}");
        return candidate;
    }
}

/// <summary>Writes the record of a cleanup run: which files, what was removed, and what was still there afterwards.</summary>
public static class CleanupLogWriter
{
    public static string ToCsv(IReadOnlyList<CleanupLogEntry> entries, DateTimeOffset when, string appVersion)
    {
        var sb = new StringBuilder();
        sb.Append('\uFEFF');
        sb.AppendLine(string.Join(",", new[] { "Original file", "Clean copy", "Result", "Removed", "Still in the copy", "Original SHA-256", "Copy SHA-256", "Run on", "Aegis Docs version" }
            .Select(Compliance.ScanReportWriter.Cell)));

        var run = when.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var e in entries)
            sb.AppendLine(string.Join(",", new[]
            {
                e.Source, e.Output ?? "", e.Outcome, string.Join("; ", e.Removed), string.Join("; ", e.StillThere),
                e.SourceSha256 ?? "", e.OutputSha256 ?? "", run, appVersion,
            }.Select(Compliance.ScanReportWriter.Cell)));
        return sb.ToString();
    }
}
