using System.Security.Cryptography;

namespace ReceiptCapture.Core.Storage;

public static class FileHasher
{
    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
