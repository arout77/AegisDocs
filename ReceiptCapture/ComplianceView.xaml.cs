using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReceiptCapture.Core.Redaction;
using ReceiptCapture.Services;
using ReceiptCapture.ViewModels;

namespace ReceiptCapture;

public partial class ComplianceView : UserControl
{
    private readonly ComplianceViewModel _vm;

    public ComplianceView()
    {
        InitializeComponent();

        _vm = new ComplianceViewModel(new WindowsRedactionService())
        {
            ShowMessage = text => Dialogs.Info(this, text),

            AskPassword = (fileName, wasWrong) => Dialogs.AskPassword(this, fileName, wasWrong),

            Confirm = text => Dialogs.Confirm(this, text),

            PickFolder = title =>
            {
                var dialog = new OpenFolderDialog { Title = title };
                return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FolderName : null;
            },

            PickFiles = () =>
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Choose files to check",
                    Multiselect = true,
                    Filter = InputFile.DialogFilter,
                };
                return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileNames : null;
            },

            PickSavePath = suggestedName =>
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Save the report",
                    Filter = "Web page (opens in any browser)|*.html|Spreadsheet (CSV)|*.csv",
                    FileName = suggestedName,
                };
                return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
            },
        };

        DataContext = _vm;
    }

    // ---------------- drag files onto the tab ----------------
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
            _vm.AddFiles(paths);
    }
}
