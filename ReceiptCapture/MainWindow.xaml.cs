using System.Windows;
using System.Windows.Input;

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

        UpdateThemeButton();
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
