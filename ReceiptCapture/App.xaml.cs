using System.Windows;
using System.Windows.Threading;
using ReceiptCapture.Core.Security;

namespace ReceiptCapture;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        AppTheme.Initialize();   // pick light/dark before the first window appears
        Vault.ClearFolder(AppPaths.TempDir);   // readable copies left behind by a crash or a viewer that was still open
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Vault.ClearFolder(AppPaths.TempDir);
        base.OnExit(e);
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
            "Aegis Docs", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
