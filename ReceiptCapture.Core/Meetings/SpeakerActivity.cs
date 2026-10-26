namespace ReceiptCapture.Core.Meetings;

/// <summary>
/// How loud the microphone and the computer's sound were, every half second of a recording. It is kept so that, for a
/// call recorded with computer sound, lines can be labelled "You" or "Other participants" without any extra software.
/// It holds levels only (no sound), and is saved next to the recording as activity.json.
/// </summary>
public sealed class SpeakerActivity
{
    public const double WindowSeconds = 0.5;

    /// <summary>One value per half second, 0 (silent) to 255 (loudest).</summary>
    public List<byte> Mic { get; set; } = new();
    public List<byte> Computer { get; set; } = new();

    /// <summary>Turns a loudest-sample reading (0 to 1) into a stored byte. The square root stretches quiet speech.</summary>
    public static byte Quantize(float peak)
        => (byte)Math.Round(Math.Clamp(Math.Sqrt(Math.Clamp(peak, 0f, 1f)), 0.0, 1.0) * 255.0);

    /// <summary>The loudest-sample reading (0 to 1) a stored byte stands for.</summary>
    public static double Level(byte stored)
    {
        double v = stored / 255.0;
        return v * v;
    }

    public void Add(float micPeak, float computerPeak)
    {
        Mic.Add(Quantize(micPeak));
        Computer.Add(Quantize(computerPeak));
    }

    public int Count => Math.Min(Mic.Count, Computer.Count);
}

public sealed record AutoLabelResult(int Labeled, int Unclear);

/// <summary>
/// Suggests who spoke each line of a call by comparing the microphone with the computer's sound: if only the microphone
/// was active it was the person at this computer, if only the computer's sound was active it was someone else.
/// When both were active (people talking over each other, or the microphone hearing the speakers) it is left blank
/// rather than guessed. It cannot tell apart several people on the same side, and it does nothing for an in-person
/// meeting, where everyone is on the one microphone.
/// </summary>
public static class SpeakerActivityLabeler
{
    public const string YouLabel = "You";
    public const string OthersLabel = "Other participants";

    private const double SilenceLevel = 0.02;   // below this a source counts as silent
    private const double ClearlyLouder = 2.5;   // one source must beat the other by this much to be chosen when both are active

    public static AutoLabelResult Apply(Transcript transcript, SpeakerActivity activity, bool overwrite = false)
    {
        int labeled = 0, unclear = 0;
        int windows = activity.Count;
        if (windows == 0) return new AutoLabelResult(0, 0);

        for (int i = 0; i < transcript.Segments.Count; i++)
        {
            var segment = transcript.Segments[i];
            if (segment.Speaker != null && !overwrite) continue;

            var who = Decide(segment, activity, windows);
            if (who == null)
            {
                unclear++;
                continue;
            }

            transcript.Segments[i] = segment with { Speaker = who };
            labeled++;
        }

        if (labeled > 0)
        {
            foreach (var name in new[] { YouLabel, OthersLabel })
                if (transcript.Segments.Any(s => s.Speaker == name) &&
                    !transcript.Speakers.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                    transcript.Speakers.Add(name);
        }

        return new AutoLabelResult(labeled, unclear);
    }

    private static string? Decide(TranscriptSegment segment, SpeakerActivity activity, int windows)
    {
        int first = (int)Math.Floor(segment.StartSeconds / SpeakerActivity.WindowSeconds);
        int last = (int)Math.Ceiling(segment.EndSeconds / SpeakerActivity.WindowSeconds) - 1;
        first = Math.Max(first, 0);
        last = Math.Min(Math.Max(last, first), windows - 1);
        if (first > last) return null;

        double mic = 0, computer = 0;
        for (int w = first; w <= last; w++)
        {
            mic += SpeakerActivity.Level(activity.Mic[w]);
            computer += SpeakerActivity.Level(activity.Computer[w]);
        }

        int count = last - first + 1;
        mic /= count;
        computer /= count;

        bool micOn = mic >= SilenceLevel;
        bool computerOn = computer >= SilenceLevel;

        if (!micOn && !computerOn) return null;
        if (micOn && !computerOn) return YouLabel;
        if (computerOn && !micOn) return OthersLabel;

        if (mic >= computer * ClearlyLouder) return YouLabel;
        if (computer >= mic * ClearlyLouder) return OthersLabel;
        return null;
    }
}
