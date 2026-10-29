using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using ReceiptCapture.Core.Storage;
using ReceiptCapture.Services;
using ReceiptCapture.ViewModels;

namespace ReceiptCapture;

public partial class ReceiptsView : UserControl
{
    private readonly MainViewModel _vm;

    public ReceiptsView()
    {
        InitializeComponent();

        _vm = new MainViewModel(new JsonReceiptStore(AppPaths.DataFile, AppVault.Instance), new WindowsOcrService())
        {
            ShowMessage = text => Dialogs.Info(this, text),
            Confirm = text => Dialogs.Confirm(this, text),

            PickFiles = () =>
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Choose receipts or invoices",
                    Multiselect = true,
                    Filter = "Receipts and invoices|*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
                };
                return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileNames : null;
            },

            PickExportPath = () =>
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Export receipts",
                    Filter = "Excel workbook (*.xlsx)|*.xlsx|CSV file (*.csv)|*.csv",
                    FileName = $"receipts-{DateTime.Today:yyyy-MM-dd}",   // the extension follows the type chosen
                };
                return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
            },
        };

        DataContext = _vm;
    }

    // Drag-and-drop. "Preview" events fire before child controls (like text boxes) can swallow the drop.
    private void View_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void View_PreviewDrop(object sender, DragEventArgs e)
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
            Dialogs.Info(this, ex.Message);
        }
    }

    // Ctrl+S saves. First push whatever is typed in the focused box into the data, then save.
    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
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
