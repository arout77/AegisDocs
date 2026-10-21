using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using ReceiptCapture.Core.Storage;
using ReceiptCapture.Services;
using ReceiptCapture.ViewModels;

namespace ReceiptCapture;

public partial class MainWindow : Window
{
    private const string AppTitle = "Receipt Capture";
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel(new JsonReceiptStore(AppPaths.DataFile), new WindowsOcrService())
        {
            ShowMessage = text =>
                MessageBox.Show(this, text, AppTitle, MessageBoxButton.OK, MessageBoxImage.Information),

            Confirm = text =>
                MessageBox.Show(this, text, AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes,

            PickFiles = () =>
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Choose receipts or invoices",
                    Multiselect = true,
                    Filter = "Receipts and invoices|*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
                };
                return dialog.ShowDialog(this) == true ? dialog.FileNames : null;
            },

            PickExportPath = () =>
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Export receipts",
                    Filter = "CSV file (opens in Excel)|*.csv",
                    FileName = $"receipts-{DateTime.Today:yyyy-MM-dd}.csv",
                };
                return dialog.ShowDialog(this) == true ? dialog.FileName : null;
            },
        };

        DataContext = _vm;
    }

    // Drag-and-drop. "Preview" events fire before child controls (like text boxes) can swallow the drop.
    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        try
        {
            await _vm.ImportFilesAsync(paths);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // Ctrl+S saves. First push whatever is typed in the focused box into the data, then save.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (Keyboard.FocusedElement is TextBox box)
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

            if (_vm.SaveCommand.CanExecute(null))
                _vm.SaveCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ReceiptGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReceiptGrid.SelectedItem != null)
            ReceiptGrid.ScrollIntoView(ReceiptGrid.SelectedItem);
    }
}
