using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using ReceiptCapture.Core;
using ReceiptCapture.Core.Meetings;
using ReceiptCapture.Core.Security;
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
    private Transcript? _transcript;
    private string _speakerInput = "";
    private bool _canSuggestSpeakers;
    private string _speakerSummary = "";

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

        if (_store.UnreadableCount > 0)
            _status = _store.UnreadableCount == 1
                ? "1 saved meeting couldn't be unlocked, so it isn't shown. It hasn't been deleted."
                : $"{_store.UnreadableCount} saved meetings couldn't be unlocked, so they aren't shown. They haven't been deleted.";

        // Recordings made before locking existed, or left plain by a crash, are locked now.
        foreach (var meeting in Meetings) SealQuietly(meeting);

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
            MakeTranscript,
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
                LoadTranscript();
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

    /// <summary>The selected meeting's transcript, one line per stretch of speech, for the Transcript tab.</summary>
    public ObservableCollection<TranscriptLine> Lines { get; } = new();

    /// <summary>The speaker names used in the selected meeting, for the quick-pick list.</summary>
    public ObservableCollection<string> SpeakerNames { get; } = new();

    /// <summary>The name typed or picked in the speaker box.</summary>
    public string SpeakerInput
    {
        get => _speakerInput;
        set => SetProperty(ref _speakerInput, value ?? "");
    }

    /// <summary>True when this meeting was recorded with computer sound, so "You" and "Other participants" can be suggested.</summary>
    public bool CanSuggestSpeakers
    {
        get => _canSuggestSpeakers;
        private set => SetProperty(ref _canSuggestSpeakers, value);
    }

    /// <summary>"12 of 40 lines have a speaker", shown above the list.</summary>
    public string SpeakerSummary
    {
        get => _speakerSummary;
        private set => SetProperty(ref _speakerSummary, value);
    }

    /// <summary>True if the selected meeting has notes (the Word export asks whether to include them).</summary>
    public bool SelectedHasNotes => Selected != null && !string.IsNullOrWhiteSpace(Selected.Notes);

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

        // Keep how loud each side of a call was, so speakers can be suggested later. Levels only, never sound.
        if (meeting.IncludedComputerAudio && _recorder.Activity.Count > 0)
        {
            try { _store.SaveActivity(meeting, _recorder.Activity); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultException) { /* suggestions just won't be offered */ }
        }

        // Straight on to the transcript, unless the program is closing or there's too little to transcribe.
        // Either way the recording ends up locked: right away, or when the transcript is done with it.
        if (AutoTranscribe && TranscriptionAvailable && !_closing && _transcribing == null && length.TotalSeconds >= 3)
            _ = TranscribeAsync(meeting);
        else
            SealQuietly(meeting);

        if (Selected == meeting) UpdateSpeakerState();
    }

    /// <summary>
    /// Locks a finished recording. If that fails the plain recording is kept (never lost) and it is tried again the next
    /// time the program starts. Nothing happens for a meeting that is still being recorded or transcribed.
    /// </summary>
    private void SealQuietly(Meeting meeting)
    {
        if (meeting == _active || meeting == _transcribing) return;

        try { _store.SealAudio(meeting); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultException) { }
    }

    // ---------------------------------------------------------------------------------------------
    // Transcripts
    // ---------------------------------------------------------------------------------------------

    private async Task TranscribeAsync(Meeting meeting)
    {
        if (_transcribing != null || !TranscriptionAvailable) return;

        string audio;
        bool audioIsTemporary;
        try
        {
            audio = _store.OpenAudioForUse(meeting, AppPaths.TempDir, out audioIsTemporary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultException)
        {
            ShowMessage?.Invoke("The recording for this meeting couldn't be opened: " + ex.Message);
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
                if (Selected == meeting) LoadTranscript();
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
            JsonMeetingStore.ReleaseAudio(audio, audioIsTemporary);   // the readable copy of a locked recording
            SealQuietly(meeting);
            OnTranscribingChanged();
        }
    }

    // "Make transcript" / "Redo transcript". A new transcript replaces the old one, including any speaker names.
    private void MakeTranscript()
    {
        var meeting = Selected;
        if (meeting == null) return;

        if (meeting.HasTranscript && _transcript != null && _transcript.Segments.Any(s => s.Speaker != null))
        {
            var ok = Confirm?.Invoke(
                "Making a new transcript replaces the current one, including the speaker names you added.\n\nContinue?") ?? true;
            if (!ok) return;
        }

        _ = TranscribeAsync(meeting);
    }

    private void LoadTranscript()
    {
        _transcript = null;
        Lines.Clear();
        SpeakerNames.Clear();
        SpeakerInput = "";

        if (Selected is { HasTranscript: true } meeting)
        {
            _transcript = _store.LoadTranscript(meeting);
            if (_transcript != null)
            {
                for (int i = 0; i < _transcript.Segments.Count; i++)
                {
                    var segment = _transcript.Segments[i];
                    Lines.Add(new TranscriptLine(i, TranscriptFormatter.TimeStamp(segment.StartSeconds), segment.Text, segment.Speaker));
                }

                SyncSpeakerNames();
            }
        }

        UpdateSpeakerState();
        OnPropertyChanged(nameof(SelectedHasNotes));
    }

    // ---------------------------------------------------------------------------------------------
    // Speakers
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Gives the chosen lines this speaker (or clears them when the name is blank) and saves. Returns the line after the
    /// last one changed, so the screen can move on to it, or -1 if there is none.
    /// </summary>
    public int AssignSpeaker(IReadOnlyList<int> lineIndexes, string? name)
    {
        if (_transcript == null || Selected == null || lineIndexes.Count == 0) return -1;

        SpeakerLabels.Assign(_transcript, lineIndexes, name);
        AfterSpeakerChange();

        int next = lineIndexes.Max() + 1;
        return next < Lines.Count ? next : -1;
    }

    public void RenameSpeaker(string from, string to)
    {
        if (_transcript == null || Selected == null) return;

        SpeakerLabels.Rename(_transcript, from, to);
        AfterSpeakerChange();
    }

    /// <summary>Labels lines "You" and "Other participants" from the recording's sound levels (calls recorded with computer sound).</summary>
    public void SuggestSpeakers()
    {
        if (_transcript == null || Selected == null) return;

        var activity = _store.LoadActivity(Selected);
        if (activity == null || activity.Count == 0)
        {
            ShowMessage?.Invoke("There's no sound-level record for this meeting, so speakers can't be suggested.");
            return;
        }

        var result = SpeakerActivityLabeler.Apply(_transcript, activity);
        AfterSpeakerChange();

        ShowMessage?.Invoke(result.Labeled == 0
            ? "No lines could be labelled this way. Lines you've already labelled are left alone, and lines where both sides were " +
              "talking or the sound was unclear are left blank. You can still label them by hand."
            : $"{result.Labeled} line(s) were labelled \"{SpeakerActivityLabeler.YouLabel}\" or \"{SpeakerActivityLabeler.OthersLabel}\". " +
              $"{result.Unclear} line(s) couldn't be told apart and were left blank.\n\n" +
              "These are suggestions based on whether the microphone or the computer's sound was active. Please check them, " +
              "and rename them to the real names if you like.");
    }

    private void AfterSpeakerChange()
    {
        if (_transcript == null || Selected == null) return;

        try { _store.SaveTranscript(Selected, _transcript); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultException)
        {
            Status = "Couldn't save the speaker names: " + ex.Message;
        }

        for (int i = 0; i < Lines.Count && i < _transcript.Segments.Count; i++)
            Lines[i].Speaker = _transcript.Segments[i].Speaker;

        SyncSpeakerNames();
        UpdateSpeakerState();
    }

    // Keeps the quick-pick list in step without clearing it, so the text typed in the speaker box isn't lost.
    private void SyncSpeakerNames()
    {
        var wanted = _transcript == null ? new List<string>() : SpeakerLabels.Names(_transcript);

        for (int i = SpeakerNames.Count - 1; i >= 0; i--)
            if (!wanted.Contains(SpeakerNames[i])) SpeakerNames.RemoveAt(i);

        foreach (var name in wanted)
            if (!SpeakerNames.Contains(name)) SpeakerNames.Add(name);
    }

    private void UpdateSpeakerState()
    {
        if (_transcript == null || Selected == null)
        {
            CanSuggestSpeakers = false;
            SpeakerSummary = "";
            return;
        }

        CanSuggestSpeakers = Selected.IncludedComputerAudio && _store.HasActivity(Selected);

        int total = _transcript.Segments.Count;
        int named = total - SpeakerLabels.UnlabeledCount(_transcript);
        SpeakerSummary = named == 0
            ? $"{total} lines. Select lines, type or pick a name, then press Apply."
            : $"{named} of {total} lines have a speaker.";
    }

    // ---------------------------------------------------------------------------------------------
    // Saving a transcript as a file
    // ---------------------------------------------------------------------------------------------

    /// <summary>Writes the selected meeting's transcript as a Word file.</summary>
    public void SaveWordFile(string path, bool includeNotes)
    {
        if (Selected == null || _transcript == null) return;
        TranscriptDocx.Write(path, Selected, _transcript, includeNotes);
    }

    /// <summary>Saves a readable copy of the selected meeting's recording (it is kept locked in the program's own folder).</summary>
    public void ExportAudio(string destination)
    {
        if (Selected == null) return;
        _store.ExportAudio(Selected, destination);
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

        // A locked recording is unlocked into a temporary readable copy, which is removed when the program exits.
        try
        {
            var audio = _store.OpenAudioForUse(Selected, AppPaths.TempDir, out _);
            Process.Start(new ProcessStartInfo(audio) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowMessage?.Invoke("Couldn't open the recording: " + ex.Message);
        }
    }

    private void OpenFolder()
    {
        if (Selected == null) return;

        var audio = File.Exists(_store.AudioPathFor(Selected)) ? _store.AudioPathFor(Selected) : _store.SealedAudioPathFor(Selected);
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
