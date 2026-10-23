using System.Text.Json.Serialization;

namespace ReceiptCapture.Core.Meetings;

public enum MeetingStatus
{
    /// <summary>Being recorded right now (or the app closed unexpectedly before it could finish).</summary>
    Recording,
    Completed,
    /// <summary>The app closed unexpectedly mid-recording; the audio up to that point was kept.</summary>
    Recovered,
}

/// <summary>One recorded meeting: its title, notes and where the audio lives. Saved as meeting.json next to the audio.</summary>
public sealed class Meeting : ObservableObject
{
    private string _title = "";
    private string _notes = "";
    private double _durationSeconds;
    private MeetingStatus _status = MeetingStatus.Completed;
    private bool _hasTranscript;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public string AudioFileName { get; set; } = "audio.wav";
    public bool IncludedComputerAudio { get; set; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value ?? "");
    }

    public string Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value ?? "");
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            if (SetProperty(ref _durationSeconds, value))
                OnPropertyChanged(nameof(DurationText));
        }
    }

    public MeetingStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(IsRecording));
                OnPropertyChanged(nameof(IsRecovered));
            }
        }
    }

    /// <summary>True once a transcript has been made and saved for this meeting.</summary>
    public bool HasTranscript
    {
        get => _hasTranscript;
        set
        {
            if (SetProperty(ref _hasTranscript, value))
                OnPropertyChanged(nameof(NoTranscript));
        }
    }

    [JsonIgnore] public bool NoTranscript => !HasTranscript;
    [JsonIgnore] public string DurationText => MeetingFormat.Duration(TimeSpan.FromSeconds(DurationSeconds));
    [JsonIgnore] public bool IsRecording => Status == MeetingStatus.Recording;
    [JsonIgnore] public bool IsRecovered => Status == MeetingStatus.Recovered;

    /// <summary>A new meeting that is about to start recording.</summary>
    public static Meeting StartNew(DateTime now, bool includedComputerAudio) => new()
    {
        StartedAt = now,
        Title = MeetingFormat.DefaultTitle(now),
        IncludedComputerAudio = includedComputerAudio,
        Status = MeetingStatus.Recording,
    };
}
