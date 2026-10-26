using System.Text.RegularExpressions;

namespace ReceiptCapture.Core.Meetings;

/// <summary>
/// Putting names to the voices in a transcript. The speech engine only writes down what was said, not who said it, so
/// the person labels the lines (or accepts a suggestion made from the recording, see <see cref="SpeakerActivityLabeler"/>).
/// </summary>
public static class SpeakerLabels
{
    public const int MaxNameLength = 60;

    /// <summary>Tidies a name: trims it, collapses runs of spaces, drops control characters. Returns null for a blank name.</summary>
    public static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var cleaned = new string(name.Where(c => !char.IsControl(c) || char.IsWhiteSpace(c)).ToArray());
        cleaned = Regex.Replace(cleaned.Trim(), @"\s+", " ");
        if (cleaned.Length > MaxNameLength) cleaned = cleaned[..MaxNameLength].TrimEnd();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>Every speaker name, once each (ignoring capitals): the saved list first, then any used on a line.</summary>
    public static List<string> Names(Transcript transcript)
    {
        var names = new List<string>();
        void Add(string? raw)
        {
            var name = Normalize(raw);
            if (name != null && !names.Any(n => Same(n, name))) names.Add(name);
        }

        foreach (var name in transcript.Speakers) Add(name);
        foreach (var segment in transcript.Segments) Add(segment.Speaker);
        return names;
    }

    /// <summary>
    /// Gives the lines at <paramref name="indexes"/> this speaker, or clears their label if <paramref name="speaker"/> is blank.
    /// A name that matches an existing one apart from capitals reuses the existing spelling. Returns how many lines changed.
    /// </summary>
    public static int Assign(Transcript transcript, IEnumerable<int> indexes, string? speaker)
    {
        var name = Normalize(speaker);
        if (name != null) name = Names(transcript).FirstOrDefault(n => Same(n, name)) ?? name;

        int changed = 0;
        foreach (var index in indexes.Distinct())
        {
            if (index < 0 || index >= transcript.Segments.Count) continue;

            var segment = transcript.Segments[index];
            if (string.Equals(segment.Speaker, name, StringComparison.Ordinal)) continue;

            transcript.Segments[index] = segment with { Speaker = name };
            changed++;
        }

        if (name != null) Remember(transcript, name);
        return changed;
    }

    /// <summary>
    /// Renames a speaker everywhere. If the new name is already someone else in this meeting the two are merged,
    /// which is also how to fix a typo. Returns how many lines changed.
    /// </summary>
    public static int Rename(Transcript transcript, string? from, string? to)
    {
        var oldName = Normalize(from);
        var newName = Normalize(to);
        if (oldName == null || newName == null) return 0;
        if (!Names(transcript).Any(n => Same(n, oldName))) return 0;

        // Merge into an existing different speaker if there is one (keeping that spelling).
        newName = Names(transcript).FirstOrDefault(n => Same(n, newName) && !Same(n, oldName)) ?? newName;

        int changed = 0;
        for (int i = 0; i < transcript.Segments.Count; i++)
        {
            var segment = transcript.Segments[i];
            if (segment.Speaker == null || !Same(segment.Speaker, oldName)) continue;
            if (!string.Equals(segment.Speaker, newName, StringComparison.Ordinal))
            {
                transcript.Segments[i] = segment with { Speaker = newName };
                changed++;
            }
        }

        int position = transcript.Speakers.FindIndex(n => Same(n, oldName));
        transcript.Speakers.RemoveAll(n => Same(n, oldName) || Same(n, newName));
        transcript.Speakers.Insert(Math.Min(Math.Max(position, 0), transcript.Speakers.Count), newName);
        return changed;
    }

    /// <summary>Removes the label from every line that has it, keeping the name in the quick-pick list only if <paramref name="keepName"/>.</summary>
    public static int Clear(Transcript transcript, string? speaker, bool keepName = false)
    {
        var name = Normalize(speaker);
        if (name == null) return 0;

        int changed = 0;
        for (int i = 0; i < transcript.Segments.Count; i++)
        {
            var segment = transcript.Segments[i];
            if (segment.Speaker == null || !Same(segment.Speaker, name)) continue;
            transcript.Segments[i] = segment with { Speaker = null };
            changed++;
        }

        if (!keepName) transcript.Speakers.RemoveAll(n => Same(n, name));
        return changed;
    }

    /// <summary>How many lines have no speaker yet.</summary>
    public static int UnlabeledCount(Transcript transcript) => transcript.Segments.Count(s => s.Speaker == null);

    private static void Remember(Transcript transcript, string name)
    {
        if (!transcript.Speakers.Any(n => Same(n, name))) transcript.Speakers.Add(name);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
