using System.Text.RegularExpressions;

namespace SaveVault.Core.Games;

public sealed partial class GameSaveDiscovery
{
    public CatalogGame? Match(GameInstallation installation, GameCatalog catalog)
    {
        if (installation.SteamId is not null)
        {
            var byId = catalog.Games.FirstOrDefault(game => game.SteamId == installation.SteamId);
            if (byId is not null) return byId;
        }
        return catalog.Games.FirstOrDefault(game => new[] { game.Title }.Concat(game.Aliases).Concat(game.InstallDirectories)
            .Any(name => Normalize(name) == Normalize(installation.Title)));
    }

    public DiscoveryResult Find(GameInstallation installation, CatalogGame? game, DiscoveryEnvironment environment,
        bool deepSearch = false, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var notices = new List<string>();
        var candidates = new List<SaveCandidate>();
        if (game is null) notices.Add("No exact catalog match. Choose a catalog title, update the catalog, or use search/watch discovery.");
        else
        {
            foreach (var rule in game.Files)
            {
                token.ThrowIfCancellationRequested();
                if (rule.When.Count > 0 && !rule.When.Any(condition => condition.Os is null or "windows")) continue;
                var storeMatches = rule.When.Count == 0 || rule.When.Any(condition => (condition.Os is null or "windows")
                    && (condition.Store is null || condition.Store == installation.Store));
                foreach (var pattern in Expand(rule.Pattern, installation, environment, notices))
                {
                    foreach (var path in DiscoveryFileSystem.Resolve(pattern, notices, token))
                    {
                        var directory = Directory.Exists(path);
                        if (rule.Pattern.EndsWith("<storeUserId>", StringComparison.Ordinal) && !directory) continue;
                        var leafPattern = Path.GetFileName(pattern.Replace('\\', '/'));
                        var includes = directory ? new List<string>() : [leafPattern == "**" ? Path.GetFileName(path) : leafPattern];
                        var root = directory ? path : Path.GetDirectoryName(path)!;
                        var confidence = storeMatches ? DiscoveryConfidence.KnownLocation : DiscoveryConfidence.LikelyAlternate;
                        Add(root, includes, confidence, $"Catalog rule: {rule.Pattern}" + (storeMatches ? "" : " (alternate store/layout)"),
                            rule.Tags.Count == 0 ? "Unclassified" : string.Join(" + ", rule.Tags), candidates, notices, token);
                    }
                }
            }
            notices.AddRange(game.Notes);
            if (game.Registry.Count > 0)
                notices.Add($"This game also has {game.Registry.Count} registry rule(s). Registry data needs a separate export and is not included in folder profiles.");
        }

        var steamId = installation.SteamId ?? game?.SteamId;
        if (steamId is not null)
        {
            foreach (var root in environment.SearchRoots)
                foreach (var layout in new[] { $"Goldberg SteamEmu Saves/{steamId}", $"Steam/CODEX/{steamId}", $"Steam/RUNE/{steamId}", $"Steam/FLTIP/{steamId}" })
                    foreach (var path in DiscoveryFileSystem.Resolve(Path.Combine(root, layout), notices, token))
                        Add(path, [], DiscoveryConfidence.LikelyAlternate, $"Alternate layout matched game ID {steamId}", "Possible saves", candidates, notices, token);
        }
        foreach (var path in ConfiguredSavePaths(installation.Path, notices, token))
            Add(path, [], DiscoveryConfidence.LikelyAlternate, "Recognized save-path setting in installation configuration", "Possible saves", candidates, notices, token);

        if (deepSearch)
        {
            var names = new[] { installation.Title, game?.Title ?? "" }.Concat(game?.Aliases ?? [])
                .Concat(game?.InstallDirectories ?? []).Select(Normalize).Where(name => name.Length >= 5).ToHashSet();
            var roots = environment.SearchRoots.Append(installation.Path).Where(Directory.Exists);
            var inspected = new HashSet<string>(PathSafety.Comparer);
            foreach (var file in DiscoveryFileSystem.Files(roots, notices, token, maxDepth: 6))
            {
                var parent = Path.GetDirectoryName(file)!;
                var relative = Path.GetRelativePath(installation.Path, file);
                var parts = parent.Split(Path.DirectorySeparatorChar).Select(Normalize).ToList();
                var gameEvidence = parts.Any(part => names.Any(name => part == name))
                    || (steamId is not null && parts.Contains(steamId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                var saveEvidence = Path.GetExtension(file).Equals(".sav", StringComparison.OrdinalIgnoreCase)
                    || parts.Any(part => part is "save" or "saves" or "savegames" or "savedgames" or "savedata")
                    || Path.GetFileName(file).StartsWith("save", StringComparison.OrdinalIgnoreCase);
                if (!saveEvidence || (!gameEvidence && !PathSafety.Contains(installation.Path, file))) continue;
                // Heuristics select individual files; an extension never selects an entire AppData folder.
                if (inspected.Add(file)) Add(parent, [Path.GetFileName(file)], DiscoveryConfidence.PossibleLocation,
                    $"Game/install-path evidence and save-like file: {relative}", "Possible saves", candidates, notices, token);
            }
        }
        return new(candidates.OrderBy(candidate => candidate.Confidence).ThenBy(candidate => candidate.Root, PathSafety.Comparer).ToList(),
            DiscoveryFileSystem.SummarizeNotices(notices));
    }

    private static void Add(string root, List<string> includes, DiscoveryConfidence confidence, string evidence,
        string kind, List<SaveCandidate> candidates, List<string> notices, CancellationToken token)
    {
        if (candidates.Any(candidate => candidate.Root.Equals(root, PathSafety.Comparison)
            && candidate.ContentKind == kind && candidate.Includes.SequenceEqual(includes, StringComparer.OrdinalIgnoreCase))) return;
        candidates.Add(DiscoveryFileSystem.Describe(root, includes, confidence, evidence, kind, notices, token));
    }

    private static IEnumerable<string> Expand(string rule, GameInstallation installation, DiscoveryEnvironment environment, List<string> notices)
    {
        var pattern = rule.Replace("<home>/Saved Games", "<winSavedGames>", StringComparison.Ordinal)
            .Replace("<home>/Documents", "<winDocuments>", StringComparison.Ordinal)
            .Replace("<winPublic>/Documents", "<winPublicDocuments>", StringComparison.Ordinal);
        foreach (var (name, value) in environment.Tokens) pattern = pattern.Replace($"<{name}>", value, StringComparison.Ordinal);
        pattern = pattern.Replace("<base>", installation.Path, StringComparison.Ordinal)
            .Replace("<storeUserId>", "*", StringComparison.Ordinal).Replace("<osUserName>", Environment.UserName, StringComparison.Ordinal);
        var roots = pattern.Contains("<root>", StringComparison.Ordinal) ? environment.SteamRoots : [""];
        foreach (var root in roots)
        {
            var expanded = pattern.Replace("<root>", root, StringComparison.Ordinal);
            if (expanded.Contains('<') || expanded.Contains('>'))
            { notices.Add($"Unsupported catalog token; rule skipped: {rule}"); continue; }
            yield return expanded;
        }
    }

    private static IEnumerable<string> ConfiguredSavePaths(string installation, List<string> notices, CancellationToken token)
    {
        // Bounded, read-only parsing of recognized keys; never execute or edit game configuration.
        foreach (var file in DiscoveryFileSystem.Files([installation], notices, token, maxDepth: 2, limit: 5000)
            .Where(path => path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)))
        {
            string[] lines;
            try { lines = new FileInfo(file).Length <= 256 * 1024 ? File.ReadAllLines(file) : []; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { notices.Add($"Cannot inspect configuration: {exception.Message}"); continue; }
            foreach (var line in lines)
            {
                var separator = line.IndexOf('=');
                if (separator < 0) continue;
                var key = Normalize(line[..separator]);
                if (key is not ("savepath" or "savegamepath" or "savespath" or "savedir" or "savedirectory")) continue;
                var value = Environment.ExpandEnvironmentVariables(line[(separator + 1)..].Trim().Trim('"', '\''));
                if (string.IsNullOrWhiteSpace(value)) continue;
                string path;
                try { path = Path.GetFullPath(value, Path.GetDirectoryName(file)!); PathSafety.RejectLinks(path); }
                catch (Exception exception) when (exception is IOException or ArgumentException)
                { notices.Add($"Invalid save-path setting: {exception.Message}"); continue; }
                if (Directory.Exists(path)) yield return path;
            }
        }
    }

    public static string Normalize(string value) => NonLetters().Replace(value.ToLowerInvariant(), "");
    [GeneratedRegex("[^\\p{L}\\p{N}]")]
    private static partial Regex NonLetters();
}
