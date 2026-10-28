using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReceiptCapture.Core.Integrity;

/// <summary>One file in the manifest: where it is, how big, and its SHA-256 fingerprint.</summary>
/// <param name="Path">Path inside the folder, with forward slashes (so a manifest reads the same everywhere).</param>
/// <param name="Error">Set instead of a fingerprint when the file could not be read.</param>
public sealed record ManifestEntry(string Path, long Size, string Sha256, DateTimeOffset Modified, string? Error = null);

/// <summary>One line of the custody log: something that was done with the manifest, by whom, and when.</summary>
public sealed record CustodyEvent(DateTimeOffset At, string By, string Action, string Detail);

/// <summary>
/// A list of the files in a folder with a SHA-256 fingerprint for each. Fingerprints change if even one byte of a file
/// changes, so the list can later show that nothing has been altered, added or removed.
///
/// What it proves and what it doesn't: it proves a file matches (or no longer matches) what was recorded. It does not,
/// by itself, prove WHEN the list was made, because anyone can make a new list. To pin the date, keep the
/// <see cref="Fingerprint"/> somewhere independent (an email, a docket entry, a signed letter).
/// </summary>
public sealed class IntegrityManifest
{
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;
    public string Tool { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public string FolderName { get; set; } = "";
    public string Matter { get; set; } = "";
    public string PreparedBy { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<ManifestEntry> Entries { get; set; } = new();
    public List<CustodyEvent> Events { get; set; } = new();

    /// <summary>The fingerprint of the file list, as saved. After loading, compare it with <see cref="ComputeFingerprint"/>.</summary>
    public string Fingerprint { get; set; } = "";

    [JsonIgnore] public int FileCount => Entries.Count;
    [JsonIgnore] public long TotalBytes => Entries.Sum(e => e.Size);

    /// <summary>True when the saved fingerprint still matches the file list. False means the list was edited after it was saved.</summary>
    [JsonIgnore] public bool FingerprintIsIntact => string.Equals(Fingerprint, ComputeFingerprint(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One SHA-256 over the whole list (path, size and fingerprint of every file, in a fixed order). Notes, names and the custody log
    /// are not part of it, so recording a check later doesn't change it.
    /// </summary>
    public string ComputeFingerprint()
    {
        var sb = new StringBuilder();
        foreach (var e in Entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Path, StringComparer.Ordinal))
            sb.Append(e.Path).Append('|').Append(e.Size).Append('|').Append(e.Sha256).Append('|').Append(e.Error ?? "").Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    public void AddEvent(DateTimeOffset at, string by, string action, string detail)
        => Events.Add(new CustodyEvent(at, by, action, detail));

    // ------------------------------------------------------------------
    // Saving and loading
    // ------------------------------------------------------------------
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson()
    {
        Fingerprint = ComputeFingerprint();
        return JsonSerializer.Serialize(this, Json);
    }

    public void Save(string path) => File.WriteAllText(path, ToJson(), new UTF8Encoding(false));

    public static IntegrityManifest Load(string path)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<IntegrityManifest>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("empty");
            if (manifest.Format > CurrentFormat)
                throw new InvalidDataException(
                    $"{System.IO.Path.GetFileName(path)} was made by a newer version of Aegis Docs. Update the program to read it.");
            if (manifest.Entries == null) throw new InvalidDataException("no entries");
            manifest.Events ??= new();
            return manifest;
        }
        catch (JsonException)
        {
            throw new InvalidDataException(
                $"{System.IO.Path.GetFileName(path)} isn't an Aegis Docs integrity manifest, or it is damaged.");
        }
    }
}
