using System.Text.Json;
using ReceiptCapture.Core.Security;

namespace ReceiptCapture.Workflows;

/// <summary>
/// Saves the workflow list locked with the same vault as the receipts (custom terms are often client names), and
/// remembers which files each workflow has already handled.
/// </summary>
public sealed class WorkflowStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();
    private Dictionary<string, HashSet<string>> _done = new();

    public List<Workflow> Workflows { get; private set; } = new();

    // ------------------------------------------------------------------ workflows
    public void Load()
    {
        var path = AppPaths.WorkflowsFile;
        if (File.Exists(path))
        {
            string? temp = null;
            try
            {
                temp = AppVault.OpenToTemp(path);
                Workflows = JsonSerializer.Deserialize<List<Workflow>>(File.ReadAllText(temp)) ?? new();
                foreach (var w in Workflows) w.WatchEnabled = w.WatchEnabled && w.ProblemWithFolders() == null;
            }
            catch (Exception ex)
            {
                // Keep the unreadable file for inspection instead of overwriting it with an empty list.
                try { File.Move(path, path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); } catch (IOException) { }
                Log("Could not read saved workflows: " + ex.Message);
                Workflows = new();
            }
            finally
            {
                if (temp != null) try { File.Delete(temp); } catch (IOException) { }
            }
        }

        LoadState();
    }

    public void Save()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            Directory.CreateDirectory(AppPaths.TempDir);
            var plain = Path.Combine(AppPaths.TempDir, "workflows-" + Guid.NewGuid().ToString("N") + ".json");
            var sealedNew = Path.Combine(AppPaths.DataDir, "workflows.new.json" + Vault.SealedSuffix);
            try
            {
                File.WriteAllText(plain, JsonSerializer.Serialize(Workflows, Json));
                if (File.Exists(sealedNew)) File.Delete(sealedNew);
                AppVault.Instance.EncryptFile(plain, sealedNew);
                AppVault.Instance.Verify(sealedNew);
                File.Move(sealedNew, AppPaths.WorkflowsFile, overwrite: true);
            }
            finally
            {
                try { File.Delete(plain); } catch (IOException) { }
                try { if (File.Exists(sealedNew)) File.Delete(sealedNew); } catch (IOException) { }
            }
        }
    }

    // ------------------------------------------------------------------ what's been handled
    public bool WasHandled(string workflowId, string sha256)
    {
        lock (_gate) return _done.TryGetValue(workflowId, out var set) && set.Contains(sha256);
    }

    public void MarkHandled(string workflowId, string sha256)
    {
        lock (_gate)
        {
            if (!_done.TryGetValue(workflowId, out var set)) _done[workflowId] = set = new(StringComparer.OrdinalIgnoreCase);
            if (!set.Add(sha256)) return;
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(AppPaths.WorkflowStateFile, JsonSerializer.Serialize(_done));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("Could not save workflow state: " + ex.Message); }
        }
    }

    public void ForgetHandled(string workflowId)
    {
        lock (_gate)
        {
            if (_done.Remove(workflowId))
                try { File.WriteAllText(AppPaths.WorkflowStateFile, JsonSerializer.Serialize(_done)); } catch (IOException) { }
        }
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(AppPaths.WorkflowStateFile)) return;
            var raw = JsonSerializer.Deserialize<Dictionary<string, HashSet<string>>>(File.ReadAllText(AppPaths.WorkflowStateFile));
            if (raw == null) return;
            _done = raw.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or JsonException) { Log("Could not read workflow state: " + ex.Message); }
    }

    private static void Log(string text)
    {
        try { File.AppendAllText(AppPaths.LogFile, $"[{DateTime.Now:u}] {text}{Environment.NewLine}"); } catch (IOException) { }
    }
}
