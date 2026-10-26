using System.Security.Cryptography;
using ReceiptCapture.Core.Security;

namespace ReceiptCapture;

/// <summary>
/// Keeps the vault's secret key in a small file that only this Windows login can open (Windows DPAPI).
/// The key is made the first time it is needed. If the Windows account is deleted or reset by an administrator, the key
/// can't be opened any more, and neither can the data it locked. That is the nature of tying data to a login.
/// </summary>
public sealed class DpapiKeyProvider : IKeyProvider
{
    // Extra bytes mixed in so another program using DPAPI as this user can't open the key just by asking.
    private static readonly byte[] Entropy = System.Text.Encoding.UTF8.GetBytes("AegisDocs.Vault.v1");

    private readonly string _path;

    public DpapiKeyProvider(string path) => _path = path;

    public byte[] GetKey()
    {
        if (File.Exists(_path))
        {
            try
            {
                return ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException ex)
            {
                throw new VaultException(
                    "The key that locks your saved data can't be opened by this Windows account.", ex);
            }
        }

        var key = RandomNumberGenerator.GetBytes(Vault.KeySize);
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
        File.Move(temp, _path, overwrite: true);
        return key;
    }
}
