namespace SaveVault.Core.Games;

public sealed record GameDiscoverySettings
{
    public List<string> LibraryRoots { get; init; } = [@"G:\Games"];
    public List<string> WatchRoots { get; init; } = [];
    public List<GameInstallation> ManualInstallations { get; init; } = [];
    public Dictionary<string, string> CatalogOverrides { get; init; } = [];
}

public sealed class GameDiscoverySettingsStore(string path)
{
    public async Task<GameDiscoverySettings> LoadAsync(CancellationToken token = default) => File.Exists(path)
        ? await JsonStorage.ReadAsync<GameDiscoverySettings>(path, token) : new();

    public Task SaveAsync(GameDiscoverySettings settings, CancellationToken token = default) => JsonStorage.WriteAsync(path, settings, token);
}
