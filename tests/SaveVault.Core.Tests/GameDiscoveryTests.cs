using System.Net;
using System.Text;
using SaveVault.Core.Games;
using Xunit;

namespace SaveVault.Core.Tests;

public sealed class GameDiscoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SaveVaultGames", Guid.NewGuid().ToString("N"));
    private const string Manifest = """
        Test Game:
          files:
            "<winDocuments>/Test Game/*.sav":
              tags: [save]
              when: [{os: windows}]
            "<winAppData>/TestGame/<storeUserId>":
              tags: [save]
              when: [{os: windows}]
            "<winAppData>/TestGame/settings.ini":
              tags: [config]
              when: [{os: windows}]
            "<home>/linux-only":
              when: [{os: linux}]
          installDir:
            TestGame: {}
          steam: {id: 123}
          cloud: {steam: true}
        Test Game Alternate Name:
          alias: Test Game
        """;

    private string Folder(params string[] parts)
    {
        var path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }
    private static void Write(string folder, string name, string contents = "save") => File.WriteAllText(Path.Combine(folder, name), contents);
    private DiscoveryEnvironment Context(string documents, string roaming) => new(new()
    {
        ["home"] = root, ["winDocuments"] = documents, ["winAppData"] = roaming
    }, [documents, roaming], []);

    [Fact]
    public async Task OfflineStarterLoadsAliasesAndWindowsRules()
    {
        var catalog = await new GameCatalogStore(Path.Combine(root, "missing.json")).LoadAsync();
        Assert.Contains(catalog.Games, entry => entry.Title == "Cyberpunk 2077");
        var game = new GameSaveDiscovery().Match(new("Alan Wake 2", root), catalog);
        Assert.NotNull(game);
        Assert.Equal("Alan Wake II", game.Title);
        Assert.Contains(game.Files, rule => rule.When.Any(condition => condition.Os == "windows"));
    }

    [Fact]
    public void RedirectedDocumentsAndAccountFoldersAreFoundWithoutMisclassifyingSettings()
    {
        var docs = Folder("redirected-documents");
        var roaming = Folder("roaming");
        var save = Folder("redirected-documents", "Test Game");
        Write(save, "slot1.sav");
        Write(save, "unrelated.dat");
        Write(Folder("roaming", "TestGame", "account-one"), "save.dat");
        Write(Folder("roaming", "TestGame", "account-two"), "save.dat");
        Write(Folder("roaming", "TestGame"), "settings.ini");
        var catalog = GameCatalogStore.Parse(Manifest);
        var game = catalog.Games.Single();
        var result = new GameSaveDiscovery().Find(new("TestGame", Folder("install")), game, Context(docs, roaming));
        Assert.Contains(result.Candidates, candidate => candidate.Root == save && candidate.Includes.SequenceEqual(["*.sav"]) && candidate.FileCount == 1);
        Assert.Equal(2, result.Candidates.Count(candidate => candidate.ContentKind == "save" && candidate.Includes.Count == 0));
        Assert.Single(result.Candidates, candidate => candidate.ContentKind == "config");
        Assert.DoesNotContain(result.Candidates, candidate => candidate.ContentKind == "save" && candidate.Includes.Contains("settings.ini"));
    }

    [Fact]
    public async Task FilePatternsBackupNewSlotsButNeverUnrelatedFilesOrFolders()
    {
        var source = Folder("saves");
        Write(source, "slot1.sav");
        Write(source, "large-unrelated.dat");
        Write(Folder("saves", "unrelated"), "slot3.sav");
        var profile = new BackupProfile { Name = "Selected saves", Destination = Path.Combine(root, "backup"),
            Sources = [new SourceFolder { Name = "Game", Path = source, Includes = ["*.sav"] }] };
        var repository = new BackupRepository(profile.Destination);
        var first = await repository.BackupAsync(profile);
        Assert.Single(first.Snapshot.Files);
        Write(source, "slot2.sav", "new slot");
        var second = await repository.BackupAsync(profile);
        Assert.Equal(2, second.Snapshot.Files.Count);
        Assert.Empty(second.Snapshot.Directories);
        var plan = repository.PlanRestore(second.Snapshot, Path.Combine(root, "restore"));
        await repository.RestoreAsync(plan);
        Assert.All(plan.Items, item => Assert.True(File.Exists(item.TargetPath)));
    }

    [Theory]
    [InlineData("../escape.sav", typeof(InvalidDataException))]
    [InlineData("/absolute.sav", typeof(InvalidDataException))]
    [InlineData("C:/escape.sav", typeof(InvalidDataException))]
    [InlineData("**/*.sav", typeof(ArgumentException))]
    [InlineData("CON.sav", typeof(InvalidDataException))]
    public void UnsafeIncludePatternsAreRejectedBeforeBackup(string include, Type exceptionType)
    {
        var profile = new BackupProfile { Name = "Game", Destination = Path.Combine(root, "backup"),
            Sources = [new SourceFolder { Name = "Game", Path = Folder("saves"), Includes = [include] }] };
        Assert.Throws(exceptionType, () => BackupRepository.ValidateProfile(profile));
        Assert.False(Directory.Exists(profile.Destination));
    }

    [Fact]
    public async Task NestedIncludesPruneUnselectedFoldersAndKeepSelectedFolderFutureFiles()
    {
        var source = Folder("source");
        Write(Folder("source", "saves"), "one.dat");
        Write(Folder("source", "config"), "settings.ini");
        Write(Folder("source", "cache"), "large.bin");
        var profile = new BackupProfile { Name = "Game", Destination = Path.Combine(root, "backup"),
            Sources = [new SourceFolder { Name = "Game", Path = source, Includes = ["saves", "config/settings.ini"] }] };
        var result = await new BackupRepository(profile.Destination).BackupAsync(profile);
        Assert.Equal(2, result.Snapshot.Files.Count);
        Assert.DoesNotContain(result.Snapshot.Directories, directory => directory.RelativePath == "cache");
    }

    [Fact]
    public void AddingConfirmedCandidatesPreservesExistingSourcesAndIdsWithoutOverlaps()
    {
        var source = new SourceFolder { Name = "Game", Path = Folder("data"), Includes = ["old.sav"] };
        var child = Folder("data", "saves");
        var result = GameProfileSources.Merge([new(child, [], DiscoveryConfidence.KnownLocation, "test", "save", 0, 0, null)], [source]);
        var merged = Assert.Single(result);
        Assert.Equal(source.Id, merged.Id);
        Assert.Contains("old.sav", merged.Includes);
        Assert.Contains("saves", merged.Includes);
        var whole = GameProfileSources.Merge([new(source.Path, [], DiscoveryConfidence.KnownLocation, "test", "save", 0, 0, null)], result);
        Assert.Empty(Assert.Single(whole).Includes);
    }

    [Fact]
    public void AlternateIdLayoutAndConfiguredRelativeSavePathAreFound()
    {
        var local = Folder("local");
        var alternate = Folder("local", "Goldberg SteamEmu Saves", "123");
        Write(alternate, "save.dat");
        var installation = Folder("install");
        var custom = Folder("install", "my-custom-saves");
        Write(custom, "file0");
        Write(installation, "settings.ini", "[Game]\nSavePath=my-custom-saves\n");
        var game = GameCatalogStore.Parse(Manifest).Games.Single();
        var result = new GameSaveDiscovery().Find(new("Test Game", installation), game, Context(Folder("docs"), local));
        Assert.Contains(result.Candidates, candidate => candidate.Root == alternate && candidate.Confidence == DiscoveryConfidence.LikelyAlternate);
        Assert.Contains(result.Candidates, candidate => candidate.Root == custom && candidate.Evidence.Contains("setting", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownTokensAreReportedAndNeverExpandedIntoABroadFolder()
    {
        var game = new CatalogGame("Game", [], [], null, [new("<unknown>/file.sav", ["save"], [])], [], []);
        var result = new GameSaveDiscovery().Find(new("Game", Folder("install")), game, Context(Folder("docs"), Folder("roaming")));
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Notices, notice => notice.Contains("Unsupported catalog token", StringComparison.Ordinal));
    }

    [Fact]
    public void DeepSearchRequiresGameEvidenceAsWellAsSaveLikeFiles()
    {
        var docs = Folder("docs");
        Write(Folder("docs", "Unknown Game", "Saves"), "progress.dat");
        Write(Folder("docs", "Unrelated Game"), "other.sav");
        var result = new GameSaveDiscovery().Find(new("Unknown Game", Folder("install")), null,
            Context(docs, Folder("roaming")), deepSearch: true);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("progress.dat", Assert.Single(candidate.Includes));
        Assert.Equal(DiscoveryConfidence.PossibleLocation, candidate.Confidence);
    }

    [Fact]
    public void SteamMetadataFindsAdditionalLibrariesAndRejectsEscapingInstallDirectories()
    {
        var steam = Folder("steam");
        var apps = Folder("steam", "steamapps");
        var secondary = Folder("secondary");
        var secondaryApps = Folder("secondary", "steamapps");
        var install = Folder("secondary", "steamapps", "common", "TestGame");
        Write(apps, "libraryfolders.vdf", $"\"libraryfolders\" {{ \"1\" {{ \"path\" \"{secondary.Replace("\\", "\\\\", StringComparison.Ordinal)}\" }} }}");
        Write(secondaryApps, "appmanifest_123.acf", "\"AppState\" { \"appid\" \"123\" \"name\" \"Test Game\" \"installdir\" \"TestGame\" }");
        Write(apps, "appmanifest_456.acf", "\"AppState\" { \"appid\" \"456\" \"name\" \"Bad\" \"installdir\" \"../escape\" }");
        var notices = new List<string>();
        var game = Assert.Single(GameLibraryDiscovery.Discover([], [steam], notices));
        Assert.Equal(install, game.Path);
        Assert.Equal(123U, game.SteamId);
        Assert.NotEmpty(notices);
    }

    [Fact]
    public async Task FailedCatalogUpdateKeepsLastGoodOfflineCache()
    {
        var store = new GameCatalogStore(Path.Combine(root, "catalog.json"));
        using var good = new HttpClient(new ReplyHandler(Manifest));
        await store.UpdateAsync(good);
        using var bad = new HttpClient(new ReplyHandler("invalid: [broken"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateAsync(bad));
        var cached = await store.LoadAsync();
        Assert.Equal("Test Game", Assert.Single(cached.Games).Title);
    }

    [Fact]
    public async Task CancelledCatalogUpdateDoesNotPublishACache()
    {
        var path = Path.Combine(root, "catalog.json");
        var store = new GameCatalogStore(path);
        using var client = new HttpClient(new ReplyHandler(Manifest));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpdateAsync(client, cancellation.Token));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void WatchFinalRescanFindsNewAndModifiedFilesAndExcludesAppSettings()
    {
        var watched = Folder("watched");
        Write(watched, "old.dat", "before");
        Write(watched, "unchanged.dat", "same");
        var settings = Folder("watched", "SaveVault");
        using var session = SaveWatchSession.Start([watched], [settings]);
        Write(watched, "old.dat", "after with new length");
        Write(watched, "new.dat", "new");
        Write(settings, "profiles.json", "{}");
        var result = session.Finish();
        Assert.Equal(2, result.Candidates.Count);
        Assert.All(result.Candidates, candidate => Assert.Equal(DiscoveryConfidence.ObservedChange, candidate.Confidence));
        Assert.DoesNotContain(result.Candidates, candidate => candidate.Includes.Contains("profiles.json"));
        Assert.DoesNotContain(result.Candidates, candidate => candidate.Includes.Contains("unchanged.dat"));
    }

    [Fact]
    public void DiscoveryDoesNotFollowLinkedLibraryFolders()
    {
        if (OperatingSystem.IsWindows()) return;
        var library = Folder("library");
        var outside = Folder("outside");
        Directory.CreateSymbolicLink(Path.Combine(library, "Linked Game"), outside);
        Assert.Empty(GameLibraryDiscovery.Discover([library], [], []));
    }

    [Fact]
    public void SavedGamesRulesUseTheRedirectedKnownFolder()
    {
        var redirected = Folder("redirected-saved-games");
        var saves = Folder("redirected-saved-games", "TestGame");
        Write(saves, "save.dat");
        var context = Context(Folder("documents"), Folder("roaming"));
        context.Tokens["winSavedGames"] = redirected;
        var game = new CatalogGame("TestGame", [], [], null,
            [new("<home>/Saved Games/TestGame", ["save"], [])], [], []);
        var result = new GameSaveDiscovery().Find(new("TestGame", Folder("install")), game, context);
        Assert.Equal(saves, Assert.Single(result.Candidates).Root);
    }

    [Fact]
    public async Task OldProfilesWithoutIncludesStillBackUpWholeFolders()
    {
        var source = Folder("old-source");
        Write(source, "one.dat");
        Write(Folder("old-source", "nested"), "two.dat");
        var path = Path.Combine(root, "profiles.json");
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new[] { new
        {
            Id = Guid.NewGuid(), Name = "Old profile", Destination = Path.Combine(root, "backup"),
            Sources = new[] { new { Id = Guid.NewGuid(), Name = "Old source", Path = source } }
        } }));
        var profile = Assert.Single(await new ProfileStore(path).LoadAsync());
        Assert.Empty(profile.Sources[0].Includes);
        var result = await new BackupRepository(profile.Destination).BackupAsync(profile);
        Assert.Equal(2, result.Snapshot.Files.Count);
    }

    [Fact]
    public async Task DiscoverySettingsPreserveWindowsPathsAndManualMappingsOnLinux()
    {
        var store = new GameDiscoverySettingsStore(Path.Combine(root, "games.json"));
        var settings = new GameDiscoverySettings { LibraryRoots = [@"G:\Games"],
            ManualInstallations = [new("Renamed game", @"G:\Games\Renamed game", Store: "added")],
            CatalogOverrides = new() { [@"G:\Games\Renamed game"] = "Test Game" } };
        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();
        Assert.Equal(@"G:\Games", Assert.Single(loaded.LibraryRoots));
        Assert.Equal("Test Game", loaded.CatalogOverrides[@"G:\Games\Renamed game"]);
        Assert.Equal(@"G:\Games\Renamed game", Assert.Single(loaded.ManualInstallations).Path);
    }

    [Fact]
    public void SteamIdTakesPriorityOverASimilarInstallationTitle()
    {
        var first = new CatalogGame("Test Game", [], [], 111, [], [], []);
        var second = new CatalogGame("Different title", [], [], 222, [], [], []);
        var catalog = new GameCatalog(DateTime.UtcNow, [first, second], "test");
        var match = new GameSaveDiscovery().Match(new("Test Game", root, 222, "steam"), catalog);
        Assert.Equal(second, match);
    }

    [Fact]
    public async Task InvalidCachedCatalogFallsBackToAnExplicitlyLabelledStarter()
    {
        var path = Path.Combine(Folder("cache"), "catalog.json");
        await File.WriteAllTextAsync(path, "{broken");
        var catalog = await new GameCatalogStore(path).LoadAsync();
        Assert.Contains("invalid", catalog.Source, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(catalog.Games);
    }

    [Fact]
    public void ParentRelativeCatalogRulesFindExistingLocalLowSaves()
    {
        var local = Folder("AppData", "Local");
        var saves = Folder("AppData", "LocalLow", "Flaming Torch Games", "Fairtravel Battle", "Profiles");
        Write(saves, "slot.sav");
        var game = new CatalogGame("Fairtravel Battle CCG", [], [], null,
            [new("<winLocalAppData>/../LocalLow/Flaming Torch Games/Fairtravel Battle/Profiles", ["save"], [new("windows")])], [], []);
        var context = new DiscoveryEnvironment(new() { ["winLocalAppData"] = local }, [], []);
        var result = new GameSaveDiscovery().Find(new(game.Title, Folder("install")), game, context);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(saves, candidate.Root);
        Assert.Equal(1, candidate.FileCount);
        Assert.DoesNotContain(result.Notices, notice => notice.Contains("Unsupported relative", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WildcardParentsRequireMatchingDirectoriesBeforeMovingToTheirParents(bool matchingAccounts)
    {
        var install = Folder("install");
        Folder("install", "accounts");
        if (matchingAccounts)
        {
            Folder("install", "accounts", "one");
            Folder("install", "accounts", "two");
        }
        var saves = Folder("install", "shared");
        Write(saves, "slot.sav");
        var game = new CatalogGame("Game", [], [], null,
            [new("<base>/accounts/*/../../shared/*.sav", ["save"], [])], [], []);
        var result = new GameSaveDiscovery().Find(new("Game", install), game, new([], [], []));
        if (!matchingAccounts) Assert.Empty(result.Candidates);
        else
        {
            var candidate = Assert.Single(result.Candidates);
            Assert.Equal(saves, candidate.Root);
            Assert.Equal("*.sav", Assert.Single(candidate.Includes));
        }
    }

    [Fact]
    public void ParentNormalizationDoesNotHideASymbolicLinkComponent()
    {
        if (OperatingSystem.IsWindows()) return;
        var install = Folder("install");
        var outside = Folder("outside");
        Directory.CreateSymbolicLink(Path.Combine(install, "linked"), outside);
        Write(Folder("install", "saves"), "slot.sav");
        var game = new CatalogGame("Game", [], [], null,
            [new("<base>/linked/../saves", ["save"], [])], [], []);
        var result = new GameSaveDiscovery().Find(new("Game", install), game, new([], [], []));
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Notices, notice => notice.Contains("Symbolic links", StringComparison.Ordinal));
    }

    private sealed class ReplyHandler(string text) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8) });
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
