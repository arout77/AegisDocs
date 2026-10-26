using System.Security.Cryptography;

namespace ReceiptCapture.Core.Updates;

public enum UpdateCheckStatus { UpToDate, Available, Failed }

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateManifest? Manifest = null, string? Error = null);

/// <summary>
/// Asks the update server whether a newer version exists, and downloads it. It sends nothing about the person or their
/// documents: the check is a plain request for one small file. Everything it receives is checked before it is used.
/// </summary>
public static class UpdateChecker
{
    public const int MaxManifestBytes = 64 * 1024;

    public static async Task<UpdateCheckResult> CheckAsync(
        HttpClient http, string manifestUrl, string publicKey, string currentVersion, CancellationToken cancel = default)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: "Updates aren't set up in this build.");

        if (!AppVersion.TryParse(currentVersion, out _))
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: "This program's own version number couldn't be read.");

        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: $"The update server answered \"{(int)response.StatusCode}\".");

            var text = await ReadLimitedTextAsync(response, cancel).ConfigureAwait(false);
            var manifest = SignedManifest.Open(text, publicKey);

            return AppVersion.IsNewer(manifest.Version, currentVersion)
                ? new UpdateCheckResult(UpdateCheckStatus.Available, manifest)
                : new UpdateCheckResult(UpdateCheckStatus.UpToDate, manifest);
        }
        catch (UpdateException ex)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: ex.Message);
        }
        catch (HttpRequestException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: "The update server couldn't be reached.");
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: "The update server took too long to answer.");
        }
    }

    /// <summary>
    /// Downloads the installer into <paramref name="folder"/> and returns its path. The file only appears under its final name
    /// once its size and SHA-256 fingerprint both match the (signed) manifest; anything else is deleted and refused.
    /// An earlier complete, matching download is reused.
    /// </summary>
    public static async Task<string> DownloadAsync(
        HttpClient http, UpdateManifest manifest, string folder, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        manifest = manifest.Validated();
        Directory.CreateDirectory(folder);

        var finalPath = Path.Combine(folder, manifest.InstallerFileName);
        if (File.Exists(finalPath) && await FileMatchesAsync(finalPath, manifest, cancel).ConfigureAwait(false))
        {
            progress?.Report(1.0);
            return finalPath;
        }

        var partPath = finalPath + ".part";
        try
        {
            using var response = await http.GetAsync(manifest.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"The update download failed (the server answered \"{(int)response.StatusCode}\").");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;

            await using (var source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
            await using (var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancel).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > manifest.SizeBytes)
                        throw new UpdateException("The update download was larger than expected, so it was discarded.");

                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                    progress?.Report((double)total / manifest.SizeBytes);
                }
            }

            if (total != manifest.SizeBytes)
                throw new UpdateException("The update download was incomplete, so it was discarded.");

            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("The update download didn't match its fingerprint, so it was discarded.");

            File.Move(partPath, finalPath, overwrite: true);
            return finalPath;
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException("The update couldn't be downloaded. Check your internet connection and try again.", ex);
        }
        finally
        {
            TryDelete(partPath);   // gone after a successful move; removed after any failure
        }
    }

    private static async Task<bool> FileMatchesAsync(string path, UpdateManifest manifest, CancellationToken cancel)
    {
        try
        {
            if (new FileInfo(path).Length != manifest.SizeBytes) return false;

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var hash = await SHA256.HashDataAsync(stream, cancel).ConfigureAwait(false);
            return string.Equals(Convert.ToHexString(hash), manifest.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static async Task<string> ReadLimitedTextAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancel).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxManifestBytes)
                throw new UpdateException("The update information was larger than expected, so it was ignored.");
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
