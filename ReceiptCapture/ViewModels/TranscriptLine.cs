using ReceiptCapture.Core;

namespace ReceiptCapture.ViewModels;

/// <summary>One line of a transcript as the Transcript tab shows it: its time, who said it (if known) and the words.</summary>
public sealed class TranscriptLine : ObservableObject
{
    private string? _speaker;

    public TranscriptLine(int index, string timeText, string text, string? speaker)
    {
        Index = index;
        TimeText = timeText;
        Text = text;
        _speaker = speaker;
    }

    /// <summary>Position in the transcript, counting from 0.</summary>
    public int Index { get; }

    public string TimeText { get; }
    public string Text { get; }

    public string? Speaker
    {
        get => _speaker;
        set
        {
            if (SetProperty(ref _speaker, value))
            {
                OnPropertyChanged(nameof(SpeakerText));
                OnPropertyChanged(nameof(HasSpeaker));
                OnPropertyChanged(nameof(NoSpeaker));
            }
        }
    }

    public string SpeakerText => _speaker ?? "";
    public bool HasSpeaker => _speaker != null;
    public bool NoSpeaker => _speaker == null;
}
