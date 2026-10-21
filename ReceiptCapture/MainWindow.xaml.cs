using System.Windows;

namespace ReceiptCapture;

/// <summary>Hosts one tab per tool. Each tool is a self-contained view with its own view model.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
