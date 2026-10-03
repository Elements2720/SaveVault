using System.Text.RegularExpressions;

namespace SaveVault.Core.Games;

public static partial class GameLibraryDiscovery
{
    public static IReadOnlyList<GameInstallation> Discover(IEnumerable<string> libraries,
        IEnumerable<string> steamRoots, List<string> notices, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var result = new List<GameInstallation>();
        var roots = new HashSet<string>(PathSafety.Comparer);
        foreach (var steamRoot in steamRoots.Where(Directory.Exists))
        {
            roots.Add(steamRoot);
            var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            var contents = ReadMetadata(vdf, notices);
            foreach (Match match in VdfPair().Matches(contents))
                if (match.Groups[1].Value == "path") roots.Add(match.Groups[2].Value.Replace("\\\\", "\\", StringComparison.Ordinal));
        }
        foreach (var root in roots.Where(Directory.Exists).ToList())
        {
            var apps = Path.Combine(root, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var path in DiscoveryFileSystem.Entries(apps, notices, token)
                .Where(path => Path.GetFileName(path).StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase)
                    && path.EndsWith(".acf", StringComparison.OrdinalIgnoreCase)))
            {
                var fields = VdfPair().Matches(ReadMetadata(path, notices)).Cast<Match>()
                    .GroupBy(match => match.Groups[1].Value, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().Groups[2].Value, StringComparer.OrdinalIgnoreCase);
                if (!fields.TryGetValue("installdir", out var folder) || !fields.TryGetValue("name", out var title)) continue;
                try
                {
                    var install = PathSafety.Resolve(Path.Combine(apps, "common"), folder);
                    if (Directory.Exists(install)) result.Add(new(title, install,
                        uint.TryParse(fields.GetValueOrDefault("appid"), out var id) ? id : null, "steam"));
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
                { notices.Add($"Skipped Steam installation: {exception.Message}"); }
            }
        }
        foreach (var library in libraries.Where(Directory.Exists))
            foreach (var folder in DiscoveryFileSystem.Entries(library, notices, token).Where(Directory.Exists))
                result.Add(new(Path.GetFileName(folder), PathSafety.Full(folder)));
        return result.DistinctBy(game => game.Path, PathSafety.Comparer).OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ReadMetadata(string path, List<string> notices)
    {
        try
        {
            PathSafety.RejectLinks(path);
            return File.Exists(path) && new FileInfo(path).Length <= 1024 * 1024 ? File.ReadAllText(path) : "";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { notices.Add($"Cannot read launcher metadata: {exception.Message}"); return ""; }
    }

    [GeneratedRegex("\"([^\"]+)\"\\s*\"([^\"]*)\"")]
    private static partial Regex VdfPair();
}
