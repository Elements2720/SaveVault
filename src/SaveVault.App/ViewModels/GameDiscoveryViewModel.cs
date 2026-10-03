using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using SaveVault.App.Infrastructure;
using SaveVault.App.Services;
using SaveVault.Core.Games;

namespace SaveVault.App.ViewModels;

public sealed class GameDiscoveryViewModel : ObservableObject, IDisposable
{
    private readonly GameCatalogStore catalogStore;
    private readonly GameDiscoverySettingsStore settingsStore;
    private readonly IUserDialogs dialogs;
    private readonly Func<Func<CancellationToken, Task>, Task> run;
    private readonly Func<bool> idle;
    private readonly Action<string> log;
    private readonly Action<GameInstallation, List<SaveCandidate>> useSources;
    private readonly DiscoveryEnvironment environment;
    private readonly string settingsDirectory;
    private readonly GameSaveDiscovery discovery = new();
    private GameCatalog? catalog;
    private SaveWatchSession? watch;
    private Dictionary<string, string> catalogOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GameInstallation> manualInstallations = new(StringComparer.OrdinalIgnoreCase);
    private bool applyingMatch;
    private GameInstallation? selectedGame;
    private CatalogGame? selectedCatalogGame;
    private string libraryFolders = @"G:\Games";
    private string watchFolders;
    private string catalogSearch = "";
    private string catalogStatus = "Loading local catalog…";
    private string summary = "Discover your game library or add a game manually.";
    private string notices = "";

    public ObservableCollection<GameInstallation> Games { get; } = [];
    public ObservableCollection<CatalogGame> CatalogMatches { get; } = [];
    public ObservableCollection<SaveCandidateSelection> Candidates { get; } = [];
    public RelayCommand BrowseLibraryCommand { get; }
    public RelayCommand DiscoverCommand { get; }
    public RelayCommand AddGameCommand { get; }
    public RelayCommand AddExecutableCommand { get; }
    public RelayCommand UpdateCatalogCommand { get; }
    public RelayCommand FindSavesCommand { get; }
    public RelayCommand SearchMoreCommand { get; }
    public RelayCommand StartWatchCommand { get; }
    public RelayCommand FinishWatchCommand { get; }
    public RelayCommand DiscardWatchCommand { get; }
    public RelayCommand AddWatchFolderCommand { get; }
    public RelayCommand UseSourcesCommand { get; }

    public GameDiscoveryViewModel(string settingsDirectory, IUserDialogs dialogs, Func<Func<CancellationToken, Task>, Task> run,
        Func<bool> idle, Action<string> log, Action<GameInstallation, List<SaveCandidate>> useSources,
        DiscoveryEnvironment environment)
    {
        this.settingsDirectory = settingsDirectory;
        this.dialogs = dialogs;
        this.run = run;
        this.idle = idle;
        this.log = log;
        this.useSources = useSources;
        catalogStore = new(Path.Combine(settingsDirectory, "game-catalog.json"));
        settingsStore = new(Path.Combine(settingsDirectory, "game-discovery.json"));
        this.environment = environment;
        watchFolders = string.Join(Environment.NewLine, environment.SearchRoots);
        BrowseLibraryCommand = new(() => AddFolder(true), () => InputsEnabled);
        AddWatchFolderCommand = new(() => AddFolder(false), () => InputsEnabled);
        DiscoverCommand = new(() => _ = DiscoverAsync(), () => InputsEnabled);
        AddGameCommand = new(AddGame, () => InputsEnabled);
        AddExecutableCommand = new(AddExecutable, () => InputsEnabled);
        UpdateCatalogCommand = new(() => _ = UpdateCatalogAsync(), () => InputsEnabled);
        FindSavesCommand = new(() => _ = FindAsync(false), () => InputsEnabled && SelectedGame is not null);
        SearchMoreCommand = new(() => _ = FindAsync(true), () => InputsEnabled && SelectedGame is not null);
        StartWatchCommand = new(() => _ = StartWatchAsync(), () => InputsEnabled && SelectedGame is not null);
        FinishWatchCommand = new(() => _ = FinishWatchAsync(), () => idle() && IsWatching);
        DiscardWatchCommand = new(DiscardWatch, () => idle() && IsWatching);
        UseSourcesCommand = new(() => useSources(SelectedGame!, Candidates.Where(candidate => candidate.IsSelected).Select(candidate => candidate.Candidate).ToList()),
            () => InputsEnabled && SelectedGame is not null && Candidates.Any(candidate => candidate.IsSelected));
    }

    public bool IsWatching => watch is not null;
    public bool InputsEnabled => idle() && !IsWatching;
    public string LibraryFolders { get => libraryFolders; set => Set(ref libraryFolders, value); }
    public string WatchFolders { get => watchFolders; set => Set(ref watchFolders, value); }
    public string CatalogStatus { get => catalogStatus; private set => Set(ref catalogStatus, value); }
    public string Summary { get => summary; private set => Set(ref summary, value); }
    public string Notices { get => notices; private set => Set(ref notices, value); }
    public string CatalogSearch
    {
        get => catalogSearch;
        set { if (Set(ref catalogSearch, value)) RefreshCatalogMatches(); }
    }
    public GameInstallation? SelectedGame
    {
        get => selectedGame;
        set
        {
            if (!Set(ref selectedGame, value)) return;
            Candidates.Clear();
            Notices = "";
            ApplyCatalogMatch();
            RefreshCommands();
        }
    }
    public CatalogGame? SelectedCatalogGame
    {
        get => selectedCatalogGame;
        set
        {
            if (!Set(ref selectedCatalogGame, value)) return;
            if (!applyingMatch && SelectedGame is not null && value is not null)
                catalogOverrides[SelectedGame.Path] = value.Title;
            Candidates.Clear();
            Summary = value is null ? "Choose a matching catalog title or use search/watch discovery."
                : $"Matched {value.Title}. Click Find save locations.";
            RefreshCommands();
        }
    }

    public async Task InitializeAsync(CancellationToken token)
    {
        var settings = await settingsStore.LoadAsync(token);
        LibraryFolders = string.Join(Environment.NewLine, settings.LibraryRoots);
        if (settings.WatchRoots.Count > 0) WatchFolders = string.Join(Environment.NewLine, settings.WatchRoots);
        catalogOverrides = new(settings.CatalogOverrides, StringComparer.OrdinalIgnoreCase);
        foreach (var stored in settings.ManualInstallations)
        {
            var game = stored.Store == "added" ? stored with { Store = "manual" } : stored;
            manualInstallations[game.Path] = game;
            Games.Add(game);
        }
        catalog = await Task.Run(() => catalogStore.LoadAsync(token), token);
        UpdateCatalogStatus();
        RefreshCatalogMatches();
        SelectedGame = Games.FirstOrDefault();
    }

    private Task DiscoverAsync() => run(async token =>
    {
        await SaveSettingsAsync(token);
        var roots = ParseFolders(LibraryFolders);
        var warnings = roots.Where(root => !Directory.Exists(root)).Select(root => $"Library folder unavailable: {root}").ToList();
        var found = await Task.Run(() => GameLibraryDiscovery.Discover(roots, environment.SteamRoots, warnings, token), token);
        var previous = SelectedGame?.Path;
        Games.Clear();
        foreach (var game in found.Concat(manualInstallations.Values).DistinctBy(game => game.Path, StringComparer.OrdinalIgnoreCase).ToList())
        {
            Games.Add(game);
            if (manualInstallations.ContainsKey(game.Path)) manualInstallations[game.Path] = game;
        }
        SelectedGame = Games.FirstOrDefault(game => game.Path.Equals(previous, StringComparison.OrdinalIgnoreCase)) ?? Games.FirstOrDefault();
        Summary = $"Found {Games.Count} game/library folder(s). Select a game and review its catalog match.";
        Notices = string.Join(Environment.NewLine, warnings.Distinct().Take(100));
        log(Summary);
    });

    private void AddFolder(bool library)
    {
        var path = dialogs.PickFolder(library ? "Choose a game-library folder" : "Choose a folder to watch/search for saves");
        if (path is null) return;
        if (library) LibraryFolders = string.Join(Environment.NewLine, ParseFolders(LibraryFolders).Append(path).Distinct(StringComparer.OrdinalIgnoreCase));
        else WatchFolders = string.Join(Environment.NewLine, ParseFolders(WatchFolders).Append(path).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private void AddGame()
    {
        var path = dialogs.PickFolder("Choose this game's installation folder");
        if (path is not null) AddInstallation(path);
    }
    private void AddExecutable()
    {
        var path = dialogs.PickFile("Choose the game's executable", "Game executables (*.exe)|*.exe");
        if (path is null) return;
        var installation = Path.GetDirectoryName(path)!;
        foreach (var library in ParseFolders(LibraryFolders))
        {
            if (!Path.IsPathFullyQualified(library)) continue;
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(library)) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var parts = path[prefix.Length..].Split(Path.DirectorySeparatorChar);
            installation = parts.Length > 1 ? Path.Combine(library, parts[0]) : Path.GetDirectoryName(path)!;
            break;
        }
        AddInstallation(installation);
    }
    private void AddInstallation(string path)
    {
        var existing = Games.FirstOrDefault(game => game.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            manualInstallations[path] = existing;
            SelectedGame = existing;
            return;
        }
        var title = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var game = new GameInstallation(string.IsNullOrEmpty(title) ? path : title, path);
        manualInstallations[path] = game;
        Games.Add(game);
        SelectedGame = game;
    }

    private Task UpdateCatalogAsync() => run(async token =>
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        catalog = await Task.Run(() => catalogStore.UpdateAsync(client, token), token);
        UpdateCatalogStatus();
        ApplyCatalogMatch();
        log($"Updated save catalog: {catalog.Games.Count:N0} titles. Cached for offline use.");
    });

    private Task FindAsync(bool deep) => run(async token =>
    {
        await SaveSettingsAsync(token);
        var game = SelectedGame!;
        var matched = SelectedCatalogGame;
        var context = environment with { SearchRoots = environment.SearchRoots.Concat(ParseFolders(WatchFolders)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
        var result = await Task.Run(() => discovery.Find(game, matched, context, deep, token), token);
        ShowResults(result);
        Summary = $"Found {Candidates.Count} candidate location(s) for {game.Title}. Check only the saves/configuration you want.";
        log(Summary);
    });

    private Task StartWatchAsync() => run(async token =>
    {
        await SaveSettingsAsync(token);
        var roots = ParseFolders(WatchFolders).Append(SelectedGame!.Path).ToList();
        watch = await Task.Run(() => SaveWatchSession.Start(roots, [settingsDirectory], token), token);
        Notify(nameof(IsWatching));
        Candidates.Clear();
        Summary = "Watching. Launch the game yourself, make a manual save, exit it, then click Finish & review changes.";
        Notices = "Changes from other applications can appear too. A final rescan runs even if watcher events overflow. All results need your selection.";
        log($"Started save-location watch for {SelectedGame.Title}.");
        RefreshCommands();
    });

    private Task FinishWatchAsync() => run(async token =>
    {
        var current = watch!;
        try
        {
            var result = await Task.Run(() => current.Finish(token), token);
            ShowResults(result);
            Summary = $"Found {Candidates.Count} changed file(s). Select the files that belong to this game.";
            log(Summary);
        }
        finally { current.Dispose(); watch = null; Notify(nameof(IsWatching)); RefreshCommands(); }
    });

    private void DiscardWatch()
    {
        watch?.Dispose();
        watch = null;
        Notify(nameof(IsWatching));
        Summary = "Watch session discarded.";
        RefreshCommands();
    }

    private void ShowResults(DiscoveryResult result)
    {
        Candidates.Clear();
        foreach (var candidate in result.Candidates)
        {
            var selection = new SaveCandidateSelection(candidate);
            selection.PropertyChanged += (_, _) => UseSourcesCommand.Refresh();
            Candidates.Add(selection);
        }
        Notices = string.Join(Environment.NewLine, result.Notices);
        RefreshCommands();
    }

    private void ApplyCatalogMatch()
    {
        applyingMatch = true;
        try
        {
            SelectedCatalogGame = null;
            CatalogSearch = SelectedGame?.Title ?? "";
            RefreshCatalogMatches();
            var match = SelectedGame is null || catalog is null ? null
                : catalog.Games.FirstOrDefault(game => game.Title.Equals(catalogOverrides.GetValueOrDefault(SelectedGame.Path), StringComparison.OrdinalIgnoreCase))
                    ?? discovery.Match(SelectedGame, catalog);
            if (match is not null && !CatalogMatches.Contains(match)) CatalogMatches.Add(match);
            SelectedCatalogGame = match;
        }
        finally { applyingMatch = false; }
    }
    private void RefreshCatalogMatches()
    {
        var selected = SelectedCatalogGame;
        CatalogMatches.Clear();
        if (catalog is null) return;
        foreach (var game in catalog.Games.Where(game => game.Title.Contains(CatalogSearch, StringComparison.OrdinalIgnoreCase)
            || game.Aliases.Any(alias => alias.Contains(CatalogSearch, StringComparison.OrdinalIgnoreCase))).Take(200)) CatalogMatches.Add(game);
        if (selected is not null && !CatalogMatches.Contains(selected)) CatalogMatches.Add(selected);
    }
    private void UpdateCatalogStatus() => CatalogStatus = $"{catalog!.Source} · {catalog.Games.Count:N0} titles · {catalog.UpdatedUtc:yyyy-MM-dd} · offline cache";
    private Task SaveSettingsAsync(CancellationToken token) => settingsStore.SaveAsync(new()
    {
        LibraryRoots = ParseFolders(LibraryFolders), WatchRoots = ParseFolders(WatchFolders),
        ManualInstallations = manualInstallations.Values.ToList(),
        CatalogOverrides = new(catalogOverrides, StringComparer.OrdinalIgnoreCase)
    }, token);
    private static List<string> ParseFolders(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(Environment.ExpandEnvironmentVariables).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public void RefreshCommands()
    {
        Notify(nameof(InputsEnabled));
        RelayCommand?[] commands = [BrowseLibraryCommand, DiscoverCommand, AddGameCommand, AddExecutableCommand,
            UpdateCatalogCommand, FindSavesCommand, SearchMoreCommand, StartWatchCommand, FinishWatchCommand,
            DiscardWatchCommand, AddWatchFolderCommand, UseSourcesCommand];
        foreach (var command in commands) command?.Refresh();
    }

    public void Dispose() => watch?.Dispose();
}
