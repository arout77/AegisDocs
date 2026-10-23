using System.IO;
using System.Windows;
using System.Windows.Controls;
using ReceiptCapture.Core.Meetings;
using ReceiptCapture.ViewModels;

namespace ReceiptCapture;

public partial class MeetingsView : UserControl
{
    private readonly MeetingsViewModel _vm;

    public MeetingsView()
    {
        InitializeComponent();

        _vm = new MeetingsViewModel(new JsonMeetingStore(AppPaths.MeetingsDir))
        {
            ShowMessage = text => Dialogs.Info(this, text),
            Confirm = text => Dialogs.Confirm(this, text),
        };

        DataContext = _vm;

        // Pick up a microphone that was plugged in since the tab was last shown.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) _vm.RefreshMicrophones();
        };
    }

    private void CopyTranscript_Click(object sender, RoutedEventArgs e)
    {
        var export = _vm.GetTranscriptExport();
        if (export == null) return;

        try
        {
            Clipboard.SetText(export.Value.Text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another program was using the clipboard at that instant.
            Dialogs.Info(this, "Couldn't copy to the clipboard just now. Please try again.");
        }
    }

    private void SaveTranscript_Click(object sender, RoutedEventArgs e)
    {
        var export = _vm.GetTranscriptExport();
        if (export == null) return;

        var safeName = string.Concat(export.Value.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save transcript",
            Filter = "Text file (*.txt)|*.txt",
            FileName = safeName + " - transcript.txt",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, export.Value.Text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Info(this, "Couldn't save the file: " + ex.Message);
        }
    }

    /// <summary>True while a meeting is being recorded (the window asks before closing).</summary>
    public bool IsRecording => _vm.IsRecording;

    /// <summary>Stops and saves the meeting in progress, so closing the app never loses a recording.</summary>
    public void StopForExit() => _vm.StopForExit();
}
