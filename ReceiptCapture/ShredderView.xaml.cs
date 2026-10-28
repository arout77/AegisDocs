using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReceiptCapture.ViewModels;

namespace ReceiptCapture;

public partial class ShredderView : UserControl
{
    private readonly ShredderViewModel _vm;

    public ShredderView()
    {
        InitializeComponent();

        _vm = new ShredderViewModel
        {
            ShowMessage = text => Dialogs.Info(this, text),
            Confirm = text => Dialogs.ConfirmDestructive(this, text),

            PickFiles = () =>
            {
                var dialog = new OpenFileDialog { Title = "Choose files to shred", Multiselect = true, Filter = "All files|*.*" };
                return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileNames : null;
            },

            PickFolder = () =>
            {
                var dialog = new OpenFolderDialog { Title = "Choose a folder to shred" };
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
            await _vm.AddPathsAsync(paths);
        }
        catch (Exception ex)
        {
            Dialogs.Info(this, ex.Message);
        }
    }
}
