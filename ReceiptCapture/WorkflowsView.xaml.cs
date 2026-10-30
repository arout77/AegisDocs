using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReceiptCapture.ViewModels;
using ReceiptCapture.Workflows;

namespace ReceiptCapture;

public partial class WorkflowsView : UserControl
{
    public WorkflowsView()
    {
        InitializeComponent();

        DataContext = new WorkflowsViewModel(WorkflowEngine.Instance)
        {
            ShowMessage = text => Dialogs.Info(this, text),
            Confirm = text => Dialogs.Confirm(this, text),
            PickFolder = title =>
            {
                var dialog = new OpenFolderDialog { Title = title };
                return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FolderName : null;
            },
        };
    }
}
