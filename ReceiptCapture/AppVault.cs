using ReceiptCapture.Core.Security;

namespace ReceiptCapture;

/// <summary>The one vault the whole program uses to lock its saved data.</summary>
public static class AppVault
{
    public static Vault Instance { get; } = new(new DpapiKeyProvider(AppPaths.KeyFile));

    /// <summary>
    /// Unlocks a locked receipt file into a temporary readable copy and returns its path. The temporary copies are
    /// removed when the program starts and when it exits.
    /// </summary>
    public static string OpenToTemp(string sealedPath)
        => Instance.DecryptToTempFile(sealedPath, AppPaths.TempDir, Vault.LogicalExtension(sealedPath));
}
