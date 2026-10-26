using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ReceiptCapture.Core.Updates;
using ReceiptCapture.Services;

namespace ReceiptCapture;

/// <summary>The Updates window: this version, "Check now", "Install and restart", and the automatic-check choice.</summary>
public sealed class UpdateDialog : Window
{
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 440, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _notes = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 440, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 8, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    private readonly CheckBox _auto = new() { Content = "Check for updates when Aegis Docs starts", Margin = new Thickness(0, 16, 0, 0) };
    private readonly Button _check = new() { Content = "Check now", MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _install = new() { Content = "Install and restart", MinWidth = 140, Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
    private readonly Button _skip = new() { Content = "Skip this version", MinWidth = 120, Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
    private readonly Button _close = new() { Content = "Close", IsCancel = true, MinWidth = 80 };

    private CancellationTokenSource? _download;
    private bool _busy;

    public UpdateDialog(Window owner)
    {
        Title = "Aegis Docs updates";
        Owner = owner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var heading = new TextBlock { Text = "Aegis Docs " + AppInfo.Version, FontSize = 18, FontWeight = FontWeights.SemiBold };
        var privacy = new TextBlock
        {
            Text = "Checking asks the update server for one small file. Nothing from your documents is sent.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 440,
            Opacity = 0.7,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
            Children = { _check, _install, _skip, _close },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(22),
            Children = { heading, _status, _notes, _progress, privacy, _auto, buttons },
        };

        _auto.IsChecked = Updater.Settings.AutoCheck;
        _auto.Click += (_, _) =>
        {
            Updater.Settings.AutoCheck = _auto.IsChecked == true;
            Updater.SaveSettings();
        };

        _check.Click += async (_, _) => await CheckNowAsync();
        _install.Click += async (_, _) => await InstallAsync();
        _skip.Click += (_, _) =>
        {
            if (Updater.Available is { } manifest) Updater.Skip(manifest);
            Close();
        };

        Closing += OnClosing;
        Loaded += (_, _) => ShowCurrentState();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_busy) _download?.Cancel();   // closing the window cancels a download in progress
    }

    private void ShowCurrentState()
    {
        if (!Updater.IsConfigured)
        {
            _status.Text = "Automatic updates aren't set up in this build.";
            _check.IsEnabled = false;
            _auto.IsEnabled = false;
            return;
        }

        if (Updater.Available is { } manifest) ShowAvailable(manifest);
        else _status.Text = "Press \"Check now\" to look for a new version.";
    }

    private void ShowAvailable(UpdateManifest manifest)
    {
        _status.Text = $"Version {manifest.Version} is available. You have version {AppInfo.Version}.";
        _notes.Text = manifest.Notes ?? "";
        _notes.Visibility = string.IsNullOrEmpty(manifest.Notes) ? Visibility.Collapsed : Visibility.Visible;
        _install.Visibility = Visibility.Visible;
        _skip.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _check.IsEnabled = !busy;
        _install.IsEnabled = !busy;
        _skip.IsEnabled = !busy;
        _auto.IsEnabled = !busy;
        _progress.Visibility = busy && _download != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task CheckNowAsync()
    {
        SetBusy(true);
        _status.Text = "Checking...";

        var result = await Updater.CheckAsync();
        SetBusy(false);

        switch (result.Status)
        {
            case UpdateCheckStatus.Available when result.Manifest != null:
                ShowAvailable(result.Manifest);
                break;
            case UpdateCheckStatus.UpToDate:
                _status.Text = "You're up to date.";
                _notes.Visibility = Visibility.Collapsed;
                _install.Visibility = Visibility.Collapsed;
                _skip.Visibility = Visibility.Collapsed;
                break;
            default:
                _status.Text = "Couldn't check for updates. " + result.Error;
                break;
        }
    }

    private async Task InstallAsync()
    {
        if (Updater.Available is not { } manifest) return;

        _download = new CancellationTokenSource();
        _progress.Value = 0;
        SetBusy(true);
        _status.Text = "Downloading the update...";

        string installer;
        try
        {
            installer = await Updater.DownloadAsync(manifest, new Progress<double>(p => _progress.Value = p), _download.Token);
        }
        catch (OperationCanceledException)
        {
            SetBusy(false);   // the window is closing
            return;
        }
        catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException)
        {
            _download = null;
            SetBusy(false);
            _status.Text = "The update wasn't installed. " + ex.Message;
            return;
        }

        _download = null;

        // Installing means closing the program. If a meeting is being recorded, this asks first (and the person can say no).
        if (Owner is MainWindow main && !main.PrepareToClose())
        {
            SetBusy(false);
            _status.Text = "The update is downloaded and checked. Press \"Install and restart\" whenever you're ready.";
            return;
        }

        try
        {
            Updater.LaunchInstaller(installer);
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            SetBusy(false);
            _status.Text = "The installer couldn't be started. " + ex.Message;
        }
    }
}
