using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ReceiptCapture.Core.Meetings;

namespace ReceiptCapture.Services;

/// <summary>
/// Records the microphone, and optionally the sound the computer is playing (the other people on a video call),
/// mixed together into one mono 16 kHz WAV file. That format is small (about 2 MB a minute) and is exactly what
/// speech-to-text wants.
///
/// How it works: each source fills a buffer at its own pace. A background thread asks the mixer for audio at a steady
/// clock rate, so the file stays in step with real time even when the computer is silent (Windows sends no audio
/// at all for the "computer sound" source while nothing is playing).
/// </summary>
public sealed class MeetingRecorder : IDisposable
{
    public const int SampleRate = 16000;

    private WasapiCapture? _mic;
    private WasapiCapture? _computer;
    private PeakMeter? _micMeter;
    private PeakMeter? _computerMeter;
    private MixingSampleProvider? _mixer;
    private WaveFileWriter? _writer;
    private Thread? _pump;
    private volatile bool _stopRequested;
    private volatile bool _paused;
    private long _samplesWritten;
    private SpeakerActivity _activity = new();

    public bool IsRecording => _pump != null;
    public bool IsPaused => _paused;

    /// <summary>True when the computer's sound is being recorded as well as the microphone.</summary>
    public bool ComputerAudioActive { get; private set; }

    /// <summary>How much has been recorded (paused time is not counted).</summary>
    public TimeSpan Elapsed => TimeSpan.FromSeconds(Interlocked.Read(ref _samplesWritten) / (double)SampleRate);

    /// <summary>
    /// How loud the microphone and the computer's sound were, every half second of what was recorded. Used afterwards to
    /// suggest who spoke which line. Only the levels are kept, never any sound.
    /// </summary>
    public SpeakerActivity Activity => _activity;

    /// <summary>Loudest recent microphone level, 0 to 1. Reading it resets it.</summary>
    public float TakeMicLevel() => _micMeter?.TakePeak() ?? 0f;

    /// <summary>Loudest recent computer-sound level, 0 to 1. Reading it resets it.</summary>
    public float TakeComputerLevel() => _computerMeter?.TakePeak() ?? 0f;

    /// <summary>Raised (on a background thread) if a device stops working mid-recording, for example a USB mic is unplugged.</summary>
    public event Action<string>? Failed;

    public void Start(string wavPath, string? microphoneId, bool includeComputerAudio)
    {
        if (_pump != null) throw new InvalidOperationException("A recording is already running.");

        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1)) { ReadFully = true };

        try
        {
            // --- microphone (required) ---
            MMDevice micDevice;
            try
            {
                if (string.IsNullOrEmpty(microphoneId))
                {
                    micDevice = WasapiCapture.GetDefaultCaptureDevice();
                }
                else
                {
                    micDevice = new MMDeviceEnumerator().GetDevice(microphoneId);
                }
            }
            catch (Exception)
            {
                throw new InvalidOperationException(
                    "No microphone was found. Plug one in (or turn it on in Windows sound settings) and try again.");
            }

            _mic = new WasapiCapture(micDevice);
            _micMeter = Attach(_mic, mixer, "microphone");

            // --- computer sound (optional) ---
            ComputerAudioActive = false;
            if (includeComputerAudio)
            {
                try
                {
                    _computer = new WasapiLoopbackCapture();
                    _computerMeter = Attach(_computer, mixer, "computer sound");
                    ComputerAudioActive = true;
                }
                catch (Exception)
                {
                    // No speakers or sound output device: carry on with just the microphone.
                    _computer?.Dispose();
                    _computer = null;
                    _computerMeter = null;
                }
            }

            _mixer = mixer;
            _writer = new WaveFileWriter(wavPath, new WaveFormat(SampleRate, 16, 1));
            _stopRequested = false;
            _paused = false;
            _activity = new SpeakerActivity();
            Interlocked.Exchange(ref _samplesWritten, 0);

            _mic.StartRecording();
            _computer?.StartRecording();

            _pump = new Thread(PumpLoop) { IsBackground = true, Name = "MeetingRecorder" };
            _pump.Start();
        }
        catch (InvalidOperationException)
        {
            Cleanup();
            throw;
        }
        catch (Exception ex)
        {
            Cleanup();
            throw new InvalidOperationException(
                "The recording couldn't start: " + ex.Message +
                "\n\nIf this keeps happening, check that Windows allows apps to use the microphone " +
                "(Settings > Privacy & security > Microphone).", ex);
        }
    }

    public void Pause() => _paused = true;
    public void Resume() => _paused = false;

    /// <summary>Stops recording and finishes the file. Returns how much was recorded.</summary>
    public TimeSpan Stop()
    {
        if (_pump == null) return Elapsed;

        // Stop the devices first so the last moments of sound reach the buffers, then let the pump drain and finish.
        StopCapture(_mic);
        StopCapture(_computer);
        Thread.Sleep(150);

        _stopRequested = true;
        _pump.Join(TimeSpan.FromSeconds(5));
        var elapsed = Elapsed;
        Cleanup();
        return elapsed;
    }

    public void Dispose()
    {
        if (_pump != null) Stop();
        else Cleanup();
    }

    // ---------------------------------------------------------------------------------------------

    private PeakMeter Attach(WasapiCapture capture, MixingSampleProvider mixer, string label)
    {
        var buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            ReadFully = true,                 // hand out silence rather than stalling when no sound has arrived
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(10),
        };

        capture.DataAvailable += (_, e) => buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null && !_stopRequested)
                Failed?.Invoke($"The {label} stopped working ({e.Exception.Message}). The recording was saved up to that point.");
        };

        ISampleProvider samples = buffer.ToSampleProvider();

        // Everything is mixed down to a single channel at 16 kHz.
        int channels = samples.WaveFormat.Channels;
        if (channels == 2)
        {
            samples = samples.ToMono();
        }
        else if (channels > 2)
        {
            var firstChannel = new MultiplexingSampleProvider(new[] { samples }, 1);
            firstChannel.ConnectInputToOutput(0, 0);
            samples = firstChannel;
        }

        if (samples.WaveFormat.SampleRate != SampleRate)
            samples = new WdlResamplingSampleProvider(samples, SampleRate);

        var meter = new PeakMeter(samples);
        mixer.AddMixerInput(meter);
        return meter;
    }

    private void PumpLoop()
    {
        var mixer = _mixer!;
        var writer = _writer!;
        var floats = new float[SampleRate];          // one second at a time
        var bytes = new byte[SampleRate * 2];
        var clock = Stopwatch.StartNew();
        long taken = 0;
        var lastFlush = clock.Elapsed;

        // Every half second of recorded sound, note how loud each source was (see Activity).
        var micMeter = _micMeter;
        var computerMeter = _computerMeter;
        var activity = _activity;
        int samplesPerWindow = (int)(SampleRate * SpeakerActivity.WindowSeconds);
        long activitySamples = 0;

        try
        {
            while (true)
            {
                Thread.Sleep(50);

                long due = (long)(clock.Elapsed.TotalSeconds * SampleRate) - taken;
                while (due > 0)
                {
                    int want = (int)Math.Min(due, floats.Length);
                    int got = mixer.Read(floats, 0, want);
                    if (got <= 0) break;
                    taken += got;
                    due -= got;

                    if (_paused) continue;           // keep the buffers drained, but write nothing

                    for (int i = 0; i < got; i++)
                    {
                        float s = Math.Clamp(floats[i], -1f, 1f);
                        short v = (short)(s * 32767f);
                        bytes[2 * i] = (byte)(v & 0xFF);
                        bytes[2 * i + 1] = (byte)((v >> 8) & 0xFF);
                    }

                    writer.Write(bytes, 0, got * 2);
                    Interlocked.Add(ref _samplesWritten, got);

                    activitySamples += got;
                    while (activitySamples >= samplesPerWindow)
                    {
                        activity.Add(micMeter?.TakeWindowPeak() ?? 0f, computerMeter?.TakeWindowPeak() ?? 0f);
                        activitySamples -= samplesPerWindow;
                    }
                }

                // Update the file's header every couple of seconds so even a crash leaves a playable recording.
                if (clock.Elapsed - lastFlush > TimeSpan.FromSeconds(2))
                {
                    writer.Flush();
                    lastFlush = clock.Elapsed;
                }

                if (_stopRequested) break;
            }
        }
        catch (Exception ex)
        {
            Failed?.Invoke("The recording hit a problem and was stopped: " + ex.Message);
        }
        finally
        {
            try { writer.Dispose(); } catch (Exception) { /* nothing more to do */ }
        }
    }

    private static void StopCapture(WasapiCapture? capture)
    {
        try { capture?.StopRecording(); }
        catch (Exception) { /* already stopped */ }
    }

    private void Cleanup()
    {
        _pump = null;
        _mixer = null;
        _writer = null;
        _micMeter = null;
        _computerMeter = null;
        ComputerAudioActive = false;

        try { _mic?.Dispose(); } catch (Exception) { }
        try { _computer?.Dispose(); } catch (Exception) { }
        _mic = null;
        _computer = null;
    }

    /// <summary>Passes audio through unchanged while remembering the loudest sample, for the level bars.</summary>
    private sealed class PeakMeter : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private volatile float _peak;
        private volatile float _windowPeak;   // like _peak, but read by the activity log instead of the level bars

        public PeakMeter(ISampleProvider source) => _source = source;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            float peak = 0f;
            for (int i = 0; i < read; i++)
            {
                float level = Math.Abs(buffer[offset + i]);
                if (level > peak) peak = level;
            }

            if (peak > _peak) _peak = peak;
            if (peak > _windowPeak) _windowPeak = peak;
            return read;
        }

        public float TakeWindowPeak()
        {
            float p = _windowPeak;
            _windowPeak = 0f;
            return p;
        }

        public float TakePeak()
        {
            float p = _peak;
            _peak = 0f;
            return p;
        }
    }
}
