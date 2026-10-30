using ReceiptCapture.Core.Security;

namespace ReceiptCapture;

/// <summary>Everything the app writes lives under %LocalAppData%\ReceiptCapture.</summary>
public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReceiptCapture");

    public static string ReceiptsDir => Path.Combine(DataDir, "receipts");
    public static string ModelsDir => Path.Combine(DataDir, "models");
    public static string MeetingsDir => Path.Combine(DataDir, "meetings");
    public static string DataFile => Path.Combine(DataDir, "receipts.json");
    public static string LogFile => Path.Combine(DataDir, "error.log");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    /// <summary>Where downloaded update installers wait. Emptied each time the program starts.</summary>
    public static string UpdatesDir => Path.Combine(DataDir, "updates");

    /// <summary>The person's update choices (check automatically, skipped version, last check).</summary>
    public static string UpdateSettingsFile => Path.Combine(DataDir, "updates.json");

    /// <summary>The secret key that locks the saved data, itself locked to this Windows login.</summary>
    public static string KeyFile => Path.Combine(DataDir, "key.bin");

    /// <summary>Short-lived readable copies of locked files (a receipt being viewed, a recording being played). Emptied at start and exit.</summary>
    public static string TempDir => Path.Combine(DataDir, "temp");

    /// <summary>The saved workflows (names, folders, what to check). Locked, because custom terms can be client names.</summary>
    public static string WorkflowsFile => Path.Combine(DataDir, "workflows.json" + Vault.SealedSuffix);

    /// <summary>Fingerprints of files each workflow has already handled, so a restart doesn't redo them.</summary>
    public static string WorkflowStateFile => Path.Combine(DataDir, "workflow-state.json");
}
