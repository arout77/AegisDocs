using System.Windows;

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

        UpdateThemeButton();
    }

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
