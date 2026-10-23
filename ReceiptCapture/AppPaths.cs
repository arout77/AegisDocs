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
}
