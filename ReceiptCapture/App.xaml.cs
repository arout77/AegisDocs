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
        Vault.ClearFolder(AppPaths.UpdatesDir);   // installers from an update that has since been installed
        base.OnStartup(e);
        try { Workflows.WorkflowEngine.Instance.Start(); }   // folder watching runs whether or not the tab is ever opened
        catch (Exception ex) { LogStartupProblem("Workflows could not start", ex); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Workflows.WorkflowEngine.Instance.Dispose();
        Vault.ClearFolder(AppPaths.TempDir);
        base.OnExit(e);
    }

    private static void LogStartupProblem(string what, Exception ex)
    {
        try { File.AppendAllText(AppPaths.LogFile, $"[{DateTime.Now:u}] {what}: {ex}{Environment.NewLine}"); } catch (IOException) { }
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
