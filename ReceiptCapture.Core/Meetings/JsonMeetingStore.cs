using System.Text.Json;

namespace ReceiptCapture.Core.Meetings;

/// <summary>
/// Keeps each meeting in its own folder: root\&lt;id&gt;\meeting.json plus the audio file.
/// One folder per meeting means deleting a meeting is one simple, complete operation.
/// </summary>
public sealed class JsonMeetingStore
{
    private const string MetaFileName = "meeting.json";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _root;

    public JsonMeetingStore(string rootDirectory) => _root = rootDirectory;

    public string FolderFor(Meeting meeting) => Path.Combine(_root, meeting.Id);
    public string AudioPathFor(Meeting meeting) => Path.Combine(FolderFor(meeting), meeting.AudioFileName);
    private string TranscriptPathFor(Meeting meeting) => Path.Combine(FolderFor(meeting), "transcript.json");

    public void SaveTranscript(Meeting meeting, Transcript transcript)
    {
        Directory.CreateDirectory(FolderFor(meeting));
        var path = TranscriptPathFor(meeting);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(transcript, Options));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Returns null if the meeting has no transcript, or the file can't be read.</summary>
    public Transcript? LoadTranscript(Meeting meeting)
    {
        var path = TranscriptPathFor(meeting);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<Transcript>(File.ReadAllText(path), Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Loads every meeting, newest first. Recordings left unfinished by a crash are repaired on the way.</summary>
    public List<Meeting> LoadAll()
    {
        var meetings = new List<Meeting>();
        if (!Directory.Exists(_root)) return meetings;

        foreach (var folder in Directory.EnumerateDirectories(_root))
        {
            var metaPath = Path.Combine(folder, MetaFileName);
            if (!File.Exists(metaPath)) continue;

            Meeting? meeting;
            try
            {
                meeting = JsonSerializer.Deserialize<Meeting>(File.ReadAllText(metaPath), Options);
            }
            catch (JsonException)
            {
                continue;   // leave an unreadable entry alone rather than deleting someone's recording
            }

            if (meeting == null) continue;
            meeting.Id = Path.GetFileName(folder);   // the folder name is the truth

            if (meeting.Status == MeetingStatus.Recording)
                Recover(meeting);

            meetings.Add(meeting);
        }

        return meetings.OrderByDescending(m => m.StartedAt).ToList();
    }

    public void Save(Meeting meeting)
    {
        var folder = FolderFor(meeting);
        Directory.CreateDirectory(folder);

        // Temp file + rename, so a crash mid-write can't leave a half-written meeting.json.
        var path = Path.Combine(folder, MetaFileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(meeting, Options));
        File.Move(temp, path, overwrite: true);
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
