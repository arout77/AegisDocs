using ReceiptCapture.Core.Meetings;
using Whisper.net;

namespace ReceiptCapture.Services;

/// <summary>
/// Turns a meeting recording into text using Whisper, an open-source speech-to-text model that runs entirely on this PC.
/// The audio never leaves the computer. The model file ships inside the installer, in a "models" folder next to the program.
/// </summary>
public sealed class TranscriptionService
{
    /// <summary>
    /// English-only "small" model, compressed (quantized) to about 180 MB. It is a good balance of accuracy and speed
    /// for meeting speech on an ordinary PC.
    /// </summary>
    public const string ModelFileName = "ggml-small.en-q5_1.bin";

    /// <summary>Where the model file is, or null if this copy of the program doesn't include it.</summary>
    public static string? FindModel()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "models", ModelFileName),   // installed by the installer
            Path.Combine(AppPaths.ModelsDir, ModelFileName),                   // or dropped in the data folder
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static bool IsAvailable => FindModel() != null;

    /// <summary>
    /// Transcribes a recording made by <see cref="MeetingRecorder"/> (16 kHz mono WAV, which is exactly what Whisper wants).
    /// Progress is reported as 0 to 100. Throws OperationCanceledException if cancelled.
    /// </summary>
    public async Task<Transcript> TranscribeAsync(string wavPath, IProgress<int>? progress, CancellationToken cancellation)
    {
        var modelPath = FindModel()
            ?? throw new FileNotFoundException("The speech model isn't part of this copy of the program.");

        try
        {
            return await TranscribeCoreAsync(modelPath, wavPath, progress, cancellation);
        }
        catch (Exception ex) when (IsEngineProblem(ex))
        {
            // Say where the program looked, so a missing-files problem is obvious from the message alone.
            throw new InvalidOperationException(ex.Message + "\n\n" + DescribeEngineFiles(), ex);
        }
    }

    private static bool IsEngineProblem(Exception ex)
        => ex is FileNotFoundException or DllNotFoundException or BadImageFormatException or PlatformNotSupportedException
           || ex.Message.Contains("native", StringComparison.OrdinalIgnoreCase);

    /// <summary>A plain-English line about whether the speech engine files are where the program expects them.</summary>
    public static string DescribeEngineFiles()
    {
        var normal = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "whisper.dll");
        var fallback = Path.Combine(AppContext.BaseDirectory, "runtimes", "noavx", "win-x64", "whisper.dll");
        return "Speech engine files: " +
               (File.Exists(normal) ? "found" : "MISSING") + " at " + normal + "; " +
               "backup set " + (File.Exists(fallback) ? "found" : "MISSING") + " at " + fallback + ".";
    }

    private static async Task<Transcript> TranscribeCoreAsync(
        string modelPath, string wavPath, IProgress<int>? progress, CancellationToken cancellation)
    {
        return await Task.Run(async () =>
        {
            using var factory = WhisperFactory.FromPath(modelPath);

            // Leave a couple of processor cores free so the computer stays usable while it works.
            int threads = Math.Max(1, Environment.ProcessorCount - 2);

            await using var processor = factory.CreateBuilder()
                .WithLanguage("en")
                .WithThreads(threads)
                .WithProgressHandler(percent => progress?.Report(percent))
                .Build();

            await using var audio = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var raw = new List<TranscriptSegment>();
            await foreach (var segment in processor.ProcessAsync(audio, cancellation))
                raw.Add(new TranscriptSegment(segment.Start.TotalSeconds, segment.End.TotalSeconds, segment.Text));

            return new Transcript
            {
                Segments = TranscriptFormatter.Clean(raw),
                Model = ModelFileName,
                CreatedAt = DateTime.Now,
            };
        }, cancellation);
    }
}
