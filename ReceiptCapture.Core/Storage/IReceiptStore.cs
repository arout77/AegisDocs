namespace ReceiptCapture.Core.Storage;

/// <summary>Where receipts live. Today that's a JSON file; swapping in SQLite later means writing one new class.</summary>
public interface IReceiptStore
{
    List<Receipt> Load();
    void Save(IEnumerable<Receipt> receipts);
}
