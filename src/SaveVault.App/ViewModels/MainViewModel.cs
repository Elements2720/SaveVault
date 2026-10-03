using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using SaveVault.App.Infrastructure;
using SaveVault.App.Services;
using SaveVault.Core;
using SaveVault.Core.Games;

namespace SaveVault.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly ProfileStore store;
    private readonly IUserDialogs dialogs;
    private readonly ActivityLog log;
    private CancellationTokenSource? operation;
    private BackupProfile? selectedProfile;
    private BackupSnapshot? selectedSnapshot;
    private SourceFolder? selectedSource;
    private Guid editorId = Guid.NewGuid();
    private string profileName = "My backup";
    private string destination = "";
    private string exclusions = "*.tmp";
    private ProfileKind kind;
    private bool isBusy;
    private bool profilesLoaded;
    private bool overwriteConflicts;
    private string status = "Create a profile to protect your files.";
    private string activity = "Ready.";
    private string restoreDestination = "";
    private string repositoryDestination = "";
    private bool browsingRepository;
    private bool changingFileSelection;
    private int selectedTab;
    private string? gameInstallationPath;

    public GameDiscoveryViewModel Games { get; }
    public int SelectedTab { get => selectedTab; set => Set(ref selectedTab, value); }

    public ObservableCollection<BackupProfile> Profiles { get; } = [];
    public ObservableCollection<SourceFolder> Sources { get; } = [];
    public ObservableCollection<BackupSnapshot> Snapshots { get; } = [];
    public ObservableCollection<FileSelection> Files { get; } = [];
    public ObservableCollection<string> Activity { get; } = [];
    public ProfileKind[] ProfileKinds { get; } = Enum.GetValues<ProfileKind>();

    public RelayCommand NewProfileCommand { get; }
    public RelayCommand SaveProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public RelayCommand AddSourceCommand { get; }
    public RelayCommand AddFileCommand { get; }
    public RelayCommand RemoveSourceCommand { get; }
    public RelayCommand BrowseDestinationCommand { get; }
    public RelayCommand BackupCommand { get; }
    public RelayCommand RefreshHistoryCommand { get; }
    public RelayCommand OpenRepositoryCommand { get; }
    public RelayCommand VerifyCommand { get; }
    public RelayCommand BrowseRestoreCommand { get; }
    public RelayCommand RestoreCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenLogsCommand { get; }

    public MainViewModel(ProfileStore store, IUserDialogs dialogs, ActivityLog log, string settingsDirectory,
        DiscoveryEnvironment gameEnvironment)
    {
        this.store = store;
        this.dialogs = dialogs;
        this.log = log;
        Games = new(settingsDirectory, dialogs, RunAsync, () => IsIdle, message => Log(message), UseGameSources, gameEnvironment);
        NewProfileCommand = new(NewProfile, () => IsIdle && profilesLoaded);
        SaveProfileCommand = new(() => _ = SaveProfileAsync(), () => IsIdle && profilesLoaded);
        DeleteProfileCommand = new(() => _ = DeleteProfileAsync(), () => IsIdle && SelectedProfile is not null);
        AddSourceCommand = new(AddSource, () => IsIdle);
        AddFileCommand = new(AddFile, () => IsIdle);
        RemoveSourceCommand = new(() => { if (SelectedSource is not null) Sources.Remove(SelectedSource); },
            () => IsIdle && SelectedSource is not null);
        BrowseDestinationCommand = new(() =>
        {
            var path = dialogs.PickFolder("Choose a backup destination (external drive or network folder)");
            if (path is not null) Destination = path;
        }, () => IsIdle);
        BackupCommand = new(() => _ = BackupAsync(), () => IsIdle && SelectedProfile is not null);
        RefreshHistoryCommand = new(() => _ = RefreshHistoryAsync(), () => IsIdle && (SelectedProfile is not null || browsingRepository));
        OpenRepositoryCommand = new(() => _ = OpenRepositoryAsync(), () => IsIdle);
        VerifyCommand = new(() => _ = VerifyAsync(), () => IsIdle && SelectedSnapshot is not null);
        BrowseRestoreCommand = new(() =>
        {
            var path = dialogs.PickFolder("Choose an alternate restore folder");
            if (path is not null) RestoreDestination = path;
        }, () => IsIdle);
        RestoreCommand = new(() => _ = RestoreAsync(), () => IsIdle && SelectedSnapshot is not null
            && !string.IsNullOrWhiteSpace(RestoreDestination)
            && (Files.Any(file => file.IsSelected) || Files.Count == 0));
        SelectAllCommand = new(() => SetFileSelection(true), () => IsIdle && Files.Count > 0);
        SelectNoneCommand = new(() => SetFileSelection(false), () => IsIdle && Files.Count > 0);
        CancelCommand = new(() => operation?.Cancel(), () => IsBusy);
        OpenLogsCommand = new(() =>
        {
            try
            {
                Directory.CreateDirectory(log.DirectoryPath);
                Process.Start(new ProcessStartInfo(log.DirectoryPath) { UseShellExecute = true });
            }
            catch (Exception exception) { dialogs.ShowError(exception.Message); }
        });
    }

    public BackupProfile? SelectedProfile
    {
        get => selectedProfile;
        set
        {
            if (!Set(ref selectedProfile, value)) return;
            LoadEditor(value);
            browsingRepository = false;
            repositoryDestination = value?.Destination ?? "";
            Snapshots.Clear();
            SelectedSnapshot = null;
            Notify(nameof(HistorySummary));
            RefreshCommands();
            if (value is not null && IsIdle) _ = RefreshHistoryAsync();
        }
    }

    public BackupSnapshot? SelectedSnapshot
    {
        get => selectedSnapshot;
        set
        {
            if (!Set(ref selectedSnapshot, value)) return;
            Files.Clear();
            if (value is not null)
            {
                var names = value.Sources.ToDictionary(source => source.Id, source => source.Name);
                foreach (var file in value.Files)
                {
                    var selection = new FileSelection(file, names[file.SourceId]);
                    selection.PropertyChanged += (_, _) =>
                    {
                        if (changingFileSelection) return;
                        Notify(nameof(SelectionSummary));
                        RefreshCommands();
                    };
                    Files.Add(selection);
                }
            }
            Notify(nameof(SelectionSummary));
            RefreshCommands();
        }
    }

    public SourceFolder? SelectedSource
    {
        get => selectedSource;
        set { if (Set(ref selectedSource, value)) RefreshCommands(); }
    }

    public string ProfileName { get => profileName; set => Set(ref profileName, value); }
    public ProfileKind Kind { get => kind; set => Set(ref kind, value); }
    public string Destination { get => destination; set => Set(ref destination, value); }
    public string Exclusions { get => exclusions; set => Set(ref exclusions, value); }
    public bool OverwriteConflicts { get => overwriteConflicts; set => Set(ref overwriteConflicts, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string CurrentActivity { get => activity; private set => Set(ref activity, value); }
    public string RestoreDestination
    {
        get => restoreDestination;
        set { if (Set(ref restoreDestination, value)) RefreshCommands(); }
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!Set(ref isBusy, value)) return;
            Notify(nameof(IsIdle));
            RefreshCommands();
        }
    }
    public bool IsIdle => !IsBusy;
    public string HistorySummary => browsingRepository
        ? $"{Snapshots.Count} snapshot(s) in {repositoryDestination}"
        : $"{Snapshots.Count} snapshot(s) for the selected saved profile";
    public string SelectionSummary => $"{Files.Count(file => file.IsSelected)} of {Files.Count} files selected · "
        + FormatBytes(Files.Where(file => file.IsSelected).Sum(file => file.File.Size));

    public Task InitializeAsync() => RunAsync(async token =>
    {
        foreach (var profile in await store.LoadAsync(token)) Profiles.Add(profile);
        await Games.InitializeAsync(token);
        profilesLoaded = true;
        if (Profiles.Count > 0)
        {
            SelectedProfile = Profiles[0];
            await LoadHistoryAsync(token);
        }
        Status = Profiles.Count == 0 ? "Create your first profile, then run a backup." : "Ready to back up.";
        Log("SaveVault started.");
    });

    private void NewProfile()
    {
        SelectedProfile = null;
        LoadEditor(null);
        browsingRepository = false;
        repositoryDestination = "";
        Snapshots.Clear();
        SelectedSnapshot = null;
        Notify(nameof(HistorySummary));
        RefreshCommands();
        Status = "Choose source folders and a destination, then save this profile.";
    }

    private void LoadEditor(BackupProfile? profile)
    {
        editorId = profile?.Id ?? Guid.NewGuid();
        gameInstallationPath = profile?.GameInstallationPath;
        ProfileName = profile?.Name ?? "My backup";
        Kind = profile?.Kind ?? ProfileKind.PersonalFiles;
        Destination = profile?.Destination ?? "";
        Exclusions = profile is null ? "*.tmp" : string.Join(Environment.NewLine, profile.Exclusions);
        Sources.Clear();
        SelectedSource = null;
        if (profile is not null) foreach (var source in profile.Sources) Sources.Add(source);
    }

    private void AddSource()
    {
        var path = dialogs.PickFolder("Choose a folder to protect (files, music, or a game-save folder)");
        if (path is null) return;
        var candidate = new SaveCandidate(path, [], DiscoveryConfidence.PossibleLocation, "Manually selected", "Folder", 0, 0, null);
        var merged = GameProfileSources.Merge([candidate], Sources);
        Sources.Clear();
        foreach (var source in merged) Sources.Add(source);
    }

    private void AddFile()
    {
        var path = dialogs.PickFile("Choose a file to protect", "All files (*.*)|*.*");
        if (path is null) return;
        var candidate = new SaveCandidate(Path.GetDirectoryName(path)!, [Path.GetFileName(path)],
            DiscoveryConfidence.PossibleLocation, "Manually selected", "File", 1, 0, null);
        var merged = GameProfileSources.Merge([candidate], Sources);
        Sources.Clear();
        foreach (var source in merged) Sources.Add(source);
    }

    private void UseGameSources(GameInstallation game, List<SaveCandidate> candidates) => _ = RunAsync(async token =>
    {
        var reuseDraft = gameInstallationPath?.Equals(game.Path, StringComparison.OrdinalIgnoreCase) == true;
        var existing = reuseDraft ? SelectedProfile
            : Profiles.FirstOrDefault(profile => profile.GameInstallationPath?.Equals(game.Path, StringComparison.OrdinalIgnoreCase) == true);
        var previousDestination = Destination;
        if (!reuseDraft)
        {
            if (existing is not null) { SelectedProfile = existing; LoadEditor(existing); }
            else { NewProfile(); ProfileName = game.Title + " — saves"; Destination = previousDestination; }
        }
        browsingRepository = false;
        repositoryDestination = existing?.Destination ?? "";
        gameInstallationPath = game.Path;
        Kind = ProfileKind.GameSaves;
        var merged = GameProfileSources.Merge(candidates, Sources);
        Sources.Clear();
        foreach (var source in merged) Sources.Add(source);
        SelectedTab = 0;
        if (existing is not null) await LoadHistoryAsync(token);
        Status = "Game-save profile draft ready. Review sources and destination, then Save profile.";
        Log($"Selected {candidates.Count} save candidate(s) for {game.Title}; existing confirmed sources retained.");
    });

    private Task SaveProfileAsync() => RunAsync(async token =>
    {
        var profile = new BackupProfile
        {
            Id = editorId,
            Name = ProfileName.Trim(),
            Kind = Kind,
            Destination = Environment.ExpandEnvironmentVariables(Destination.Trim()),
            Sources = Sources.ToList(),
            Exclusions = Exclusions.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            GameInstallationPath = gameInstallationPath
        };
        await Task.Run(() => BackupRepository.ValidateProfile(profile), token);
        var updated = Profiles.Where(existing => existing.Id != profile.Id).Append(profile).ToList();
        await store.SaveAsync(updated, token);
        var previous = Profiles.FirstOrDefault(existing => existing.Id == profile.Id);
        if (previous is not null) Profiles[Profiles.IndexOf(previous)] = profile;
        else Profiles.Add(profile);
        SelectedProfile = profile;
        await LoadHistoryAsync(token);
        Status = $"Saved profile: {profile.Name}";
        Log(Status);
    });

    private Task DeleteProfileAsync() => RunAsync(async token =>
    {
        var profile = SelectedProfile!;
        if (!dialogs.Confirm("Remove profile", $"Remove '{profile.Name}' from the profile list?\n\nIts backup snapshots will remain on the destination drive.")) return;
        await store.SaveAsync(Profiles.Where(existing => existing.Id != profile.Id), token);
        Profiles.Remove(profile);
        SelectedProfile = Profiles.FirstOrDefault();
        if (SelectedProfile is not null) await LoadHistoryAsync(token);
        Status = $"Removed profile: {profile.Name}";
        Log(Status);
    });

    private Task BackupAsync() => RunAsync(async token =>
    {
        var profile = SelectedProfile!;
        Log($"Backup started: {profile.Name}");
        var progress = Progress();
        var result = await Task.Run(() => new BackupRepository(profile.Destination).BackupAsync(profile, progress, token), token);
        browsingRepository = false;
        await LoadHistoryAsync(token);
        SelectedSnapshot = Snapshots.FirstOrDefault(snapshot => snapshot.Id == result.Snapshot.Id);
        Status = $"Backup complete · {result.Snapshot.Files.Count} files · {result.NewObjects} new / {result.ReusedObjects} reused objects";
        Log(Status);
        if (result.Snapshot.SkippedLinks.Count > 0)
        {
            Status += $" · {result.Snapshot.SkippedLinks.Count} links skipped";
            foreach (var link in result.Snapshot.SkippedLinks) Log($"Skipped symbolic link/junction: {link}");
        }
    });

    private Task RefreshHistoryAsync() => RunAsync(async token =>
    {
        await LoadHistoryAsync(token);
        Status = HistorySummary;
    });

    private async Task LoadHistoryAsync(CancellationToken token)
    {
        var profile = SelectedProfile;
        if (!browsingRepository && profile is null) return;
        if (!browsingRepository) repositoryDestination = profile!.Destination;
        Guid? profileId = browsingRepository ? null : profile!.Id;
        var snapshots = await Task.Run(() => new BackupRepository(repositoryDestination).ListSnapshotsAsync(profileId, token), token);
        Snapshots.Clear();
        foreach (var snapshot in snapshots) Snapshots.Add(snapshot);
        SelectedSnapshot = Snapshots.FirstOrDefault();
        Notify(nameof(HistorySummary));
    }

    private Task OpenRepositoryAsync() => RunAsync(async token =>
    {
        var path = dialogs.PickFolder("Choose the backup destination containing the SaveVaultRepository folder");
        if (path is null) return;
        if (!Directory.Exists(Path.Combine(path, "SaveVaultRepository", "snapshots")))
            throw new DirectoryNotFoundException("No SaveVault snapshots were found. Select the parent folder of SaveVaultRepository.");
        repositoryDestination = path;
        browsingRepository = true;
        Snapshots.Clear();
        SelectedSnapshot = null;
        await LoadHistoryAsync(token);
        Status = HistorySummary;
        Log("Opened backup repository: " + path);
    });

    private Task VerifyAsync() => RunAsync(async token =>
    {
        var snapshot = SelectedSnapshot!;
        var progress = Progress();
        var repository = new BackupRepository(repositoryDestination);
        var result = await Task.Run(() => repository.VerifyAsync(snapshot, progress, token), token);
        Status = result.IsValid ? $"Verified {result.CheckedObjects} objects. All checks passed."
            : $"Verification failed: {result.Issues.Count} object(s) need attention. See activity log.";
        Log(Status);
        foreach (var issue in result.Issues) Log($"{issue.Item}: {issue.Message}");
        if (!result.IsValid) dialogs.ShowError(Status);
    });

    private Task RestoreAsync() => RunAsync(async token =>
    {
        var snapshot = SelectedSnapshot!;
        var selected = Files.Where(file => file.IsSelected).Select(file => file.File).ToList();
        var repository = new BackupRepository(repositoryDestination);
        var plan = await Task.Run(() => repository.PlanRestore(snapshot, RestoreDestination,
            selected.Count == snapshot.Files.Count ? null : selected), token);
        var conflict = OverwriteConflicts ? RestoreConflict.OverwriteWithRollback : RestoreConflict.Skip;
        if (!dialogs.ConfirmRestore(plan, conflict)) return;
        Log($"Restore started: snapshot {snapshot.Id}");
        var progress = Progress();
        var result = await Task.Run(() => repository.RestoreAsync(plan, conflict, progress, token), token);
        Status = $"Restore complete · {result.RestoredFiles} restored · {result.SkippedFiles} skipped";
        Log(Status);
        if (result.RollbackFolder is not null) Log($"Previous files preserved in: {result.RollbackFolder}");
    });

    private IProgress<OperationProgress> Progress() => new Progress<OperationProgress>(progress =>
    {
        CurrentActivity = $"{progress.Stage}: {progress.Item} · {progress.CompletedFiles} files · {FormatBytes(progress.ProcessedBytes)}";
    });

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        operation = new CancellationTokenSource();
        IsBusy = true;
        Status = "Working…";
        try { await action(operation.Token); }
        catch (OperationCanceledException)
        {
            Status = "Operation cancelled. Completed snapshots and previously restored files are preserved.";
            Log(Status);
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            Log($"Operation failed: {exception.Message}", exception);
            dialogs.ShowError(exception.Message);
        }
        finally
        {
            operation.Dispose();
            operation = null;
            IsBusy = false;
            CurrentActivity = "Ready.";
        }
    }

    private void SetFileSelection(bool selected)
    {
        changingFileSelection = true;
        try { foreach (var file in Files) file.IsSelected = selected; }
        finally { changingFileSelection = false; }
        Notify(nameof(SelectionSummary));
        RefreshCommands();
    }

    private void Log(string message, Exception? exception = null)
    {
        Activity.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (Activity.Count > 200) Activity.RemoveAt(Activity.Count - 1);
        log.Write(message, exception);
    }

    private void RefreshCommands()
    {
        // Commands are not all assigned while constructor-time bindings initialize.
        RelayCommand?[] commands = [NewProfileCommand, SaveProfileCommand, DeleteProfileCommand,
            AddSourceCommand, AddFileCommand, RemoveSourceCommand, BrowseDestinationCommand, BackupCommand,
            RefreshHistoryCommand, OpenRepositoryCommand, VerifyCommand, BrowseRestoreCommand, RestoreCommand,
            SelectAllCommand, SelectNoneCommand, CancelCommand];
        foreach (var command in commands) command?.Refresh();
        Games?.RefreshCommands();
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}
