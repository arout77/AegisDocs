using ReceiptCapture.Core;
using ReceiptCapture.Core.Compliance;
using ReceiptCapture.Core.HiddenContent;
using ReceiptCapture.Core.Pdf;
using ReceiptCapture.Core.Storage;
using System.Collections.Concurrent;
using System.Threading.Channels;
using ReceiptCapture.Core.Redaction;
using ReceiptCapture.Services;

namespace ReceiptCapture.Workflows;

/// <summary>
/// Owns the saved workflows, the folder watchers, and the one worker that processes files one at a time (reading
/// documents is heavy, so they queue). Starts with the program, so watching works even if the tab was never opened.
/// Watching only happens while Aegis Docs is running.
/// </summary>
public sealed class WorkflowEngine : IDisposable
{
    private static readonly string[] IgnoredPrefixes = { "~$", ".~" };
    private static readonly string[] IgnoredSuffixes = { ".tmp", ".part", ".crdownload", ".download", ".partial" };

    public static WorkflowEngine Instance { get; } = new(new WindowsRedactionService());

    private readonly WorkflowRunner _runner;
    private readonly Channel<WorkItem> _queue = Channel.CreateUnbounded<WorkItem>();
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;

    private sealed record WorkItem(string WorkflowId, string Path, string Display);

    private WorkflowEngine(IRedactionService service) => _runner = new WorkflowRunner(service);

    public WorkflowStore Store { get; } = new();

    /// <summary>Raised on a background thread each time a file has been dealt with. Hop to the UI thread to show it.</summary>
    public event Action<WorkflowOutcome>? FileDone;

    /// <summary>Raised when something about the engine itself needs saying (a watcher failed, a folder vanished).</summary>
    public event Action<string, string>? Notice;       // (workflow name, text)

    public int QueueLength => _pending.Count;
    public event Action? QueueChanged;

    public void Start()
    {
        Store.Load();
        _worker = Task.Run(WorkAsync);
        Sync(sweepNew: true);
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_watchers)
        {
            foreach (var w in _watchers.Values) w.Dispose();
            _watchers.Clear();
        }
    }

    // ------------------------------------------------------------------ watchers
    /// <summary>Makes the watchers match the saved workflows. Call after any change. Newly switched-on folders are swept once.</summary>
    public void Sync(bool sweepNew = false)
    {
        lock (_watchers)
        {
            var wanted = Store.Workflows.Where(w => w.WatchEnabled && w.ProblemWithFolders() == null).ToDictionary(w => w.Id);

            foreach (var id in _watchers.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
            {
                _watchers[id].Dispose();
                _watchers.Remove(id);
            }

            foreach (var wf in wanted.Values)
            {
                if (_watchers.TryGetValue(wf.Id, out var existing) &&
                    string.Equals(existing.Path, wf.WatchFolder, StringComparison.OrdinalIgnoreCase) &&
                    existing.IncludeSubdirectories == wf.IncludeSubfolders)
                    continue;

                if (existing != null) { existing.Dispose(); _watchers.Remove(wf.Id); }
                try
                {
                    var watcher = new FileSystemWatcher(wf.WatchFolder)
                    {
                        IncludeSubdirectories = wf.IncludeSubfolders,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 64 * 1024,
                    };
                    var id = wf.Id;
                    watcher.Created += (_, e) => OnFileEvent(id, e.FullPath);
                    watcher.Changed += (_, e) => OnFileEvent(id, e.FullPath);
                    watcher.Renamed += (_, e) => OnFileEvent(id, e.FullPath);
                    watcher.Error += (_, e) =>
                    {
                        // Too many changes at once for the watcher to keep up: look at the whole folder instead.
                        Notice?.Invoke(wf.Name, "Lost track of some changes, so the whole folder is being looked at again.");
                        SweepFolder(id);
                    };
                    watcher.EnableRaisingEvents = true;
                    _watchers[wf.Id] = watcher;
                    if (sweepNew) SweepFolder(wf.Id);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    Notice?.Invoke(wf.Name, "Could not watch the folder: " + ex.Message);
                }
            }
        }
    }

    public bool IsWatching(string workflowId)
    {
        lock (_watchers) return _watchers.ContainsKey(workflowId);
    }

    /// <summary>Looks at everything in the folder now. Files already handled (same content) are skipped.</summary>
    public void SweepFolder(string workflowId)
    {
        var wf = Store.Workflows.FirstOrDefault(w => w.Id == workflowId);
        if (wf == null || wf.ProblemWithFolders() != null) return;

        _ = Task.Run(() =>
        {
            try
            {
                var listing = FolderScan.Enumerate(wf.WatchFolder, wf.IncludeSubfolders, FolderScan.DefaultMaxFiles);
                foreach (var file in listing.Files)
                    Enqueue(wf, file.FullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Notice?.Invoke(wf.Name, "Could not read the folder: " + ex.Message);
            }
        });
    }

    private void OnFileEvent(string workflowId, string path)
    {
        var wf = Store.Workflows.FirstOrDefault(w => w.Id == workflowId);
        if (wf == null || !wf.WatchEnabled) return;
        Enqueue(wf, path);
    }

    private void Enqueue(Workflow wf, string path)
    {
        if (!ShouldConsider(wf, path)) return;
        var key = wf.Id + "|" + path;
        if (!_pending.TryAdd(key, 0)) return;           // already waiting its turn

        var root = Workflow.Normalize(wf.WatchFolder);
        var relative = Path.GetRelativePath(root, path);
        var display = Path.Combine(Path.GetFileName(root), relative);
        _queue.Writer.TryWrite(new WorkItem(wf.Id, path, display));
        QueueChanged?.Invoke();
    }

    private static bool ShouldConsider(Workflow wf, string path)
    {
        var name = Path.GetFileName(path);
        if (IgnoredPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))) return false;
        if (IgnoredSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase))) return false;
        if (Directory.Exists(path)) return false;

        // Never feed the workflow its own results.
        if (!string.IsNullOrWhiteSpace(wf.OutputFolder))
        {
            var output = Workflow.Normalize(wf.OutputFolder) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(path).StartsWith(output, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return InputFile.Classify(path) != InputKind.Unsupported;
    }

    // ------------------------------------------------------------------ the worker
    private async Task WorkAsync()
    {
        var token = _stop.Token;
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(token))
            {
                var key = item.WorkflowId + "|" + item.Path;
                try
                {
                    var wf = Store.Workflows.FirstOrDefault(w => w.Id == item.WorkflowId);
                    if (wf == null) continue;

                    // Something that just landed is often still being copied or saved: wait until it stops changing.
                    if (!await WaitUntilSettledAsync(item.Path, token)) continue;
                    _pending.TryRemove(key, out _);
                    QueueChanged?.Invoke();

                    var outcome = await _runner.RunAsync(wf, item.Path, item.Display, Store, token);
                    if (outcome == null) continue;
                    if (outcome.Remember) Store.MarkHandled(wf.Id, outcome.Sha256);
                    FileDone?.Invoke(outcome);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    try { File.AppendAllText(AppPaths.LogFile, $"[{DateTime.Now:u}] Workflow error on {item.Path}: {ex}{Environment.NewLine}"); } catch (IOException) { }
                    Notice?.Invoke("Workflows", $"{Path.GetFileName(item.Path)}: {ex.Message.Split('\n')[0]}");
                }
                finally
                {
                    _pending.TryRemove(key, out _);
                    QueueChanged?.Invoke();
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>True once the file has had the same size for a couple of seconds and can be opened. Gives up after two minutes.</summary>
    private static async Task<bool> WaitUntilSettledAsync(string path, CancellationToken token)
    {
        long last = -1;
        int steady = 0;
        for (int i = 0; i < 60; i++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(path)) return false;
                using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                steady = s.Length == last ? steady + 1 : 0;
                last = s.Length;
                if (steady >= 1 && last > 0) return true;
            }
            catch (IOException) { steady = 0; }
            catch (UnauthorizedAccessException) { steady = 0; }
            await Task.Delay(2000, token);
        }
        return false;
    }
}
