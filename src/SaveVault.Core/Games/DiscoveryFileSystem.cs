using System.IO.Enumeration;

namespace SaveVault.Core.Games;

internal static class DiscoveryFileSystem
{
    internal static IReadOnlyList<string> Entries(string path, List<string> notices, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            PathSafety.RejectLinks(path);
            var result = new List<string>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                token.ThrowIfCancellationRequested();
                if (result.Count == 10000) { notices.Add($"Directory entry limit reached: {path}"); break; }
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0) result.Add(entry);
            }
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { notices.Add($"Skipped {path}: {exception.Message}"); return []; }
    }

    internal static IEnumerable<string> Files(IEnumerable<string> roots, List<string> notices,
        CancellationToken token, int maxDepth = 10, int limit = 100000, IReadOnlyList<string>? excludedRoots = null)
    {
        var pending = new Stack<(string Path, int Depth)>();
        foreach (var root in roots.Where(Directory.Exists).Distinct(PathSafety.Comparer)) pending.Push((root, 0));
        var seen = new HashSet<string>(PathSafety.Comparer);
        var count = 0;
        while (pending.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(PathSafety.Full(current.Path))) continue;
            foreach (var entry in Entries(current.Path, notices, token))
            {
                if (excludedRoots?.Any(root => PathSafety.Contains(root, entry)) == true) continue;
                if (++count > limit) { notices.Add("Search entry limit reached; narrow the search roots and try again."); yield break; }
                if (Directory.Exists(entry))
                {
                    if (current.Depth < maxDepth) pending.Push((entry, current.Depth + 1));
                    else notices.Add($"Depth limit reached: {entry}");
                }
                else if (File.Exists(entry)) yield return entry;
            }
        }
    }

    internal static IReadOnlyList<string> Resolve(string pattern, List<string> notices, CancellationToken token)
    {
        pattern = pattern.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(pattern)) return [];
        var root = Path.GetPathRoot(pattern)!;
        var parts = pattern[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var results = new List<string>();
        var visited = 0;
        var depthLimited = false;
        void Expand(string path, int index, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (++visited > 25000) return;
            if (depth > 16) { depthLimited = true; return; }
            if (index == parts.Length)
            {
                if (Directory.Exists(path) || File.Exists(path))
                {
                    try { PathSafety.RejectLinks(path); results.Add(PathSafety.Full(path)); }
                    catch (IOException exception) { notices.Add(exception.Message); }
                }
                return;
            }
            var part = parts[index];
            if (part is "." or "..")
            {
                // Resolve parents after wildcard expansion, not lexically before it: */.. must
                // still require a matching directory. Check links before discarding a component.
                try { PathSafety.RejectLinks(path); }
                catch (IOException exception) { notices.Add(exception.Message); return; }
                var normalized = PathSafety.Full(path);
                Expand(part == "." ? normalized : Path.GetDirectoryName(normalized) ?? root, index + 1, depth);
            }
            else if (part == "**")
            {
                Expand(path, index + 1, depth);
                foreach (var entry in Entries(path, notices, token).Where(Directory.Exists)) Expand(entry, index, depth + 1);
            }
            else if (part.IndexOfAny(['*', '?']) >= 0)
            {
                if (!Directory.Exists(path)) return;
                foreach (var entry in Entries(path, notices, token))
                    if (FileSystemName.MatchesSimpleExpression(part, Path.GetFileName(entry), ignoreCase: true)
                        && (index == parts.Length - 1 || Directory.Exists(entry))) Expand(entry, index + 1, depth + 1);
            }
            else Expand(Path.Combine(path, part), index + 1, depth);
        }
        Expand(root, 0, 0);
        if (visited > 25000) notices.Add($"Pattern search limit reached: {pattern}");
        if (depthLimited) notices.Add($"Pattern depth limit reached: {pattern}");
        return results;
    }

    internal static List<string> SummarizeNotices(IEnumerable<string> notices)
    {
        var all = notices.Distinct().OrderBy(notice => notice.Contains("limit", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        if (all.Count <= 100) return all;
        return all.Take(99).Append($"{all.Count - 99} additional scan notices omitted.").ToList();
    }

    internal static SaveCandidate Describe(string root, List<string> includes, DiscoveryConfidence confidence,
        string evidence, string kind, List<string> notices, CancellationToken token)
    {
        long bytes = 0;
        var count = 0;
        DateTime? latest = null;
        // Exact/wildcard leaves avoid enumerating unrelated AppData subdirectories.
        var files = includes.Count > 0
            ? includes.SelectMany(include => Resolve(Path.Combine(root, include), notices, token))
                .SelectMany(path => Directory.Exists(path) ? Files([path], notices, token) : new[] { path })
            : Files([root], notices, token);
        foreach (var path in files.Distinct(PathSafety.Comparer))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                bytes += info.Length;
                count++;
                if (latest is null || info.LastWriteTimeUtc > latest) latest = info.LastWriteTimeUtc;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { notices.Add($"Cannot inspect {path}: {exception.Message}"); }
        }
        return new(root, includes, confidence, evidence, kind, count, bytes, latest);
    }
}
