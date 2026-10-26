using System.Text.Json;
using ReceiptCapture.Core.Security;

namespace ReceiptCapture.Core.Meetings;

/// <summary>
/// Keeps each meeting in its own folder: root\&lt;id&gt;\meeting.json plus the audio file.
/// One folder per meeting means deleting a meeting is one simple, complete operation.
///
/// With a <see cref="Vault"/> everything in the folder is locked: the meeting's title and notes, the transcript, the
/// speaker activity and the recording (kept as audio.wav.aegis once it is finished). Files saved before locking was
/// added are read as they are and locked the next time they are saved.
/// </summary>
public sealed class JsonMeetingStore
{
    private const string MetaFileName = "meeting.json";
    private const string ActivityFileName = "activity.json";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _root;
    private readonly Vault? _vault;

    public JsonMeetingStore(string rootDirectory, Vault? vault = null)
    {
        _root = rootDirectory;
        _vault = vault;
    }

    /// <summary>How many meetings the last <see cref="LoadAll"/> couldn't open (for example, locked by a different key).</summary>
    public int UnreadableCount { get; private set; }

    public string FolderFor(Meeting meeting) => Path.Combine(_root, meeting.Id);

    /// <summary>Where a recording is written while it is being made. Once finished it is locked and this file goes away.</summary>
    public string AudioPathFor(Meeting meeting) => Path.Combine(FolderFor(meeting), meeting.AudioFileName);

    public string SealedAudioPathFor(Meeting meeting) => AudioPathFor(meeting) + Vault.SealedSuffix;
    private string TranscriptPathFor(Meeting meeting) => Path.Combine(FolderFor(meeting), "transcript.json");
    private string ActivityPathFor(Meeting meeting) => Path.Combine(FolderFor(meeting), ActivityFileName);

    // ---------------------------------------------------------------------------------------------
    // Transcript and speaker activity
    // ---------------------------------------------------------------------------------------------

    public void SaveTranscript(Meeting meeting, Transcript transcript)
    {
        Directory.CreateDirectory(FolderFor(meeting));
        Vault.WriteText(_vault, TranscriptPathFor(meeting), JsonSerializer.Serialize(transcript, Options));
    }

    /// <summary>Returns null if the meeting has no transcript, or the file can't be read.</summary>
    public Transcript? LoadTranscript(Meeting meeting)
    {
        var path = TranscriptPathFor(meeting);
        if (!File.Exists(path)) return null;
        try
        {
            var text = Vault.ReadText(_vault, path, out var wasEncrypted);
            var transcript = JsonSerializer.Deserialize<Transcript>(text, Options);
            if (transcript != null && _vault != null && !wasEncrypted) SaveTranscript(meeting, transcript);   // lock an older file
            return transcript;
        }
        catch (JsonException) { return null; }
        catch (VaultException) { return null; }
    }

    public void SaveActivity(Meeting meeting, SpeakerActivity activity)
    {
        Directory.CreateDirectory(FolderFor(meeting));
        Vault.WriteText(_vault, ActivityPathFor(meeting), JsonSerializer.Serialize(activity, Options));
    }

    public bool HasActivity(Meeting meeting) => File.Exists(ActivityPathFor(meeting));

    public SpeakerActivity? LoadActivity(Meeting meeting)
    {
        var path = ActivityPathFor(meeting);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<SpeakerActivity>(Vault.ReadText(_vault, path, out _), Options);
        }
        catch (JsonException) { return null; }
        catch (VaultException) { return null; }
    }

    // ---------------------------------------------------------------------------------------------
    // Recordings
    // ---------------------------------------------------------------------------------------------

    /// <summary>True if the recording exists, locked or not.</summary>
    public bool AudioExists(Meeting meeting) => File.Exists(AudioPathFor(meeting)) || File.Exists(SealedAudioPathFor(meeting));

    /// <summary>
    /// Locks a finished recording: makes the locked copy, proves it opens, and only then deletes the plain file.
    /// Does nothing without a vault, or if there is no plain file. Returns true if it locked something.
    /// </summary>
    public bool SealAudio(Meeting meeting)
    {
        if (_vault == null) return false;

        var plain = AudioPathFor(meeting);
        if (!File.Exists(plain)) return false;

        var sealedPath = SealedAudioPathFor(meeting);
        _vault.EncryptFile(plain, sealedPath);
        try
        {
            _vault.Verify(sealedPath);
        }
        catch (VaultException)
        {
            File.Delete(sealedPath);   // keep the plain recording rather than risk losing it
            throw;
        }

        File.Delete(plain);
        return true;
    }

    /// <summary>
    /// A path to a playable copy of the recording. If it is locked, it is unlocked into <paramref name="tempDirectory"/>
    /// and <paramref name="isTemporary"/> is true: delete it with <see cref="ReleaseAudio"/> when done.
    /// </summary>
    public string OpenAudioForUse(Meeting meeting, string tempDirectory, out bool isTemporary)
    {
        var plain = AudioPathFor(meeting);
        if (File.Exists(plain))
        {
            isTemporary = false;
            return plain;
        }

        var sealedPath = SealedAudioPathFor(meeting);
        if (_vault == null || !File.Exists(sealedPath))
            throw new FileNotFoundException("The audio file for this meeting couldn't be found.");

        isTemporary = true;
        return _vault.DecryptToTempFile(sealedPath, tempDirectory, Path.GetExtension(meeting.AudioFileName));
    }

    public static void ReleaseAudio(string path, bool isTemporary)
    {
        if (!isTemporary) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Saves a readable copy of the recording to <paramref name="destination"/>.</summary>
    public void ExportAudio(Meeting meeting, string destination)
    {
        var plain = AudioPathFor(meeting);
        if (File.Exists(plain))
        {
            File.Copy(plain, destination, overwrite: true);
            return;
        }

        var sealedPath = SealedAudioPathFor(meeting);
        if (_vault == null || !File.Exists(sealedPath))
            throw new FileNotFoundException("The audio file for this meeting couldn't be found.");

        _vault.DecryptFile(sealedPath, destination);
    }

    // ---------------------------------------------------------------------------------------------
    // Meetings
    // ---------------------------------------------------------------------------------------------

    /// <summary>Loads every meeting, newest first. Recordings left unfinished by a crash are repaired on the way.</summary>
    public List<Meeting> LoadAll()
    {
        UnreadableCount = 0;
        var meetings = new List<Meeting>();
        if (!Directory.Exists(_root)) return meetings;

        foreach (var folder in Directory.EnumerateDirectories(_root))
        {
            var metaPath = Path.Combine(folder, MetaFileName);
            if (!File.Exists(metaPath)) continue;

            Meeting? meeting;
            bool wasEncrypted;
            try
            {
                var text = Vault.ReadText(_vault, metaPath, out wasEncrypted);
                meeting = JsonSerializer.Deserialize<Meeting>(text, Options);
            }
            catch (JsonException)
            {
                continue;   // leave an unreadable entry alone rather than deleting someone's recording
            }
            catch (VaultException)
            {
                UnreadableCount++;
                continue;
            }

            if (meeting == null) continue;
            meeting.Id = Path.GetFileName(folder);   // the folder name is the truth

            if (meeting.Status == MeetingStatus.Recording)
                Recover(meeting);
            else if (_vault != null && !wasEncrypted)
                Save(meeting);   // lock an older file

            meetings.Add(meeting);
        }

        return meetings.OrderByDescending(m => m.StartedAt).ToList();
    }

    public void Save(Meeting meeting)
    {
        var folder = FolderFor(meeting);
        Directory.CreateDirectory(folder);

        // Temp file + rename, so a crash mid-write can't leave a half-written meeting.json.
        Vault.WriteText(_vault, Path.Combine(folder, MetaFileName), JsonSerializer.Serialize(meeting, Options));
    }

    public void Delete(Meeting meeting)
    {
        var folder = FolderFor(meeting);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    // The app was closed (or crashed) while this meeting was being recorded: keep the audio that made it to disk.
    private void Recover(Meeting meeting)
    {
        var audio = AudioPathFor(meeting);
        var length = File.Exists(audio) ? WavInfo.GetDuration(audio) : null;

        if (length.HasValue) meeting.DurationSeconds = length.Value.TotalSeconds;
        meeting.Status = MeetingStatus.Recovered;
        Save(meeting);
    }
}
