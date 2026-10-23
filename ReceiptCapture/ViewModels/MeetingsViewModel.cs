using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using ReceiptCapture.Core;
using ReceiptCapture.Core.Meetings;
using ReceiptCapture.Services;

namespace ReceiptCapture.ViewModels;

/// <summary>Everything the Meetings tab shows: the recorder controls, the list of past meetings and the selected meeting's details.</summary>
public sealed class MeetingsViewModel : ObservableObject, IDisposable
{
    private static bool _consentShownThisRun;

    private readonly JsonMeetingStore _store;
    private readonly MeetingRecorder _recorder = new();
    private readonly DispatcherTimer _timer;
    private readonly HashSet<Meeting> _dirty = new();
    private readonly TranscriptionService _transcriber = new();

    private CancellationTokenSource? _transcribeCts;
    private Meeting? _transcribing;
    private bool _autoTranscribe = true;
    private bool _closing;
    private int _transcriptionPercent;
    private string _transcriptText = "";

    private Meeting? _selected;
    private Meeting? _active;
    private AudioDevice _selectedMicrophone = AudioDevice.Default;
    private bool _includeComputerAudio = true;
    private bool _isPaused;
    private string _timerText = "0:00";
    private string _status = "";
    private double _micLevel;
    private double _computerLevel;
    private int _ticks;

    public MeetingsViewModel(JsonMeetingStore store)
    {
        _store = store;

        try
        {
            foreach (var meeting in _store.LoadAll()) Add(meeting, atTop: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status = "Couldn't read the saved meetings: " + ex.Message;
        }

        if (Meetings.Any(m => m.IsRecovered))
            _status = "A recording that was interrupted last time was recovered. Its audio is kept up to the moment it stopped.";

        Microphones = new ObservableCollection<AudioDevice>(AudioDevices.GetMicrophones());

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => OnTick();

        _recorder.Failed += message => System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (!IsRecording) return;
            StopRecording();
            Status = message;
            ShowMessage?.Invoke(message);
        });

        RecordCommand = new RelayCommand(StartRecording, () => !IsRecording);
        PauseCommand = new RelayCommand(TogglePause, () => IsRecording);
        StopCommand = new RelayCommand(StopRecording, () => IsRecording);
        PlayCommand = new RelayCommand(Play, () => Selected != null && !Selected.IsRecording);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => Selected != null);
        DeleteCommand = new RelayCommand(Delete, () => Selected != null && Selected != _active && Selected != _transcribing);
        TranscribeCommand = new RelayCommand(
            () => { if (Selected != null) _ = TranscribeAsync(Selected); },
            () => Selected != null && !Selected.IsRecording && !IsTranscribing && TranscriptionAvailable);
        CancelTranscriptionCommand = new RelayCommand(() => _transcribeCts?.Cancel(), () => IsTranscribing);
    }

    // ---- hooks the view fills in (keeps dialogs out of the view model) ----
    public Action<string>? ShowMessage { get; set; }
    public Func<string, bool>? Confirm { get; set; }

    // ---- what the screen binds to ----
    public ObservableCollection<Meeting> Meetings { get; } = new();
    public ObservableCollection<AudioDevice> Microphones { get; }

    public Meeting? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(NoSelection));
                LoadTranscriptText();
            }
        }
    }

    public bool HasSelection => Selected != null;
    public bool NoSelection => Selected == null;
    public bool HasMeetings => Meetings.Count > 0;
    public bool NoMeetings => Meetings.Count == 0;

    public AudioDevice SelectedMicrophone
    {
        get => _selectedMicrophone;
        set => SetProperty(ref _selectedMicrophone, value ?? AudioDevice.Default);
    }

    public bool IncludeComputerAudio
    {
        get => _includeComputerAudio;
        set => SetProperty(ref _includeComputerAudio, value);
    }

    public bool IsRecording => _active != null;
    public bool IsIdle => _active == null;

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (SetProperty(ref _isPaused, value))
                OnPropertyChanged(nameof(PauseText));
        }
    }

    public string PauseText => IsPaused ? "Resume" : "Pause";

    public string TimerText
    {
        get => _timerText;
        private set => SetProperty(ref _timerText, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public double MicLevel
    {
        get => _micLevel;
        private set => SetProperty(ref _micLevel, value);
    }

    public double ComputerLevel
    {
        get => _computerLevel;
        private set => SetProperty(ref _computerLevel, value);
    }

    public bool ComputerMeterVisible => _recorder.ComputerAudioActive;

    // ---- transcripts ----

    /// <summary>False when this copy of the program doesn't include the speech model (only the installer does).</summary>
    public bool TranscriptionAvailable { get; } = TranscriptionService.IsAvailable;
    public bool TranscriptionUnavailable => !TranscriptionAvailable;

    /// <summary>Make a transcript on its own as soon as a recording is saved.</summary>
    public bool AutoTranscribe
    {
        get => _autoTranscribe;
        set => SetProperty(ref _autoTranscribe, value);
    }

    public bool IsTranscribing => _transcribing != null;

    public int TranscriptionPercent
    {
        get => _transcriptionPercent;
        private set
        {
            if (SetProperty(ref _transcriptionPercent, value))
                OnPropertyChanged(nameof(TranscriptionLabel));
        }
    }

    public string TranscriptionLabel => _transcribing == null
        ? ""
        : $"Making a transcript of \"{_transcribing.Title}\"…  {TranscriptionPercent}%";

    /// <summary>The selected meeting's transcript, ready to show in the Transcript tab.</summary>
    public string TranscriptText
    {
        get => _transcriptText;
        private set => SetProperty(ref _transcriptText, value);
    }

    /// <summary>Title and full text for saving or copying the selected meeting's transcript, or null if there isn't one.</summary>
    public (string Title, string Text)? GetTranscriptExport()
    {
        if (Selected == null || !Selected.HasTranscript) return null;
        var transcript = _store.LoadTranscript(Selected);
        return transcript == null ? null : (Selected.Title, TranscriptFormatter.ToExportText(Selected, transcript));
    }

    public ICommand TranscribeCommand { get; }
    public ICommand CancelTranscriptionCommand { get; }

    public ICommand RecordCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand DeleteCommand { get; }

    /// <summary>Used by the window when it is closing, so a meeting in progress is never lost.</summary>
    public void StopForExit()
    {
        _closing = true;
        if (IsRecording) StopRecording();
        SaveDirty();
    }

    public void RefreshMicrophones()
    {
        if (IsRecording) return;

        var previous = SelectedMicrophone.Id;
        var fresh = AudioDevices.GetMicrophones();
        Microphones.Clear();
        foreach (var device in fresh) Microphones.Add(device);
        SelectedMicrophone = fresh.FirstOrDefault(d => d.Id == previous) ?? AudioDevice.Default;
    }

    public void Dispose()
    {
        _transcribeCts?.Cancel();
        _timer.Stop();
        _recorder.Dispose();
    }

    // ---------------------------------------------------------------------------------------------
    // Recording
    // ---------------------------------------------------------------------------------------------

    private void StartRecording()
    {
        if (IsRecording) return;

        if (!_consentShownThisRun)
        {
            var proceed = Confirm?.Invoke(
                "Before you record:\n\n" +
                "Tell everyone in the meeting that it is being recorded. Many places (including several US states) " +
                "require everyone's permission to record a conversation.\n\n" +
                "Start recording?") ?? true;
            if (!proceed) return;
            _consentShownThisRun = true;
        }

        if (IsTranscribing)
        {
            var carryOn = Confirm?.Invoke(
                "A transcript is still being made. Recording at the same time can slow the computer down.\n\n" +
                "Start recording anyway?") ?? true;
            if (!carryOn) return;
        }

        var meeting = Meeting.StartNew(DateTime.Now, IncludeComputerAudio);

        try
        {
            _store.Save(meeting);   // creates the meeting's folder; also means a crash leaves something to recover
            _recorder.Start(_store.AudioPathFor(meeting),
                string.IsNullOrEmpty(SelectedMicrophone.Id) ? null : SelectedMicrophone.Id,
                IncludeComputerAudio);
        }
        catch (Exception ex)
        {
            try { _store.Delete(meeting); } catch (Exception) { /* leave it */ }
            Status = "Couldn't start recording.";
            ShowMessage?.Invoke(ex.Message);
            return;
        }

        meeting.IncludedComputerAudio = _recorder.ComputerAudioActive;
        _active = meeting;
        Add(meeting, atTop: true);
        Selected = meeting;

        IsPaused = false;
        TimerText = "0:00";
        _ticks = 0;
        _timer.Start();

        Status = IncludeComputerAudio && !_recorder.ComputerAudioActive
            ? "Recording the microphone only. Windows didn't offer any computer sound to record."
            : _recorder.ComputerAudioActive
                ? "Recording the microphone and the sound from this computer."
                : "Recording the microphone.";

        OnRecordingStateChanged();
    }

    private void TogglePause()
    {
        if (!IsRecording) return;

        if (IsPaused)
        {
            _recorder.Resume();
            IsPaused = false;
            Status = "Recording.";
        }
        else
        {
            _recorder.Pause();
            IsPaused = true;
            Status = "Paused. Nothing is being recorded until you press Resume.";
        }
    }

    private void StopRecording()
    {
        var meeting = _active;
        if (meeting == null) return;

        _timer.Stop();
        TimeSpan length;
        try { length = _recorder.Stop(); }
        catch (Exception ex)
        {
            length = _recorder.Elapsed;
            ShowMessage?.Invoke("The recording ended with a problem: " + ex.Message);
        }

        meeting.DurationSeconds = length.TotalSeconds;
        meeting.Status = MeetingStatus.Completed;
        SaveNow(meeting);

        _active = null;
        IsPaused = false;
        MicLevel = 0;
        ComputerLevel = 0;
        TimerText = MeetingFormat.Duration(length);
        Status = $"Saved \"{meeting.Title}\" ({MeetingFormat.Duration(length)}).";
        OnRecordingStateChanged();

        // Straight on to the transcript, unless the program is closing or there's too little to transcribe.
        if (AutoTranscribe && TranscriptionAvailable && !_closing && _transcribing == null && length.TotalSeconds >= 3)
            _ = TranscribeAsync(meeting);
    }

    // ---------------------------------------------------------------------------------------------
    // Transcripts
    // ---------------------------------------------------------------------------------------------

    private async Task TranscribeAsync(Meeting meeting)
    {
        if (_transcribing != null || !TranscriptionAvailable) return;

        var audio = _store.AudioPathFor(meeting);
        if (!File.Exists(audio))
        {
            ShowMessage?.Invoke("The audio file for this meeting couldn't be found.");
            return;
        }

        _transcribing = meeting;
        _transcribeCts = new CancellationTokenSource();
        var token = _transcribeCts.Token;
        TranscriptionPercent = 0;
        OnTranscribingChanged();
        Status = "Making a transcript. This happens on this PC and can take a while for a long meeting.";

        try
        {
            var progress = new Progress<int>(percent => TranscriptionPercent = Math.Clamp(percent, 0, 100));
            var transcript = await _transcriber.TranscribeAsync(audio, progress, token);
            token.ThrowIfCancellationRequested();

            if (transcript.Segments.Count == 0)
            {
                Status = "No speech was found in this recording.";
            }
            else
            {
                _store.SaveTranscript(meeting, transcript);
                meeting.HasTranscript = true;
                SaveNow(meeting);
                if (Selected == meeting) LoadTranscriptText();
                Status = $"Transcript ready for \"{meeting.Title}\".";
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Transcription cancelled.";
        }
        catch (Exception ex)
        {
            Status = "Couldn't make the transcript.";
            ShowMessage?.Invoke("The transcript couldn't be made: " + ex.Message);
        }
        finally
        {
            _transcribeCts?.Dispose();
            _transcribeCts = null;
            _transcribing = null;
            OnTranscribingChanged();
        }
    }

    private void LoadTranscriptText()
    {
        TranscriptText = "";
        if (Selected is { HasTranscript: true } meeting)
        {
            var transcript = _store.LoadTranscript(meeting);
            if (transcript != null) TranscriptText = TranscriptFormatter.ToText(transcript.Segments);
        }
    }

    private void OnTranscribingChanged()
    {
        OnPropertyChanged(nameof(IsTranscribing));
        OnPropertyChanged(nameof(TranscriptionLabel));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnTick()
    {
        var meeting = _active;
        if (meeting == null) return;

        var elapsed = _recorder.Elapsed;
        meeting.DurationSeconds = elapsed.TotalSeconds;
        TimerText = IsPaused ? "Paused  " + MeetingFormat.Duration(elapsed) : MeetingFormat.Duration(elapsed);

        // Speech is quiet compared with the loudest possible sound, so stretch it a little for the bars.
        // The bars fall back gradually instead of flickering.
        MicLevel = Math.Max(Math.Sqrt(_recorder.TakeMicLevel()), MicLevel * 0.85);
        ComputerLevel = Math.Max(Math.Sqrt(_recorder.TakeComputerLevel()), ComputerLevel * 0.85);

        _ticks++;
        if (_ticks % 20 == 0) SaveDirty();            // every ~2 seconds: typed notes and title edits
        if (_ticks % 100 == 0) SaveNow(meeting);      // every ~10 seconds: also the running length
    }

    private void OnRecordingStateChanged()
    {
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(ComputerMeterVisible));
        CommandManager.InvalidateRequerySuggested();
    }

    // ---------------------------------------------------------------------------------------------
    // Past meetings
    // ---------------------------------------------------------------------------------------------

    private void Play()
    {
        if (Selected == null) return;

        var audio = _store.AudioPathFor(Selected);
        if (!File.Exists(audio))
        {
            ShowMessage?.Invoke("The audio file for this meeting couldn't be found.");
            return;
        }

        try { Process.Start(new ProcessStartInfo(audio) { UseShellExecute = true }); }
        catch (Exception ex) { ShowMessage?.Invoke("Couldn't open the recording: " + ex.Message); }
    }

    private void OpenFolder()
    {
        if (Selected == null) return;

        var audio = _store.AudioPathFor(Selected);
        var folder = _store.FolderFor(Selected);
        try
        {
            if (File.Exists(audio))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{audio}\"") { UseShellExecute = true });
            else if (Directory.Exists(folder))
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowMessage?.Invoke("Couldn't open the folder: " + ex.Message); }
    }

    private void Delete()
    {
        var meeting = Selected;
        if (meeting == null || meeting == _active) return;

        var ok = Confirm?.Invoke(
            $"Delete \"{meeting.Title}\"?\n\nThis permanently removes the recording and your notes for it.") == true;
        if (!ok) return;

        try { _store.Delete(meeting); }
        catch (Exception ex)
        {
            ShowMessage?.Invoke("Couldn't delete it: " + ex.Message);
            return;
        }

        _dirty.Remove(meeting);
        meeting.PropertyChanged -= OnMeetingChanged;
        var index = Meetings.IndexOf(meeting);
        Meetings.Remove(meeting);
        Selected = Meetings.Count == 0 ? null : Meetings[Math.Min(index, Meetings.Count - 1)];
        Status = "Meeting deleted.";
        NotifyListChanged();
    }

    // ---------------------------------------------------------------------------------------------
    // Saving
    // ---------------------------------------------------------------------------------------------

    private void Add(Meeting meeting, bool atTop)
    {
        meeting.PropertyChanged += OnMeetingChanged;
        if (atTop) Meetings.Insert(0, meeting); else Meetings.Add(meeting);
        NotifyListChanged();
    }

    private void NotifyListChanged()
    {
        OnPropertyChanged(nameof(HasMeetings));
        OnPropertyChanged(nameof(NoMeetings));
    }

    // Typing in the notes box changes a property on every keystroke; remember it and save in a batch.
    private void OnMeetingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is Meeting meeting && e.PropertyName is nameof(Meeting.Title) or nameof(Meeting.Notes))
            _dirty.Add(meeting);
    }

    private void SaveDirty()
    {
        foreach (var meeting in _dirty.ToList())
            SaveNow(meeting);
    }

    private void SaveNow(Meeting meeting)
    {
        try
        {
            _store.Save(meeting);
            _dirty.Remove(meeting);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Couldn't save changes to disk: " + ex.Message;
        }
    }
}
