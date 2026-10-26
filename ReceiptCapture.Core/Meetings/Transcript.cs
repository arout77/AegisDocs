namespace ReceiptCapture.Core.Meetings;

/// <summary>
/// One stretch of speech: when it started and ended (in seconds from the start of the recording), what was said,
/// and who said it (null until someone labels it).
/// </summary>
public sealed record TranscriptSegment(double StartSeconds, double EndSeconds, string Text, string? Speaker = null);

/// <summary>The written-out text of a meeting, saved as transcript.json next to the audio.</summary>
public sealed class Transcript
{
    public List<TranscriptSegment> Segments { get; set; } = new();

    /// <summary>The speaker names for this meeting, in the order they were added. Used for the quick-pick list.</summary>
    public List<string> Speakers { get; set; } = new();

    /// <summary>Which speech model produced it (handy to know if the model is ever changed).</summary>
    public string Model { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
