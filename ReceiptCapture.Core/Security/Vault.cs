using System.Security.Cryptography;
using System.Text;

namespace ReceiptCapture.Core.Security;

/// <summary>Where the vault gets its secret key from. The Windows app keeps the key locked to the person's Windows login.</summary>
public interface IKeyProvider
{
    /// <summary>The 32-byte key. A new one is made and kept the first time. Throws <see cref="VaultException"/> if a kept key can't be opened.</summary>
    byte[] GetKey();
}

/// <summary>A key held in memory, for tests.</summary>
public sealed class StaticKeyProvider : IKeyProvider
{
    private readonly byte[] _key;

    public StaticKeyProvider(byte[] key)
    {
        if (key.Length != Vault.KeySize) throw new ArgumentException("The key must be 32 bytes.", nameof(key));
        _key = (byte[])key.Clone();
    }

    public byte[] GetKey() => (byte[])_key.Clone();
}

/// <summary>Raised when something can't be locked or unlocked. The message is written for the person using the program.</summary>
public sealed class VaultException : Exception
{
    public VaultException(string message) : base(message) { }
    public VaultException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Locks and unlocks the program's own saved data (receipts, meetings, transcripts, recordings) so a copied folder,
/// a lost laptop or another Windows user can't read it.
///
/// It uses AES-256-GCM, the standard authenticated cipher: a changed or cut-short file is detected rather than read.
/// Big files (recordings) are handled in 1 MB pieces so they never have to fit in memory. The key itself is kept by an
/// <see cref="IKeyProvider"/>.
///
/// What it does NOT do: it can't protect against someone who is signed in to this Windows account, or against malware
/// running as this person, because the program has to be able to open the data as them.
/// </summary>
public sealed class Vault
{
    public const int KeySize = 32;
    public const string SealedSuffix = ".aegis";

    private const int ChunkSize = 1 << 20;
    private const int MaxChunkSize = 16 << 20;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int PrefixSize = 8;
    private const int HeaderSize = 8 + 4 + PrefixSize;

    // "AEGISv1" and a zero byte. Every locked file starts with these 8 bytes.
    private static readonly byte[] Magic = { (byte)'A', (byte)'E', (byte)'G', (byte)'I', (byte)'S', (byte)'v', (byte)'1', 0 };

    private readonly IKeyProvider _keys;
    private byte[]? _key;

    public Vault(IKeyProvider keys) => _keys = keys;

    // ---------------------------------------------------------------------------------------------
    // Recognising locked data
    // ---------------------------------------------------------------------------------------------

    public static bool IsEncrypted(ReadOnlySpan<byte> start)
        => start.Length >= Magic.Length && start[..Magic.Length].SequenceEqual(Magic);

    public static bool IsEncryptedFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var head = new byte[Magic.Length];
            int read = 0;
            while (read < head.Length)
            {
                int n = stream.Read(head, read, head.Length - read);
                if (n <= 0) break;
                read += n;
            }
            return read == head.Length && IsEncrypted(head);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>True for a path that ends in ".aegis", the name this program gives locked copies of files.</summary>
    public static bool IsSealedPath(string? path)
        => !string.IsNullOrEmpty(path) && path.EndsWith(SealedSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The extension a file really has: "receipt.pdf.aegis" gives ".pdf".</summary>
    public static string LogicalExtension(string path)
    {
        if (IsSealedPath(path)) path = path[..^SealedSuffix.Length];
        return Path.GetExtension(path);
    }

    // ---------------------------------------------------------------------------------------------
    // Bytes and text
    // ---------------------------------------------------------------------------------------------

    public byte[] Encrypt(byte[] data)
    {
        using var input = new MemoryStream(data, writable: false);
        using var output = new MemoryStream();
        Encrypt(input, output);
        return output.ToArray();
    }

    public byte[] Decrypt(byte[] data)
    {
        using var input = new MemoryStream(data, writable: false);
        using var output = new MemoryStream();
        Decrypt(input, output);
        return output.ToArray();
    }

    /// <summary>
    /// Reads a text file that may or may not be locked. Files saved by older versions of the program are plain text;
    /// <paramref name="wasEncrypted"/> tells the caller whether to lock it now.
    /// </summary>
    public static string ReadText(Vault? vault, string path, out bool wasEncrypted)
    {
        var bytes = File.ReadAllBytes(path);
        wasEncrypted = IsEncrypted(bytes);

        if (wasEncrypted)
        {
            if (vault == null) throw new VaultException("This file is locked, but no key is available to open it.");
            bytes = vault.Decrypt(bytes);
        }

        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Writes text (locked if there is a vault), through a temporary file so a crash can't leave half a file.</summary>
    public static void WriteText(Vault? vault, string path, string text)
    {
        var bytes = new UTF8Encoding(false).GetBytes(text);
        if (vault != null) bytes = vault.Encrypt(bytes);

        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    // ---------------------------------------------------------------------------------------------
    // Files
    // ---------------------------------------------------------------------------------------------

    /// <summary>Makes a locked copy of a file. The original is left alone.</summary>
    public void EncryptFile(string source, string destination)
        => TransformFile(source, destination, Encrypt);

    /// <summary>Makes an unlocked copy of a locked file. The locked file is left alone.</summary>
    public void DecryptFile(string source, string destination)
        => TransformFile(source, destination, Decrypt);

    /// <summary>Unlocks a file into a new uniquely named file in <paramref name="tempDirectory"/> and returns its path.</summary>
    public string DecryptToTempFile(string source, string tempDirectory, string extension)
    {
        Directory.CreateDirectory(tempDirectory);
        if (extension.Length > 0 && extension[0] != '.') extension = "." + extension;
        var path = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + extension);

        try
        {
            DecryptFile(source, path);
        }
        catch
        {
            TryDelete(path);
            throw;
        }

        return path;
    }

    /// <summary>Reads a locked file all the way through to prove it can be opened. Throws <see cref="VaultException"/> if not.</summary>
    public void Verify(string sealedPath)
    {
        using var input = new FileStream(sealedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Decrypt(input, Stream.Null);
    }

    /// <summary>Deletes everything in a temporary folder. Files a viewer still has open are skipped.</summary>
    public static void ClearFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder)) TryDelete(file);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void TransformFile(string source, string destination, Action<Stream, Stream> transform)
    {
        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = destination + ".tmp";
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                transform(input, output);

            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------------------------------------
    // The cipher
    //
    // File layout:  "AEGISv1\0" | chunk size (4 bytes) | random prefix (8 bytes) | chunk, chunk, ...
    // Each chunk is its ciphertext followed by a 16-byte tag. A chunk's nonce is the prefix plus its
    // number. The header and a "this is the last chunk" flag are authenticated with every chunk, so
    // cutting a file short, reordering chunks or editing the header is detected.
    // ---------------------------------------------------------------------------------------------

    private void Encrypt(Stream input, Stream output)
    {
        var key = Key();
        long remaining = input.Length - input.Position;

        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BitConverter.TryWriteBytes(header.AsSpan(8, 4), ChunkSize);
        RandomNumberGenerator.Fill(header.AsSpan(12, PrefixSize));
        output.Write(header, 0, header.Length);

        using var aes = new AesGcm(key, TagSize);
        var plain = new byte[ChunkSize];
        var cipher = new byte[ChunkSize];
        var tag = new byte[TagSize];
        var nonce = new byte[NonceSize];
        header.AsSpan(12, PrefixSize).CopyTo(nonce);
        var aad = new byte[HeaderSize + 1];
        header.CopyTo(aad, 0);

        uint counter = 0;
        bool last;
        do
        {
            int n = (int)Math.Min(ChunkSize, remaining);
            ReadExactly(input, plain, n);
            remaining -= n;
            last = remaining == 0;

            BitConverter.TryWriteBytes(nonce.AsSpan(PrefixSize, 4), counter);
            aad[HeaderSize] = last ? (byte)1 : (byte)0;
            aes.Encrypt(nonce, plain.AsSpan(0, n), cipher.AsSpan(0, n), tag, aad);

            output.Write(cipher, 0, n);
            output.Write(tag, 0, TagSize);
            counter++;
        }
        while (!last);
    }

    private void Decrypt(Stream input, Stream output)
    {
        const string Damaged = "This data couldn't be unlocked. It was changed, cut short, or locked by a different Windows account.";

        var header = new byte[HeaderSize];
        int got = 0;
        while (got < header.Length)
        {
            int n = input.Read(header, got, header.Length - got);
            if (n <= 0) break;
            got += n;
        }
        if (got < header.Length || !IsEncrypted(header)) throw new VaultException("This file isn't locked data.");

        int chunkSize = BitConverter.ToInt32(header, 8);
        if (chunkSize <= 0 || chunkSize > MaxChunkSize) throw new VaultException(Damaged);

        var key = Key();
        long remaining = input.Length - input.Position;

        using var aes = new AesGcm(key, TagSize);
        var cipher = new byte[chunkSize];
        var plain = new byte[chunkSize];
        var tag = new byte[TagSize];
        var nonce = new byte[NonceSize];
        header.AsSpan(12, PrefixSize).CopyTo(nonce);
        var aad = new byte[HeaderSize + 1];
        header.CopyTo(aad, 0);

        uint counter = 0;
        bool last;
        do
        {
            long record = Math.Min((long)chunkSize + TagSize, remaining);
            if (record < TagSize) throw new VaultException(Damaged);

            int n = (int)record - TagSize;
            ReadExactly(input, cipher, n);
            ReadExactly(input, tag, TagSize);
            remaining -= record;
            last = remaining == 0;

            BitConverter.TryWriteBytes(nonce.AsSpan(PrefixSize, 4), counter);
            aad[HeaderSize] = last ? (byte)1 : (byte)0;

            try
            {
                aes.Decrypt(nonce, cipher.AsSpan(0, n), tag, plain.AsSpan(0, n), aad);
            }
            catch (CryptographicException ex)
            {
                throw new VaultException(Damaged, ex);
            }

            output.Write(plain, 0, n);
            counter++;
        }
        while (!last);
    }

    private byte[] Key()
    {
        if (_key != null) return _key;

        var key = _keys.GetKey();
        if (key.Length != KeySize) throw new VaultException("The saved key isn't the right size.");
        _key = key;
        return key;
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n <= 0) throw new VaultException("The data ended earlier than expected.");
            read += n;
        }
    }
}
