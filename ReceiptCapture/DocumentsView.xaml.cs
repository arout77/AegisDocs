using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReceiptCapture.ViewModels;

namespace ReceiptCapture;

public partial class DocumentsView : UserControl
{
    private readonly DocumentsViewModel _vm;

    public DocumentsView()
    {
        InitializeComponent();

        _vm = new DocumentsViewModel
        {
            ShowMessage = text => Dialogs.Info(this, text),

            PickFiles = () =>
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Choose PDFs",
                    Multiselect = true,
                    Filter = "PDF files|*.pdf|All files|*.*",
                };
                return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileNames : null;
            },

            PickFolder = () =>
            {
                var dialog = new OpenFolderDialog { Title = "Choose where results are saved" };
                return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FolderName : null;
            },
        };

        DataContext = _vm;
    }

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
            await _vm.AddFilesAsync(paths);
        }
        catch (Exception ex)
        {
            Dialogs.Info(this, ex.Message);
        }
    }
}
