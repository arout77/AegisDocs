namespace ReceiptCapture.Core.Pdf;

/// <summary>
/// Cheap check for "is this PDF encrypted?" without a PDF library: encrypted files always name an
/// /Encrypt entry in their trailer. Used to tell "needs a password" apart from "damaged" when the
/// Windows PDF renderer simply says the file couldn't be opened.
/// </summary>
public static class PdfEncryptionProbe
{
    private static readonly byte[] Token = "/Encrypt"u8.ToArray();

    public static bool IsEncrypted(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return IsEncrypted(stream);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static bool IsEncrypted(Stream stream)
    {
        const int ChunkSize = 1 << 20;
        const int Overlap = 64;                    // enough to hold the token plus what follows it
        var buffer = new byte[ChunkSize + Overlap];
        int carried = 0;

        while (true)
        {
            int read = stream.Read(buffer, carried, ChunkSize);
            if (read <= 0) break;
            int length = carried + read;

            if (ContainsEncryptEntry(buffer.AsSpan(0, length))) return true;

            // Keep the tail so a token split across two chunks is still found.
            carried = Math.Min(Overlap, length);
            Buffer.BlockCopy(buffer, length - carried, buffer, 0, carried);
        }
        return false;
    }

    private static bool ContainsEncryptEntry(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            int found = data[offset..].IndexOf(Token);
            if (found < 0) return false;

            int next = offset + found + Token.Length;
            // What follows must look like a value: "/Encrypt 12 0 R" or "/Encrypt<< ... >>".
            // That rules out look-alikes such as "/EncryptMetadata".
            int i = next;
            while (i < data.Length && IsWhite(data[i])) i++;
            if (i < data.Length && (i > next || data[i] == (byte)'<') && (data[i] == (byte)'<' || IsDigit(data[i])))
                return true;

            offset = next;
        }
        return false;
    }

    private static bool IsWhite(byte b) => b is 0x20 or 0x0A or 0x0D or 0x09 or 0x0C or 0x00;
    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';
}
