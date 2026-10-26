using System.Text;
using System.Text.RegularExpressions;

namespace ReceiptCapture.Core.Meetings;

/// <summary>One paragraph of a transcript: where it starts, who is speaking (if known) and what they said.</summary>
public sealed record TranscriptTurn(double StartSeconds, string? Speaker, string Text);

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

    /// <summary>
    /// Groups lines into paragraphs: neighbouring lines by the same named speaker become one paragraph, so a long answer
    /// reads as one block. Lines with no speaker stay as they are, one each.
    /// </summary>
    public static List<TranscriptTurn> ToTurns(IEnumerable<TranscriptSegment> segments)
    {
        var turns = new List<TranscriptTurn>();
        foreach (var segment in segments)
        {
            if (turns.Count > 0 && segment.Speaker != null &&
                string.Equals(turns[^1].Speaker, segment.Speaker, StringComparison.OrdinalIgnoreCase))
            {
                var previous = turns[^1];
                turns[^1] = previous with { Text = previous.Text + " " + segment.Text };
            }
            else
            {
                turns.Add(new TranscriptTurn(segment.StartSeconds, segment.Speaker, segment.Text));
            }
        }

        return turns;
    }

    /// <summary>The time in brackets, as in "[0:12]".</summary>
    public static string TimeStamp(double seconds)
        => "[" + MeetingFormat.Duration(TimeSpan.FromSeconds(seconds)) + "]";

    /// <summary>One paragraph per speaker turn, each starting with its time: "[0:12] Dana: Good morning, everyone."</summary>
    public static string ToText(IEnumerable<TranscriptSegment> segments)
    {
        var sb = new StringBuilder();
        foreach (var turn in ToTurns(segments))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(TimeStamp(turn.StartSeconds)).Append(' ');
            if (turn.Speaker != null) sb.Append(turn.Speaker).Append(": ");
            sb.Append(turn.Text);
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

        var speakers = SpeakerLabels.Names(transcript);
        if (speakers.Count > 0) sb.AppendLine("Speakers " + string.Join(", ", speakers));

        sb.AppendLine();
        sb.Append(ToText(transcript.Segments));
        return sb.ToString();
    }
}
