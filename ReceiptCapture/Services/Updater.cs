using System.Diagnostics;
using System.Net.Http.Headers;
using ReceiptCapture.Core.Updates;

namespace ReceiptCapture.Services;

/// <summary>
/// Looks for a newer version, downloads it and starts its installer. The rules that make this safe (the signature check,
/// the size and fingerprint check) live in <see cref="UpdateChecker"/>, where they are tested; this class just joins them
/// to the app. It never installs anything on its own: the person always chooses "Install and restart".
/// </summary>
public static class Updater
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    // The only thing this reveals about the person is the program's name and version number (the user agent).
    private static readonly HttpClient Http = CreateClient();

    public static UpdateSettings Settings { get; } = UpdateSettings.Load(AppPaths.UpdateSettingsFile);

    /// <summary>False until an address and a public key have been put in <see cref="UpdateConfig"/>.</summary>
    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(UpdateConfig.ManifestUrl) && !string.IsNullOrWhiteSpace(UpdateConfig.PublicKey);

    /// <summary>A newer version found by the last check, or null.</summary>
    public static UpdateManifest? Available { get; private set; }

    /// <summary>True when a newer version is waiting and the person hasn't chosen to skip it.</summary>
    public static bool IsUpdateWaiting => Available != null && Available.Version != Settings.SkippedVersion;

    /// <summary>Raised after every check, so the window can update its "Updates" button.</summary>
    public static event Action? StateChanged;

    /// <summary>The quiet check at start-up: only if it is turned on and none has been made in the last day.</summary>
    public static async Task CheckInBackgroundAsync()
    {
        if (!IsConfigured || !Settings.AutoCheck || !Settings.IsCheckDue(DateTimeOffset.Now, CheckInterval)) return;
        await CheckAsync();
    }

    public static async Task<UpdateCheckResult> CheckAsync()
    {
        if (!IsConfigured)
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: "Automatic updates aren't set up in this build.");

        UpdateCheckResult result;
        try
        {
            using var timeout = new CancellationTokenSource(CheckTimeout);
            result = await UpdateChecker.CheckAsync(Http, UpdateConfig.ManifestUrl, UpdateConfig.PublicKey, AppInfo.Version, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            result = new UpdateCheckResult(UpdateCheckStatus.Failed, Error: "The update server took too long to answer.");
        }

        if (result.Status != UpdateCheckStatus.Failed)
        {
            Settings.LastCheck = DateTimeOffset.Now;
            SaveSettings();
        }

        Available = result.Status == UpdateCheckStatus.Available ? result.Manifest : null;
        StateChanged?.Invoke();
        return result;
    }

    public static Task<string> DownloadAsync(UpdateManifest manifest, IProgress<double>? progress, CancellationToken cancel)
        => UpdateChecker.DownloadAsync(Http, manifest, AppPaths.UpdatesDir, progress, cancel);

    /// <summary>
    /// Starts the downloaded installer quietly. The caller must close the program straight afterwards. "/UPDATED=1" tells
    /// the installer to reopen the program when it finishes.
    /// </summary>
    public static void LaunchInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo(installerPath, "/SILENT /SP- /CLOSEAPPLICATIONS /NORESTART /UPDATED=1")
        {
            UseShellExecute = true,
        });
    }

    /// <summary>"Skip this version": the start-up indicator stays quiet until something newer appears.</summary>
    public static void Skip(UpdateManifest manifest)
    {
        Settings.SkippedVersion = manifest.Version;
        SaveSettings();
        StateChanged?.Invoke();
    }

    public static void SaveSettings() => Settings.Save(AppPaths.UpdateSettingsFile);

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };   // checks have their own timeout; downloads can be long
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AegisDocs", AppInfo.Version));
        return http;
    }
}
