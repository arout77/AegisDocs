using System.Text.Json;
using ReceiptCapture.Core.Security;

namespace ReceiptCapture.Core.Storage;

/// <summary>
/// Keeps the receipt list in one JSON file. With a <see cref="Vault"/> the file is locked on disk; a plain file saved by an
/// older version is still read, and gets locked the next time the list is saved.
/// </summary>
public sealed class JsonReceiptStore : IReceiptStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly Vault? _vault;

    public JsonReceiptStore(string path, Vault? vault = null)
    {
        _path = path;
        _vault = vault;
    }

    /// <summary>Set by <see cref="Load"/> when the saved file couldn't be read: a sentence for the person using the program.</summary>
    public string? LoadProblem { get; private set; }

    public List<Receipt> Load()
    {
        LoadProblem = null;
        if (!File.Exists(_path)) return new List<Receipt>();

        try
        {
            var text = Vault.ReadText(_vault, _path, out var wasEncrypted);
            var receipts = JsonSerializer.Deserialize<List<Receipt>>(text, Options) ?? new List<Receipt>();
            if (_vault != null && !wasEncrypted) Save(receipts);   // lock a file saved by an older version
            return receipts;
        }
        catch (JsonException)
        {
            // Never silently throw away the user's data: keep the unreadable file next to the new one.
            var backup = SetAside("corrupt");
            LoadProblem = $"The saved receipts file couldn't be read. It was kept as {Path.GetFileName(backup)}.";
            return new List<Receipt>();
        }
        catch (VaultException ex)
        {
            var backup = SetAside("unreadable");
            LoadProblem = $"The saved receipts couldn't be unlocked. {ex.Message} They were kept as {Path.GetFileName(backup)}.";
            return new List<Receipt>();
        }
    }

    public void Save(IEnumerable<Receipt> receipts)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Write to a temp file first so a crash mid-write can't leave a half-written data file.
        Vault.WriteText(_vault, _path, JsonSerializer.Serialize(receipts.ToList(), Options));
    }

    private string SetAside(string reason)
    {
        var backup = _path + $".{reason}-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Move(_path, backup);
        return backup;
    }
}
