using System.Windows;
using System.Windows.Input;
using ReceiptCapture.Services;

namespace ReceiptCapture;

/// <summary>Hosts one tab per tool. Each tool is a self-contained view with its own view model.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) => AppTheme.ApplyTitleBar(this);
        AppTheme.Changed += OnThemeChanged;
        Closed += (_, _) => AppTheme.Changed -= OnThemeChanged;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F1)
            {
                ShowHelp();
                e.Handled = true;
            }
        };

        Closing += OnClosing;

        // A quiet look for a new version once the window is up. The Updates button changes if one is found.
        Updater.StateChanged += UpdateUpdateButton;
        Closed += (_, _) => Updater.StateChanged -= UpdateUpdateButton;
        Loaded += async (_, _) =>
        {
            try { await Updater.CheckInBackgroundAsync(); }
            catch (Exception) { /* an update check is a nicety: it must never get in the way of the program */ }
        };

        UpdateThemeButton();
        UpdateUpdateButton();
    }

    /// <summary>
    /// Gets the program ready to close so an update can be installed. If a meeting is being recorded, asks first and then
    /// stops and saves it. Returns false if the person said no.
    /// </summary>
    public bool PrepareToClose()
    {
        if (!MeetingsTab.IsRecording) return true;

        if (!Dialogs.Confirm(this, "A meeting is still being recorded.\n\nStop recording, save it, and install the update?"))
            return false;

        MeetingsTab.StopForExit();
        return true;
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e) => new UpdateDialog(this).ShowDialog();

    private void UpdateUpdateButton()
    {
        bool waiting = Updater.IsUpdateWaiting;
        UpdateButton.Content = waiting ? "Update available" : "Updates";
        UpdateButton.Style = (Style)FindResource(waiting ? "PrimaryButton" : "SecondaryButton");
    }

    // Never lose a meeting: if one is being recorded, ask first, then stop and save it before the app closes.
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!MeetingsTab.IsRecording) return;

        if (!Dialogs.Confirm(this, "A meeting is still being recorded.\n\nStop recording, save it, and close Aegis Docs?"))
        {
            e.Cancel = true;
            return;
        }

        MeetingsTab.StopForExit();
    }

    private HelpWindow? _help;

    private void ShowHelp()
    {
        if (_help == null)
        {
            _help = new HelpWindow { Owner = this };
            _help.Closed += (_, _) => _help = null;
            _help.Show();
        }
        else
        {
            if (_help.WindowState == WindowState.Minimized) _help.WindowState = WindowState.Normal;
            _help.Activate();
        }
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e) => ShowHelp();

    private void OnThemeChanged()
    {
        AppTheme.ApplyTitleBar(this);
        UpdateThemeButton();
    }

    private void UpdateThemeButton()
    {
        var label = AppTheme.Choice switch
        {
            ThemeChoice.System => "Auto",
            ThemeChoice.Light => "Light",
            _ => "Dark",
        };
        ThemeButton.Content = $"Theme: {label}";
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e) => AppTheme.Cycle();
}
