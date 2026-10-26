using System.IO;
using System.Windows;
using System.Windows.Controls;
using ReceiptCapture.Core.Meetings;
using ReceiptCapture.Core.Security;
using ReceiptCapture.ViewModels;

namespace ReceiptCapture;

public partial class MeetingsView : UserControl
{
    private readonly MeetingsViewModel _vm;

    public MeetingsView()
    {
        InitializeComponent();

        _vm = new MeetingsViewModel(new JsonMeetingStore(AppPaths.MeetingsDir, AppVault.Instance))
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

    private void SaveWord_Click(object sender, RoutedEventArgs e)
    {
        var meeting = _vm.Selected;
        if (meeting == null || !meeting.HasTranscript) return;

        // Notes are often private to the person taking them, so ask rather than assume.
        bool includeNotes = _vm.SelectedHasNotes &&
                            Dialogs.Confirm(this, "Include your notes for this meeting in the Word file?");

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save transcript as a Word file",
            Filter = "Word document (*.docx)|*.docx",
            FileName = SafeFileName(meeting.Title) + " - transcript.docx",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            _vm.SaveWordFile(dialog.FileName, includeNotes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Info(this, "Couldn't save the file: " + ex.Message);
        }
    }

    private void SaveAudio_Click(object sender, RoutedEventArgs e)
    {
        var meeting = _vm.Selected;
        if (meeting == null) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save a copy of the recording",
            Filter = "Audio file (*.wav)|*.wav",
            FileName = SafeFileName(meeting.Title) + ".wav",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            _vm.ExportAudio(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultException)
        {
            Dialogs.Info(this, "Couldn't save the recording: " + ex.Message);
        }
    }

    // ---- speakers ----

    private List<int> SelectedLineIndexes()
        => TranscriptList.SelectedItems.OfType<TranscriptLine>().Select(l => l.Index).OrderBy(i => i).ToList();

    private void ApplySpeaker_Click(object sender, RoutedEventArgs e)
    {
        var indexes = SelectedLineIndexes();
        if (indexes.Count == 0)
        {
            Dialogs.Info(this, "Select one or more lines in the transcript first.");
            return;
        }

        if (SpeakerLabels.Normalize(_vm.SpeakerInput) == null)
        {
            Dialogs.Info(this, "Type a name, or pick one from the list, then press Apply.");
            return;
        }

        var next = _vm.AssignSpeaker(indexes, _vm.SpeakerInput);

        // One line at a time is the common case, so move on to the next line ready for the next label.
        if (indexes.Count == 1 && next >= 0)
        {
            TranscriptList.SelectedIndex = next;
            TranscriptList.ScrollIntoView(TranscriptList.SelectedItem);
        }
    }

    private void ClearSpeaker_Click(object sender, RoutedEventArgs e)
    {
        var indexes = SelectedLineIndexes();
        if (indexes.Count == 0)
        {
            Dialogs.Info(this, "Select the lines whose speaker you want to clear first.");
            return;
        }

        _vm.AssignSpeaker(indexes, null);
    }

    private void RenameSpeaker_Click(object sender, RoutedEventArgs e)
    {
        var current = TranscriptList.SelectedItems.OfType<TranscriptLine>().FirstOrDefault(l => l.HasSpeaker)?.Speaker;
        if (current == null)
        {
            Dialogs.Info(this, "Select a line that has the speaker you want to rename.");
            return;
        }

        var name = Dialogs.AskText(this,
            $"Rename \"{current}\" everywhere in this transcript. If you pick a name that's already used, the two are combined.",
            current);
        if (name != null) _vm.RenameSpeaker(current, name);
    }

    private void SuggestSpeakers_Click(object sender, RoutedEventArgs e) => _vm.SuggestSpeakers();

    private static string SafeFileName(string title)
        => string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));

    /// <summary>True while a meeting is being recorded (the window asks before closing).</summary>
    public bool IsRecording => _vm.IsRecording;

    /// <summary>Stops and saves the meeting in progress, so closing the app never loses a recording.</summary>
    public void StopForExit() => _vm.StopForExit();
}
