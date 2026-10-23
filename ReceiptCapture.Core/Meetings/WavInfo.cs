using System.Text;

namespace ReceiptCapture.Core.Meetings;

/// <summary>
/// Works out how long a WAV file is by reading its header. Also copes with a recording that was cut short
/// (the header's length field is stale), so a meeting can be recovered after a crash or power cut.
/// </summary>
public static class WavInfo
{
    public static TimeSpan? GetDuration(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return GetDuration(stream);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static TimeSpan? GetDuration(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (stream.Length < 12) return null;
        if (ReadId(reader) != "RIFF") return null;
        reader.ReadUInt32();
        if (ReadId(reader) != "WAVE") return null;

        uint bytesPerSecond = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            string id = ReadId(reader);
            uint size = reader.ReadUInt32();
            long bodyStart = stream.Position;

            if (id == "fmt ")
            {
                if (size < 16) return null;
                reader.ReadUInt16();              // format
                reader.ReadUInt16();              // channels
                reader.ReadUInt32();              // sample rate
                bytesPerSecond = reader.ReadUInt32();
            }
            else if (id == "data")
            {
                if (bytesPerSecond == 0) return null;
                long remaining = stream.Length - bodyStart;
                // An unfinished file has 0 (or garbage) in the size field: trust what is actually on disk.
                long dataBytes = (size == 0 || size > remaining) ? remaining : size;
                return TimeSpan.FromSeconds(dataBytes / (double)bytesPerSecond);
            }

            long next = bodyStart + size + (size & 1);   // chunks are padded to an even length
            if (next > stream.Length) return null;
            stream.Position = next;
        }

        return null;
    }

    private static string ReadId(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));
}
