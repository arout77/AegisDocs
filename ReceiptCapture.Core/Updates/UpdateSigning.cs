using System.Security.Cryptography;
using System.Text.Json;

namespace ReceiptCapture.Core.Updates;

/// <summary>
/// Signing and checking with ECDSA (P-256, SHA-256). The publisher keeps the private key secret (a GitHub secret);
/// the program carries only the public key, so it can check a signature but never make one.
/// </summary>
public static class UpdateSigning
{
    /// <summary>Makes a new key pair. Both keys are text (base64) so they can be pasted into a secret or a source file.</summary>
    public static (string PublicKey, string PrivateKey) GenerateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()),
                Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()));
    }

    public static byte[] Sign(byte[] data, string privateKey)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey.Trim()), out _);
        return ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>True only if <paramref name="signature"/> was made over exactly <paramref name="data"/> by the matching private key.</summary>
    public static bool Verify(byte[] data, byte[] signature, string publicKey)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey.Trim()), out _);
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// The file the program downloads to learn about updates (latest.json). It holds the manifest exactly as it was signed,
/// plus the signature, so there is no room for the two to disagree: { "payload": "...", "signature": "..." }.
/// </summary>
public static class SignedManifest
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private sealed record Envelope(string? Payload, string? Signature);

    /// <summary>Signs a manifest and returns the text of latest.json.</summary>
    public static string Create(UpdateManifest manifest, string privateKey)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest.Validated(), Json);
        var signature = UpdateSigning.Sign(payload, privateKey);
        return JsonSerializer.Serialize(
            new Envelope(Convert.ToBase64String(payload), Convert.ToBase64String(signature)),
            new JsonSerializerOptions(Json) { WriteIndented = true });
    }

    /// <summary>
    /// Checks the signature and returns the manifest. Throws <see cref="UpdateException"/> if the file is malformed, wasn't
    /// signed by the matching key, or contains anything unsafe. Nothing in the manifest is trusted before the check passes.
    /// </summary>
    public static UpdateManifest Open(string text, string publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey))
            throw new UpdateException("Updates aren't set up in this build.");

        byte[] payload, signature;
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(text, Json);
            if (envelope?.Payload is null || envelope.Signature is null)
                throw new UpdateException("The update information isn't in the expected format.");

            payload = Convert.FromBase64String(envelope.Payload);
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new UpdateException("The update information isn't in the expected format.", ex);
        }

        if (!UpdateSigning.Verify(payload, signature, publicKey))
            throw new UpdateException("The update information couldn't be verified, so it was ignored.");

        try
        {
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(payload, Json)
                           ?? throw new UpdateException("The update information is empty.");
            return manifest.Validated();
        }
        catch (JsonException ex)
        {
            throw new UpdateException("The update information isn't in the expected format.", ex);
        }
    }
}
