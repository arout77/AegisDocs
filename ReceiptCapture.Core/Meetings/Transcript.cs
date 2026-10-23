namespace ReceiptCapture.Core.Meetings;

/// <summary>One stretch of speech: when it started and ended (in seconds from the start of the recording) and what was said.</summary>
public sealed record TranscriptSegment(double StartSeconds, double EndSeconds, string Text);

/// <summary>The written-out text of a meeting, saved as transcript.json next to the audio.</summary>
public sealed class Transcript
{
    public List<TranscriptSegment> Segments { get; set; } = new();

    /// <summary>Which speech model produced it (handy to know if the model is ever changed).</summary>
    public string Model { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
