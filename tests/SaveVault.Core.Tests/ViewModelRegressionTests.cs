using SaveVault.App.Infrastructure;
using SaveVault.App.Services;
using SaveVault.App.ViewModels;
using SaveVault.Core.Games;
using Xunit;

namespace SaveVault.Core.Tests;

// Linked production view models use a supplied environment and fake dialogs, not WPF/Windows services.
public sealed class ViewModelRegressionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SaveVaultViewModels", Guid.NewGuid().ToString("N"));
    private readonly List<MainViewModel> models = [];
    private readonly TestDialogs dialogs = new();

    private string Folder(params string[] parts)
    {
        var path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private async Task<MainViewModel> StartAsync(DiscoveryEnvironment environment)
    {
        var settings = Folder("settings");
        var model = new MainViewModel(new ProfileStore(Path.Combine(settings, "profiles.json")), dialogs,
            new ActivityLog(Path.Combine(settings, "logs")), settings, environment);
        models.Add(model);
        await model.InitializeAsync();
        Assert.Empty(dialogs.Errors);
        return model;
    }

    private static async Task ExecuteAsync(MainViewModel model, RelayCommand command)
    {
        Assert.True(command.CanExecute(null));
        command.Execute(null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (model.IsBusy) await Task.Delay(5, timeout.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedConfirmationsKeepUnsavedSourcesAndEditorChanges(bool savedProfile)
    {
        var source = Folder("source");
        var install = Folder("install");
        await File.WriteAllTextAsync(Path.Combine(source, "one.dat"), "one");
        await File.WriteAllTextAsync(Path.Combine(source, "two.dat"), "two");
        await File.WriteAllTextAsync(Path.Combine(source, "saved.dat"), "saved");
        var store = new ProfileStore(Path.Combine(Folder("settings"), "profiles.json"));
        var original = new BackupProfile { Name = "Saved game", Kind = ProfileKind.GameSaves,
            Destination = Path.Combine(root, "original-backup"), GameInstallationPath = install,
            Sources = [new SourceFolder { Name = "Saves", Path = source, Includes = ["saved.dat"] }] };
        if (savedProfile) await store.SaveAsync([original]);
        var model = await StartAsync(new(new() { ["winAppData"] = source }, [], []));
        dialogs.Folder = install;
        model.Games.AddGameCommand.Execute(null);
        model.Games.SelectedCatalogGame = new("Test Game", [], [], null,
            [new("<winAppData>/one.dat", ["save"], []), new("<winAppData>/two.dat", ["save"], [])], [], []);
        await ExecuteAsync(model, model.Games.FindSavesCommand);
        model.Games.Candidates.Single(candidate => candidate.Candidate.Includes.Contains("one.dat")).IsSelected = true;
        await ExecuteAsync(model, model.Games.UseSourcesCommand);
        var sourceId = Assert.Single(model.Sources).Id;
        model.ProfileName = "My unsaved name";
        model.Destination = Path.Combine(root, "edited-backup");
        model.Exclusions = "cache";

        await ExecuteAsync(model, model.Games.FindSavesCommand);
        model.Games.Candidates.Single(candidate => candidate.Candidate.Includes.Contains("two.dat")).IsSelected = true;
        await ExecuteAsync(model, model.Games.UseSourcesCommand);
        var merged = Assert.Single(model.Sources);
        Assert.Equal(sourceId, merged.Id);
        Assert.Contains("one.dat", merged.Includes);
        Assert.Contains("two.dat", merged.Includes);
        Assert.Equal("My unsaved name", model.ProfileName);
        Assert.Equal(Path.Combine(root, "edited-backup"), model.Destination);
        Assert.Equal("cache", model.Exclusions);
        if (savedProfile)
        {
            Assert.Contains("saved.dat", merged.Includes);
            Assert.Equal("saved.dat", Assert.Single(Assert.Single(model.Profiles).Sources[0].Includes));
        }
        else Assert.Empty(model.Profiles);

        await ExecuteAsync(model, model.SaveProfileCommand);
        var persisted = Assert.Single(await store.LoadAsync());
        if (savedProfile) Assert.Equal(original.Id, persisted.Id);
        Assert.Contains("one.dat", persisted.Sources[0].Includes);
        Assert.Contains("two.dat", persisted.Sources[0].Includes);
        await ExecuteAsync(model, model.BackupCommand);
        var snapshot = Assert.Single(await new BackupRepository(model.Destination).ListSnapshotsAsync());
        Assert.Equal(savedProfile ? 3 : 2, snapshot.Files.Count);
        Assert.Empty(dialogs.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualMembershipSurvivesRediscoveryAndRestart(bool steamInstallation)
    {
        var library = Folder("library");
        var steam = Folder("steam");
        var install = steamInstallation ? Folder("steam", "steamapps", "common", "TestGame") : Folder("library", "TestGame");
        if (steamInstallation)
            await File.WriteAllTextAsync(Path.Combine(Folder("steam", "steamapps"), "appmanifest_123.acf"),
                "\"AppState\" { \"appid\" \"123\" \"name\" \"Test Game\" \"installdir\" \"TestGame\" }");
        var model = await StartAsync(new([], [], steamInstallation ? [steam] : []));
        model.Games.LibraryFolders = steamInstallation ? "" : library;
        if (steamInstallation) await ExecuteAsync(model, model.Games.DiscoverCommand);
        dialogs.Folder = install;
        model.Games.AddGameCommand.Execute(null);
        await ExecuteAsync(model, model.Games.DiscoverCommand);
        await ExecuteAsync(model, model.Games.DiscoverCommand);
        var store = new GameDiscoverySettingsStore(Path.Combine(root, "settings", "game-discovery.json"));
        var manual = Assert.Single((await store.LoadAsync()).ManualInstallations);
        Assert.Equal(install, manual.Path);
        Assert.Equal(steamInstallation ? "steam" : "manual", manual.Store);
        if (steamInstallation) Assert.Equal(123U, manual.SteamId);

        // Without a configured library/launcher, the explicit manual membership still restores the entry.
        var reopened = await StartAsync(new([], [], []));
        reopened.Games.LibraryFolders = "";
        await ExecuteAsync(reopened, reopened.Games.DiscoverCommand);
        await ExecuteAsync(reopened, reopened.Games.DiscoverCommand);
        Assert.Equal(install, Assert.Single(reopened.Games.Games).Path);
        Assert.Equal(install, Assert.Single((await store.LoadAsync()).ManualInstallations).Path);
        Assert.Empty(dialogs.Errors);
    }

    public void Dispose()
    {
        foreach (var model in models) model.Games.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class TestDialogs : IUserDialogs
    {
        public string? Folder { get; set; }
        public List<string> Errors { get; } = [];
        public string? PickFolder(string title) => Folder;
        public string? PickFile(string title, string filter) => null;
        public bool Confirm(string title, string message) => true;
        public void ShowError(string message) => Errors.Add(message);
        public bool ConfirmRestore(RestorePlan plan, RestoreConflict conflict) => true;
    }
}
