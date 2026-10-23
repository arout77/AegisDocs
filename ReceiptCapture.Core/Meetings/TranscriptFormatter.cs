using System.Text;
using System.Text.RegularExpressions;

namespace ReceiptCapture.Core.Meetings;

public static class TranscriptFormatter
{
    // Whisper writes things like [BLANK_AUDIO], [MUSIC] or (silence) when it hears no speech.
    private static readonly Regex NoiseTag = new(@"^\s*[\[\(][^\]\)]*[\]\)]\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Turns raw speech-to-text output into clean segments: trims spaces, joins nothing, and drops empty pieces and
    /// non-speech tags. "[inaudible]" and "[crosstalk]" are kept on purpose: in a legal or medical record it matters
    /// that something was said but couldn't be made out.
    /// </summary>
    public static List<TranscriptSegment> Clean(IEnumerable<TranscriptSegment> raw)
    {
        var cleaned = new List<TranscriptSegment>();
        foreach (var segment in raw)
        {
            var text = (segment.Text ?? "").Trim();
            if (text.Length == 0) continue;

            if (NoiseTag.IsMatch(text) &&
                !text.Contains("inaudible", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("crosstalk", StringComparison.OrdinalIgnoreCase))
                continue;

            cleaned.Add(segment with { Text = text });
        }

        return cleaned;
    }

    /// <summary>One line per segment, each starting with its time: "[0:12] Good morning, everyone."</summary>
    public static string ToText(IEnumerable<TranscriptSegment> segments)
    {
        var sb = new StringBuilder();
        foreach (var segment in segments)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append('[').Append(MeetingFormat.Duration(TimeSpan.FromSeconds(segment.StartSeconds))).Append("] ")
              .Append(segment.Text);
        }

        return sb.ToString();
    }

    /// <summary>The text for a saved or copied file: a short heading, then the transcript.</summary>
    public static string ToExportText(Meeting meeting, Transcript transcript)
    {
        var sb = new StringBuilder();
        sb.AppendLine(meeting.Title);
        sb.AppendLine("Recorded " + meeting.StartedAt.ToString("f", System.Globalization.CultureInfo.CurrentCulture));
        sb.AppendLine("Length " + meeting.DurationText);
        sb.AppendLine();
        sb.Append(ToText(transcript.Segments));
        return sb.ToString();
    }
}
