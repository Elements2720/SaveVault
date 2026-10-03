namespace SaveVault.Core.Games;

public sealed record SaveRule(string Pattern, List<string> Tags, List<RuleConstraint> When);
public sealed record RuleConstraint(string? Os = null, string? Store = null);
public sealed record CatalogGame(string Title, List<string> Aliases, List<string> InstallDirectories,
    uint? SteamId, List<SaveRule> Files, List<string> Registry, List<string> Notes);
public sealed record GameCatalog(DateTime UpdatedUtc, List<CatalogGame> Games, string Source);
public sealed record GameInstallation(string Title, string Path, uint? SteamId = null, string Store = "manual")
{
    public override string ToString() => Title;
}
public enum DiscoveryConfidence { KnownLocation, LikelyAlternate, PossibleLocation, ObservedChange }
public sealed record SaveCandidate(string Root, List<string> Includes, DiscoveryConfidence Confidence,
    string Evidence, string ContentKind, int FileCount, long Bytes, DateTime? LatestWriteUtc)
{
    public string Selection => Includes.Count == 0 ? "Entire folder" : string.Join(", ", Includes);
}
public sealed record DiscoveryResult(List<SaveCandidate> Candidates, List<string> Notices);

// Supplied by the host: the core never assumes a Linux user's home is a Windows profile.
public sealed record DiscoveryEnvironment(Dictionary<string, string> Tokens, List<string> SearchRoots,
    List<string> SteamRoots);
