using System.Reflection;

namespace ReceiptCapture;

/// <summary>Facts about this copy of the program, shared by anything that writes a report or log.</summary>
public static class AppInfo
{
    /// <summary>The version number, such as 0.1.0.</summary>
    public static string Version => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0";
}
