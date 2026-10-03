using System.Collections.Concurrent;

namespace SaveVault.Core.Games;

/// <summary>Read-only session evidence, not process attribution. Always rescan on finish, including after watcher overflow.</summary>
public sealed class SaveWatchSession : IDisposable
{
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly List<string> roots;
    private readonly List<string> excludedRoots;
    private readonly ConcurrentDictionary<string, byte> changed = new(PathSafety.Comparer);
    private readonly ConcurrentQueue<string> watcherNotices = new();
    private readonly Dictionary<string, FileStamp> baseline;
    private readonly List<string> baselineNotices = [];
    private bool disposed;

    private SaveWatchSession(List<string> roots, List<string> excludedRoots, CancellationToken token)
    {
        this.roots = roots;
        this.excludedRoots = excludedRoots;
        try
        {
            foreach (var root in roots)
            {
                PathSafety.RejectLinks(root);
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 32768
                };
                watcher.Changed += OnChange;
                watcher.Created += OnChange;
                watcher.Renamed += OnChange;
                watcher.Error += (_, e) => watcherNotices.Enqueue($"Watcher error: {e.GetException().Message}. A final metadata rescan is required.");
                watchers.Add(watcher);
                watcher.EnableRaisingEvents = true;
            }
            baseline = Capture(baselineNotices, token);
        }
        catch { Dispose(); throw; }
    }

    public static SaveWatchSession Start(IEnumerable<string> roots, IEnumerable<string>? excludedRoots = null, CancellationToken token = default)
    {
        var resolved = roots.Where(Directory.Exists).Select(PathSafety.Full).Distinct(PathSafety.Comparer).OrderBy(path => path.Length).ToList();
        var minimal = new List<string>();
        foreach (var root in resolved) if (!minimal.Any(parent => PathSafety.Contains(parent, root))) minimal.Add(root);
        if (minimal.Count == 0) throw new DirectoryNotFoundException("Choose at least one existing watch folder.");
        return new(minimal, excludedRoots?.Select(PathSafety.Full).ToList() ?? [], token);
    }

    private void OnChange(object sender, FileSystemEventArgs e)
    {
        if (changed.Count >= 25000)
        {
            if (changed.Count == 25000 && changed.TryAdd("<event-limit>", 0))
                watcherNotices.Enqueue("Watcher event limit reached; final results also use a metadata rescan.");
            return;
        }
        changed.TryAdd(e.FullPath, 0);
    }

    public DiscoveryResult Finish(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        foreach (var watcher in watchers) watcher.EnableRaisingEvents = false;
        var notices = baselineNotices.Concat(watcherNotices).ToList();
        var after = Capture(notices, token);
        var candidates = new List<SaveCandidate>();
        foreach (var (path, stamp) in after)
        {
            token.ThrowIfCancellationRequested();
            if (baseline.TryGetValue(path, out var before) && before == stamp && !changed.ContainsKey(path)) continue;
            if (candidates.Count == 5000) { notices.Add("Changed-file result limit reached; repeat with narrower watch roots."); break; }
            candidates.Add(new(Path.GetDirectoryName(path)!, [Path.GetFileName(path)], DiscoveryConfidence.ObservedChange,
                baseline.ContainsKey(path) ? "File changed during this session; game ownership needs confirmation"
                    : "File appeared during this session; game ownership needs confirmation",
                "Observed file", 1, stamp.Size, stamp.LastWriteUtc));
        }
        var deleted = baseline.Keys.Count(path => !after.ContainsKey(path));
        if (deleted > 0) notices.Add($"{deleted} file(s) disappeared during this session; they cannot be backed up.");
        if (notices.Any(notice => notice.Contains("limit", StringComparison.OrdinalIgnoreCase)))
            notices.Add("The scan was bounded; choose narrower watch roots for files outside the scanned scope.");
        return new(candidates.OrderByDescending(candidate => candidate.LatestWriteUtc).ToList(), DiscoveryFileSystem.SummarizeNotices(notices));
    }

    private Dictionary<string, FileStamp> Capture(List<string> notices, CancellationToken token)
    {
        var result = new Dictionary<string, FileStamp>(PathSafety.Comparer);
        foreach (var path in DiscoveryFileSystem.Files(roots, notices, token, excludedRoots: excludedRoots))
        {
            try
            {
                var info = new FileInfo(path);
                result[path] = new(info.Length, info.LastWriteTimeUtc);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { notices.Add($"Cannot inspect watched file: {exception.Message}"); }
        }
        return result;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var watcher in watchers) watcher.Dispose();
        watchers.Clear();
    }

    private sealed record FileStamp(long Size, DateTime LastWriteUtc);
}
