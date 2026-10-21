using System.Windows;
using System.Windows.Threading;

namespace ReceiptCapture;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        AppTheme.Initialize();   // pick light/dark before the first window appears
        base.OnStartup(e);
    }

    // Last line of defence: log the error and keep the app (and the user's data) alive.
    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.AppendAllText(AppPaths.LogFile, $"[{DateTime.Now:u}] {e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // If even logging fails there's nothing more to do.
        }

        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were saved to:\n{AppPaths.LogFile}",
            "Receipt Capture", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
