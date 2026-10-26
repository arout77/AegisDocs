
namespace ReceiptCapture.Core.Updates;

/// <summary>Raised when an update can't be trusted or completed. The message is written for the person using the program.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException(string message) : base(message) { }
    public UpdateException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Describes the newest release: what it is, where to download it and the fingerprint (SHA-256) the download must match.
/// The publisher signs this (see <see cref="SignedManifest"/>), so the download address and fingerprint can't be swapped
/// by anyone who doesn't hold the signing key.
/// </summary>
public sealed record UpdateManifest(
    string Version,
    string InstallerUrl,
    string Sha256,
    long SizeBytes,
    string? Notes = null,
    bool Mandatory = false,
    DateTimeOffset? ReleasedAt = null)
{
    /// <summary>The largest installer the program will download (the speech model makes the installer a few hundred MB).</summary>
    public const long MaxInstallerBytes = 2L * 1024 * 1024 * 1024;

    public const int MaxNotesLength = 4000;

    /// <summary>Throws <see cref="UpdateException"/> if any field is missing or unsafe.</summary>
    public UpdateManifest Validated()
    {
        if (!AppVersion.TryParse(Version, out _))
            throw new UpdateException("The update information has a version number the program can't read.");

        if (!Uri.TryCreate(InstallerUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new UpdateException("The update information doesn't have a secure (https) download address.");

        if (Sha256 is null || Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit))
            throw new UpdateException("The update information doesn't have a valid file fingerprint.");

        if (SizeBytes <= 0 || SizeBytes > MaxInstallerBytes)
            throw new UpdateException("The update information has an unreasonable file size.");

        var notes = Notes?.Trim();
        if (notes != null && notes.Length > MaxNotesLength) notes = notes[..MaxNotesLength];

        return this with { Sha256 = Sha256.ToLowerInvariant(), Notes = string.IsNullOrEmpty(notes) ? null : notes };
    }

    /// <summary>The name used for the downloaded installer. Built from the version only, never from the web address.</summary>
    public string InstallerFileName
    {
        get
        {
            AppVersion.TryParse(Version, out var v);
            return "AegisDocs-Setup-" + v.ToString(4) + ".exe";
        }
    }
}
