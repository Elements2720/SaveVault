using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SaveVault.Core;
using Xunit;

namespace SaveVault.Core.Tests;

public sealed class BackupRepositoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SaveVaultTests", Guid.NewGuid().ToString("N"));

    private (BackupProfile Profile, BackupRepository Repository, string Source) Setup(params string[] exclusions)
    {
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        var profile = new BackupProfile
        {
            Name = "Game saves",
            Kind = ProfileKind.GameSaves,
            Destination = Path.Combine(root, "backup"),
            Sources = [new SourceFolder { Name = "My game", Path = source }],
            Exclusions = exclusions.ToList()
        };
        return (profile, new(profile.Destination), source);
    }

    private static string ObjectPath(BackupRepository repository, SnapshotFile file) =>
        Path.Combine(repository.Root, "objects", file.Hash[..2], file.Hash);

    [Fact]
    public async Task ChangedAndDeletedFilesKeepOlderVersionsAndReuseIdenticalContent()
    {
        var (profile, repository, source) = Setup();
        await File.WriteAllTextAsync(Path.Combine(source, "save.dat"), "level 1");
        await File.WriteAllTextAsync(Path.Combine(source, "settings.ini"), "volume=80");
        var first = await repository.BackupAsync(profile);
        await File.WriteAllTextAsync(Path.Combine(source, "save.dat"), "level 2");
        var second = await repository.BackupAsync(profile);
        Assert.Equal(2, first.NewObjects);
        Assert.Equal(1, second.NewObjects);
        Assert.Equal(1, second.ReusedObjects);
        File.Delete(Path.Combine(source, "settings.ini"));
        var third = await repository.BackupAsync(profile);
        Assert.Single(third.Snapshot.Files);
        Assert.Equal(3, (await repository.ListSnapshotsAsync(profile.Id)).Count);
        var restore = repository.PlanRestore(first.Snapshot, Path.Combine(root, "restored"));
        await repository.RestoreAsync(restore);
        Assert.Equal("level 1", await File.ReadAllTextAsync(restore.Items.Single(item => item.File.RelativePath == "save.dat").TargetPath));
        Assert.Equal("volume=80", await File.ReadAllTextAsync(restore.Items.Single(item => item.File.RelativePath == "settings.ini").TargetPath));
    }

    [Fact]
    public async Task DuplicateContentIsStoredOnceAcrossFilesAndProfiles()
    {
        var (profile, repository, source) = Setup();
        await File.WriteAllTextAsync(Path.Combine(source, "one.dat"), "same content");
        await File.WriteAllTextAsync(Path.Combine(source, "two.dat"), "same content");
        var result = await repository.BackupAsync(profile);
        Assert.Equal(1, result.NewObjects);
        Assert.Equal(1, result.ReusedObjects);
        var another = await repository.BackupAsync(profile with { Id = Guid.NewGuid(), Name = "Another profile" });
        Assert.Equal(0, another.NewObjects);
        Assert.Equal(2, another.ReusedObjects);
        Assert.Single(await repository.ListSnapshotsAsync(profile.Id));
    }

    [Fact]
    public async Task SelectiveRestoreSkipsConflictsAndOverwritePreservesRollback()
    {
        var (profile, repository, source) = Setup();
        await File.WriteAllTextAsync(Path.Combine(source, "one.dat"), "backed up");
        await File.WriteAllTextAsync(Path.Combine(source, "two.dat"), "other");
        var snapshot = (await repository.BackupAsync(profile)).Snapshot;
        var plan = repository.PlanRestore(snapshot, Path.Combine(root, "restored"),
            snapshot.Files.Where(file => file.RelativePath == "one.dat"));
        Assert.Single(plan.Items);
        await repository.RestoreAsync(plan);
        await File.WriteAllTextAsync(plan.Items[0].TargetPath, "current data");
        var skipped = await repository.RestoreAsync(plan);
        Assert.Equal(1, skipped.SkippedFiles);
        Assert.Equal("current data", await File.ReadAllTextAsync(plan.Items[0].TargetPath));
        var overwrite = await repository.RestoreAsync(plan, RestoreConflict.OverwriteWithRollback);
        Assert.Equal("backed up", await File.ReadAllTextAsync(plan.Items[0].TargetPath));
        Assert.NotNull(overwrite.RollbackFolder);
        var oldPath = Path.Combine(overwrite.RollbackFolder,
            Path.GetRelativePath(plan.Destination, plan.Items[0].TargetPath));
        Assert.Equal("current data", await File.ReadAllTextAsync(oldPath));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(plan.Items[0].TargetPath)!, "two.dat")));
    }

    [Fact]
    public async Task CorruptionIsDetectedBeforeAnyRestoreFileIsWritten()
    {
        var (profile, repository, source) = Setup();
        await File.WriteAllTextAsync(Path.Combine(source, "one.dat"), "good");
        await File.WriteAllTextAsync(Path.Combine(source, "two.dat"), "also good");
        var snapshot = (await repository.BackupAsync(profile)).Snapshot;
        await File.WriteAllTextAsync(ObjectPath(repository, snapshot.Files[1]), "corrupt!!");
        var verification = await repository.VerifyAsync(snapshot);
        Assert.False(verification.IsValid);
        Assert.Single(verification.Issues);
        var destination = Path.Combine(root, "restored");
        var plan = repository.PlanRestore(snapshot, destination);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.RestoreAsync(plan));
        Assert.False(Directory.Exists(destination));
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.BackupAsync(profile));
        Assert.Single(await repository.ListSnapshotsAsync());
    }

    [Fact]
    public async Task SelectiveRestoreIgnoresADirectoryAtAnUnselectedFileTarget()
    {
        var (profile, repository, source) = Setup();
        await File.WriteAllTextAsync(Path.Combine(source, "one.dat"), "one");
        await File.WriteAllTextAsync(Path.Combine(source, "two.dat"), "two");
        var snapshot = (await repository.BackupAsync(profile)).Snapshot;
        var destination = Path.Combine(root, "restored");
        var full = repository.PlanRestore(snapshot, destination);
        var unrelated = full.Items.Single(item => item.File.RelativePath == "two.dat").TargetPath;
        Directory.CreateDirectory(unrelated);
        var plan = repository.PlanRestore(snapshot, destination, snapshot.Files.Where(file => file.RelativePath == "one.dat"));
        var result = await repository.RestoreAsync(plan);
        Assert.Equal(1, result.RestoredFiles);
        Assert.Equal("one", await File.ReadAllTextAsync(Assert.Single(plan.Items).TargetPath));
        Assert.True(Directory.Exists(unrelated));
    }

    [Fact]
    public async Task SelectiveRestoreIgnoresAFileAtAnUnselectedEmptyFolderTarget()
    {
        var (profile, repository, source) = Setup();
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(Path.Combine(source, "one.dat"), "one");
        var snapshot = (await repository.BackupAsync(profile)).Snapshot;
        var destination = Path.Combine(root, "restored");
        var full = repository.PlanRestore(snapshot, destination);
        var unrelated = full.Directories.Single(directory => Path.GetFileName(directory) == "empty");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
        await File.WriteAllTextAsync(unrelated, "leave this alone");
        var plan = repository.PlanRestore(snapshot, destination, snapshot.Files);
        var result = await repository.RestoreAsync(plan);
        Assert.Equal(1, result.RestoredFiles);
        Assert.Equal("leave this alone", await File.ReadAllTextAsync(unrelated));
    }

    [Fact]
    public async Task RestoreRejectsADirectoryOutsideTheSnapshotBeforeWritingFiles()
    {
        var (profile, repository, source) = Setup();
        await File.WriteAllTextAsync(Path.Combine(source, "save.dat"), "data");
        var snapshot = (await repository.BackupAsync(profile)).Snapshot;
        var destination = Path.Combine(root, "restored");
        var plan = repository.PlanRestore(snapshot, destination);
        var tampered = plan with { Directories = [.. plan.Directories, Path.Combine(destination, "invented")] };
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.RestoreAsync(tampered));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task RestoreRechecksSelectedEmptyFolderConflictsBeforeWritingAnyFile()
    {
        var (profile, repository, source) = Setup();
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(Path.Combine(source, "one.dat"), "one");
        var snapshot = (await repository.BackupAsync(profile)).Snapshot;
        var plan = repository.PlanRestore(snapshot, Path.Combine(root, "restored"));
        var required = plan.Directories.Single(directory => Path.GetFileName(directory) == "empty");
        Directory.CreateDirectory(Path.GetDirectoryName(required)!);
        await File.WriteAllTextAsync(required, "blocker");
        await Assert.ThrowsAsync<IOException>(() => repository.RestoreAsync(plan));
        Assert.False(File.Exists(Assert.Single(plan.Items).TargetPath));
        Assert.Equal("blocker", await File.ReadAllTextAsync(required));
    }

    [Fact]
    public async Task MissingObjectsAreReportedAsVerificationFailures()
    {
        var (profile, repository, source) = Setup();
        await File.WriteAllTextAsync(Path.Combine(source, "save.dat"), "data");
        var snapshot = (await repository.BackupAsync(profile)).Snapshot;
        File.Delete(ObjectPath(repository, snapshot.Files[0]));
        Assert.False((await repository.VerifyAsync(snapshot)).IsValid);
    }

    [Fact]
    public async Task ExclusionsPruneDirectoriesAndEmptyFoldersAndTimestampsAreRestored()
    {
        var (profile, repository, source) = Setup("*.tmp", "cache");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        Directory.CreateDirectory(Path.Combine(source, "cache"));
        await File.WriteAllTextAsync(Path.Combine(source, "cache", "large.dat"), "skip");
        await File.WriteAllTextAsync(Path.Combine(source, "scratch.tmp"), "skip");
        var path = Path.Combine(source, "song.flac");
        await File.WriteAllTextAsync(path, "music");
        var timestamp = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, timestamp);
        var snapshot = (await repository.BackupAsync(profile)).Snapshot;
        Assert.Single(snapshot.Files);
        Assert.Single(snapshot.Directories);
        var plan = repository.PlanRestore(snapshot, Path.Combine(root, "restored"));
        await repository.RestoreAsync(plan);
        Assert.Contains(plan.Directories, directory => Path.GetFileName(directory) == "empty" && Directory.Exists(directory));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(plan.Items[0].TargetPath));
    }

    [Fact]
    public async Task CancellationNeverPublishesAnIncompleteSnapshot()
    {
        var (profile, repository, source) = Setup();
        await File.WriteAllTextAsync(Path.Combine(source, "save.dat"), "data");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(_ => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.BackupAsync(profile, progress, cancellation.Token));
        Assert.Empty(await repository.ListSnapshotsAsync());
        Assert.Empty(Directory.GetFiles(repository.Root, "*.tmp", SearchOption.AllDirectories));
        Assert.Single((await repository.BackupAsync(profile)).Snapshot.Files);
    }

    [Fact]
    public void RecursiveBackupAndNestedSourcesAreRejected()
    {
        var (profile, _, source) = Setup();
        Assert.Throws<ArgumentException>(() => BackupRepository.ValidateProfile(profile with
        {
            Destination = Path.Combine(source, "backups")
        }));
        var nested = Path.Combine(source, "nested");
        Directory.CreateDirectory(nested);
        Assert.Throws<ArgumentException>(() => BackupRepository.ValidateProfile(profile with
        {
            Sources = [.. profile.Sources, new SourceFolder { Name = "Nested", Path = nested }]
        }));
    }

    [Theory]
    [InlineData("../escape.dat")]
    [InlineData("..\\escape.dat")]
    [InlineData("/outside.dat")]
    [InlineData("C:\\outside.dat")]
    [InlineData("folder/CON.txt")]
    [InlineData("file.dat:secret")]
    [InlineData("folder./file.dat")]
    public async Task UnsafeManifestPathsAreRejected(string relative)
    {
        var (profile, repository, source) = Setup();
        await File.WriteAllTextAsync(Path.Combine(source, "save.dat"), "data");
        var snapshot = (await repository.BackupAsync(profile)).Snapshot;
        var malicious = snapshot with { Files = [snapshot.Files[0] with { RelativePath = relative }] };
        Assert.Throws<InvalidDataException>(() => repository.PlanRestore(malicious, Path.Combine(root, "restored")));
    }

    [Fact]
    public async Task RestoreCannotTargetOriginalSourceOrBackupStorage()
    {
        var (profile, repository, source) = Setup();
        var snapshot = (await repository.BackupAsync(profile)).Snapshot;
        Assert.Throws<ArgumentException>(() => repository.PlanRestore(snapshot, source));
        Assert.Throws<ArgumentException>(() => repository.PlanRestore(snapshot, repository.Root));
    }

    [Fact]
    public async Task BackupSkipsLinksAndRestoreRejectsLinkedDestination()
    {
        if (OperatingSystem.IsWindows()) return; // Creating Windows links may require developer mode/admin.
        var (profile, repository, source) = Setup();
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "private.dat"), "outside");
        Directory.CreateSymbolicLink(Path.Combine(source, "linked"), outside);
        await File.WriteAllTextAsync(Path.Combine(source, "save.dat"), "data");
        var snapshot = (await repository.BackupAsync(profile)).Snapshot;
        Assert.Single(snapshot.SkippedLinks);
        Assert.Single(snapshot.Files);
        var restoreLink = Path.Combine(root, "restore-link");
        Directory.CreateSymbolicLink(restoreLink, outside);
        Assert.Throws<IOException>(() => repository.PlanRestore(snapshot, restoreLink));
    }

    [Fact]
    public async Task RepositoryLockPreventsConcurrentWriters()
    {
        var (profile, repository, source) = Setup();
        Directory.CreateDirectory(repository.Root);
        using var heldLock = new FileStream(Path.Combine(repository.Root, ".operation.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await File.WriteAllTextAsync(Path.Combine(source, "save.dat"), "data");
        await Assert.ThrowsAsync<IOException>(() => repository.BackupAsync(profile));
        Assert.Empty(await repository.ListSnapshotsAsync());
    }

    [Fact]
    public async Task ProfileStorageRoundTripsAndDoesNotHideInvalidJson()
    {
        var (profile, _, _) = Setup();
        var path = Path.Combine(root, "profiles.json");
        var store = new ProfileStore(path);
        Assert.Empty(await store.LoadAsync());
        await store.SaveAsync([profile]);
        var loaded = Assert.Single(await store.LoadAsync());
        Assert.Equal(profile.Id, loaded.Id);
        Assert.Equal(profile.Sources[0].Id, loaded.Sources[0].Id);
        await File.WriteAllTextAsync(path, "not json");
        await Assert.ThrowsAsync<JsonException>(() => store.LoadAsync());
    }

    [Fact]
    public async Task SnapshotHashMatchesStandardSha256()
    {
        var (profile, repository, source) = Setup();
        await File.WriteAllTextAsync(Path.Combine(source, "save.dat"), "abc", new UTF8Encoding(false));
        var file = Assert.Single((await repository.BackupAsync(profile)).Snapshot.Files);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("abc"))), file.Hash);
    }

    [Fact]
    public async Task ReopenedRepositoryCanRestoreAfterOriginalSourceIsGone()
    {
        var (profile, repository, source) = Setup();
        await File.WriteAllTextAsync(Path.Combine(source, "save.dat"), "portable backup");
        await repository.BackupAsync(profile);
        Directory.Delete(source, recursive: true);
        var reopened = new BackupRepository(profile.Destination);
        var snapshot = Assert.Single(await reopened.ListSnapshotsAsync());
        var plan = reopened.PlanRestore(snapshot, Path.Combine(root, "recovered"));
        await reopened.RestoreAsync(plan);
        Assert.Equal("portable backup", await File.ReadAllTextAsync(plan.Items[0].TargetPath));
    }

    [Fact]
    public async Task SameNamedSourcesRestoreIntoDistinctFolders()
    {
        var (profile, repository, source) = Setup();
        var second = Path.Combine(root, "second-source");
        Directory.CreateDirectory(second);
        await File.WriteAllTextAsync(Path.Combine(source, "save.dat"), "first game");
        await File.WriteAllTextAsync(Path.Combine(second, "save.dat"), "second game");
        profile = profile with
        {
            Sources = [.. profile.Sources, new SourceFolder { Name = profile.Sources[0].Name, Path = second }]
        };
        var snapshot = (await repository.BackupAsync(profile)).Snapshot;
        var plan = repository.PlanRestore(snapshot, Path.Combine(root, "restored"));
        await repository.RestoreAsync(plan);
        Assert.Equal(2, plan.Items.Select(item => item.TargetPath).Distinct().Count());
        var contents = new List<string>();
        foreach (var item in plan.Items) contents.Add(await File.ReadAllTextAsync(item.TargetPath));
        Assert.Contains("first game", contents);
        Assert.Contains("second game", contents);
    }

    [Fact]
    public async Task InterruptedRestorePreservesCompletedFilesAndCleansTemporaryCopies()
    {
        var (profile, repository, source) = Setup();
        await File.WriteAllTextAsync(Path.Combine(source, "one.dat"), "one");
        await File.WriteAllTextAsync(Path.Combine(source, "two.dat"), "two");
        var snapshot = (await repository.BackupAsync(profile)).Snapshot;
        var destination = Path.Combine(root, "restored");
        var plan = repository.PlanRestore(snapshot, destination);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.RestoreAsync(plan,
            progress: new InlineProgress(_ => cancellation.Cancel()), cancellationToken: cancellation.Token));
        Assert.True(File.Exists(plan.Items[0].TargetPath));
        Assert.False(File.Exists(plan.Items[1].TargetPath));
        Assert.Empty(Directory.GetFiles(destination, "*.tmp", SearchOption.AllDirectories));
        var resumed = await repository.RestoreAsync(plan);
        Assert.Equal(1, resumed.RestoredFiles);
        Assert.Equal(1, resumed.SkippedFiles);
    }

    [Fact]
    public async Task ManifestCannotUseAFileAsAParentDirectory()
    {
        var (profile, repository, source) = Setup();
        await File.WriteAllTextAsync(Path.Combine(source, "save.dat"), "data");
        var snapshot = (await repository.BackupAsync(profile)).Snapshot;
        var malformed = snapshot with
        {
            Files = [snapshot.Files[0], snapshot.Files[0] with { RelativePath = "save.dat/child.dat" }]
        };
        Assert.Throws<InvalidDataException>(() => repository.PlanRestore(malformed, Path.Combine(root, "restored")));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class InlineProgress(Action<OperationProgress> report) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => report(value);
    }
}
