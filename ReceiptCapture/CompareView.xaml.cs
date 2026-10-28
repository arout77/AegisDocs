using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReceiptCapture.Services;
using ReceiptCapture.ViewModels;

namespace ReceiptCapture;

public partial class CompareView : UserControl
{
    private readonly CompareViewModel _vm;

    public CompareView()
    {
        InitializeComponent();

        _vm = new CompareViewModel(new WindowsRedactionService())
        {
            ShowMessage = text => Dialogs.Info(this, text),

            AskPassword = (fileName, wasWrong) => Dialogs.AskPassword(this, fileName, wasWrong),

            PickFile = () =>
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Choose a document",
                    Filter = "Documents|*.pdf;*.docx;*.txt|PDF files|*.pdf|Word documents|*.docx|Text files|*.txt",
                };
                return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
            },

            PickSavePath = suggestedName =>
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Save the report",
                    Filter = "Web page (opens in any browser)|*.html",
                    FileName = suggestedName,
                };
                return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
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

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _vm.AddPaths(paths);
    }
}
