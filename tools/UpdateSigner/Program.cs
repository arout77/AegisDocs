using System.Security.Cryptography;
using ReceiptCapture.Core.Updates;

// Makes and signs the update information (latest.json) that Aegis Docs checks for new versions.
//
//   keygen                                  Makes a new signing key pair and prints both keys.
//   sign   --installer <file> --version <x.y.z> --url <https address of the installer>
//          --out <latest.json> [--notes <text> | --notes-file <file>] [--mandatory] [--key-env <NAME>]
//   verify --file <latest.json> --public-key <key>
//
// The private key is read from an environment variable (default UPDATE_SIGNING_KEY), never from the command line,
// so it doesn't end up in build logs.

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0) return Usage();

    var options = ParseOptions(args.Skip(1).ToArray());
    try
    {
        switch (args[0].ToLowerInvariant())
        {
            case "keygen": return KeyGen();
            case "sign": return Sign(options);
            case "verify": return Verify(options);
            default: return Usage();
        }
    }
    catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException or FormatException or CryptographicException)
    {
        Console.Error.WriteLine("Error: " + ex.Message);
        return 1;
    }
}

static int KeyGen()
{
    var (publicKey, privateKey) = UpdateSigning.GenerateKeyPair();
    Console.WriteLine("Signing keys made. Keep the PRIVATE key secret and never commit it.");
    Console.WriteLine();
    Console.WriteLine("PUBLIC key (goes in ReceiptCapture/Services/UpdateConfig.cs):");
    Console.WriteLine(publicKey);
    Console.WriteLine();
    Console.WriteLine("PRIVATE key (goes in the GitHub secret UPDATE_SIGNING_KEY, and in a password manager as a backup):");
    Console.WriteLine(privateKey);
    Console.WriteLine();
    Console.WriteLine("If the private key is lost, installed copies can no longer be updated automatically,");
    Console.WriteLine("because they only trust this public key.");
    return 0;
}

static int Sign(Dictionary<string, string> o)
{
    var installer = Need(o, "installer");
    var version = Need(o, "version");
    var url = Need(o, "url");
    var output = Need(o, "out");

    var keyEnv = o.GetValueOrDefault("key-env", "UPDATE_SIGNING_KEY");
    var privateKey = Environment.GetEnvironmentVariable(keyEnv);
    if (string.IsNullOrWhiteSpace(privateKey))
        throw new UpdateException($"The private key wasn't found in the environment variable {keyEnv}.");

    string? notes = o.GetValueOrDefault("notes");
    if (o.TryGetValue("notes-file", out var notesFile)) notes = File.ReadAllText(notesFile);

    var info = new FileInfo(installer);
    if (!info.Exists) throw new UpdateException($"The installer {installer} doesn't exist.");

    string hash;
    using (var stream = info.OpenRead()) hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

    var manifest = new UpdateManifest(version, url, hash, info.Length, notes, o.ContainsKey("mandatory"), DateTimeOffset.UtcNow);
    File.WriteAllText(output, SignedManifest.Create(manifest, privateKey));

    Console.WriteLine($"Signed {output}: version {version}, {info.Length:N0} bytes, SHA-256 {hash}");
    return 0;
}

static int Verify(Dictionary<string, string> o)
{
    var manifest = SignedManifest.Open(File.ReadAllText(Need(o, "file")), Need(o, "public-key"));
    Console.WriteLine($"OK: version {manifest.Version}, {manifest.SizeBytes:N0} bytes, {manifest.InstallerUrl}");
    return 0;
}

static string Need(Dictionary<string, string> o, string name)
    => o.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : throw new UpdateException($"--{name} is required.");

static Dictionary<string, string> ParseOptions(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) throw new UpdateException($"Unexpected argument \"{args[i]}\".");
        var name = args[i][2..];
        bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
        result[name] = hasValue ? args[++i] : "";
    }
    return result;
}

static int Usage()
{
    Console.Error.WriteLine("Usage: UpdateSigner keygen | sign --installer F --version V --url U --out F | verify --file F --public-key K");
    return 2;
}
