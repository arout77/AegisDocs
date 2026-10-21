using System.Text.Json;

namespace ReceiptCapture.Core.Storage;

public sealed class JsonReceiptStore : IReceiptStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public JsonReceiptStore(string path) => _path = path;

    public List<Receipt> Load()
    {
        if (!File.Exists(_path)) return new List<Receipt>();

        try
        {
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<List<Receipt>>(stream, Options) ?? new List<Receipt>();
        }
        catch (JsonException)
        {
            // Never silently throw away the user's data: keep the unreadable file next to the new one.
            var backup = _path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(_path, backup);
            return new List<Receipt>();
        }
    }

    public void Save(IEnumerable<Receipt> receipts)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Write to a temp file first so a crash mid-write can't leave a half-written data file.
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(receipts.ToList(), Options));
        File.Move(temp, _path, overwrite: true);
    }
}
