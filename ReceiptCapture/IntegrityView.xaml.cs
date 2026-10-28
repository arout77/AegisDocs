using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReceiptCapture.ViewModels;

namespace ReceiptCapture;

public partial class IntegrityView : UserControl
{
    private readonly IntegrityViewModel _vm;

    public IntegrityView()
    {
        InitializeComponent();

        _vm = new IntegrityViewModel
        {
            ShowMessage = text => Dialogs.Info(this, text),
            Confirm = text => Dialogs.Confirm(this, text),

            PickFolder = title =>
            {
                var dialog = new OpenFolderDialog { Title = title };
                return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FolderName : null;
            },

            PickManifest = () =>
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Choose an integrity manifest",
                    Filter = "Integrity manifest (*.json)|*.json|All files|*.*",
                };
                return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
            },

            PickSavePath = (title, suggestedName, filter) =>
            {
                var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = suggestedName };
                return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
            },

            CopyToClipboard = text =>
            {
                try { Clipboard.SetText(text); }
                catch (System.Runtime.InteropServices.COMException) { /* another program is holding the clipboard; the person can try again */ }
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

    private void View_PreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            _vm.AddPaths(paths, checkMode: ModeTabs.SelectedIndex == 1);
    }
}
