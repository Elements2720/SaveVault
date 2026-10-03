using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SaveVault.Core.Games;

public sealed class GameCatalogStore(string cachePath)
{
    public const string ManifestUrl = "https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/master/data/manifest.yaml";
    private const int MaximumBytes = 32 * 1024 * 1024;

    public async Task<GameCatalog> LoadAsync(CancellationToken token = default)
    {
        if (File.Exists(cachePath))
        {
            try
            {
                var catalog = await JsonStorage.ReadAsync<GameCatalog>(cachePath, token);
                Validate(catalog);
                return catalog;
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException)
            {
                return Starter() with { Source = "Offline starter (cached catalog is invalid; update it)" };
            }
        }
        return Starter();
    }

    public async Task<GameCatalog> UpdateAsync(HttpClient client, CancellationToken token = default)
    {
        using var response = await client.GetAsync(ManifestUrl, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes)
            throw new InvalidDataException("Game catalog exceeds the download size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var data = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (data.Length + count > MaximumBytes) throw new InvalidDataException("Game catalog exceeds the download size limit.");
            data.Write(buffer, 0, count);
        }
        var catalog = Parse(Encoding.UTF8.GetString(data.ToArray()), "Ludusavi / PCGamingWiki", token);
        token.ThrowIfCancellationRequested();
        // Parse and validate before atomically replacing the last working offline cache.
        await JsonStorage.WriteAsync(cachePath, catalog, token);
        return catalog;
    }

    public static GameCatalog Parse(string yaml, string source = "Imported manifest", CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Dictionary<string, ManifestEntry> entries;
        try
        {
            entries = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties().Build().Deserialize<Dictionary<string, ManifestEntry>>(yaml)
                ?? throw new InvalidDataException("Empty game manifest.");
        }
        catch (YamlDotNet.Core.YamlException exception)
        { throw new InvalidDataException("Invalid YAML game manifest. The previous catalog was preserved.", exception); }
        if (entries.Count > 100000 || entries.Values.Any(entry => entry is null || entry.Files is null
            || entry.InstallDir is null || entry.Registry is null || entry.Notes is null
            || entry.Files.Values.Any(rule => rule is null || rule.Tags is null || rule.When is null || rule.When.Any(condition => condition is null))))
            throw new InvalidDataException("Incomplete game manifest.");
        var aliases = entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Value.Alias))
            .GroupBy(entry => ResolveAlias(entries, entry.Key), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Key).ToList(), StringComparer.OrdinalIgnoreCase);
        var games = new List<CatalogGame>();
        foreach (var (title, entry) in entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Alias is not null) continue;
            games.Add(new(title, aliases.GetValueOrDefault(title) ?? [], entry.InstallDir.Keys.ToList(),
                entry.Steam?.Id, entry.Files.Select(file => new SaveRule(file.Key, file.Value.Tags,
                    file.Value.When.Select(condition => new RuleConstraint(condition.Os, condition.Store)).ToList())).ToList(),
                entry.Registry.Keys.ToList(), entry.Notes.Select(note => note.Message).ToList()));
        }
        var result = new GameCatalog(DateTime.UtcNow, games, source);
        Validate(result);
        return result;
    }

    private static string ResolveAlias(Dictionary<string, ManifestEntry> entries, string title)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (entries.TryGetValue(title, out var entry) && entry.Alias is { } alias)
        {
            if (!seen.Add(title)) throw new InvalidDataException("Cyclic game aliases in catalog.");
            title = alias;
        }
        return title;
    }

    private static void Validate(GameCatalog catalog)
    {
        if (catalog.Games is null || catalog.Games.Count == 0 || catalog.Games.Count > 100000
            || catalog.Games.Any(game => game is null || string.IsNullOrWhiteSpace(game.Title) || game.Files is null
                || game.Aliases is null || game.InstallDirectories is null || game.Registry is null || game.Notes is null
                || game.Files.Any(rule => rule is null || string.IsNullOrWhiteSpace(rule.Pattern) || rule.Tags is null || rule.When is null)))
            throw new InvalidDataException("Game catalog is incomplete or invalid.");
    }

    private static GameCatalog Starter()
    {
        using var input = typeof(GameCatalogStore).Assembly.GetManifestResourceStream("SaveVault.Core.Games.StarterCatalog.yaml")!;
        using var reader = new StreamReader(input);
        return Parse(reader.ReadToEnd(), "Offline starter — update for the full catalog");
    }

    public sealed class ManifestEntry
    {
        public string? Alias { get; set; }
        public Dictionary<string, ManifestRule> Files { get; set; } = [];
        public Dictionary<string, object?> InstallDir { get; set; } = [];
        public Dictionary<string, object?> Registry { get; set; } = [];
        public SteamEntry? Steam { get; set; }
        public List<ManifestNote> Notes { get; set; } = [];
    }
    public sealed class ManifestRule
    {
        public List<string> Tags { get; set; } = [];
        public List<ManifestConstraint> When { get; set; } = [];
    }
    public sealed class ManifestConstraint
    {
        public string? Os { get; set; }
        public string? Store { get; set; }
    }
    public sealed class SteamEntry { public uint Id { get; set; } }
    public sealed class ManifestNote { public string Message { get; set; } = ""; }
}
