using System.IO.Enumeration;
using System.Security.Cryptography;

namespace SaveVault.Core;

/// <summary>A versioned, content-addressed repository. Objects are immutable; manifests commit last.</summary>
public sealed class BackupRepository
{
    public string Destination { get; }
    public string Root { get; }

    public BackupRepository(string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        Destination = PathSafety.Full(Environment.ExpandEnvironmentVariables(destination));
        Root = Path.Combine(Destination, "SaveVaultRepository");
    }

    public static void ValidateProfile(BackupProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Destination);
        if (profile.Id == Guid.Empty || profile.Sources.Count == 0)
            throw new ArgumentException("A profile needs an ID and at least one source folder.");
        if (profile.Sources.Select(source => source.Id).Distinct().Count() != profile.Sources.Count
            || profile.Sources.Any(source => source.Id == Guid.Empty))
            throw new ArgumentException("Source folder IDs must be unique and non-empty.");
        var destination = PathSafety.Full(Environment.ExpandEnvironmentVariables(profile.Destination));
        PathSafety.RejectLinks(destination);
        var sources = new List<string>();
        foreach (var source in profile.Sources)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(source.Name);
            foreach (var include in source.Includes) SourceSelection.Validate(include);
            var path = PathSafety.Full(Environment.ExpandEnvironmentVariables(source.Path));
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Source folder not found: {path}");
            PathSafety.RejectLinks(path);
            if (PathSafety.Contains(path, destination) || PathSafety.Contains(destination, path))
                throw new ArgumentException("Source and backup destination folders must not overlap.");
            if (sources.Any(other => PathSafety.Contains(other, path) || PathSafety.Contains(path, other)))
                throw new ArgumentException("Source folders must not overlap each other.");
            sources.Add(path);
        }
    }

    public async Task<BackupResult> BackupAsync(BackupProfile profile,
        IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateProfile(profile);
        if (!PathSafety.Full(Environment.ExpandEnvironmentVariables(profile.Destination))
            .Equals(Destination, PathSafety.Comparison))
            throw new ArgumentException("Profile destination does not match this repository.");
        using var repositoryLock = AcquireLock(cancellationToken);
        var snapshot = new BackupSnapshot
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            Sources = profile.Sources.Select(source => source with
            {
                Path = PathSafety.Full(Environment.ExpandEnvironmentVariables(source.Path))
            }).ToList()
        };
        var newObjects = 0;
        var reusedObjects = 0;
        long processedBytes = 0;
        var verifiedHashes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in snapshot.Sources)
        {
            foreach (var (path, isDirectory) in Walk(source.Path, source.Includes, profile.Exclusions,
                snapshot.SkippedLinks, cancellationToken))
            {
                var relative = Path.GetRelativePath(source.Path, path).Replace('\\', '/');
                PathSafety.SafeRelative(relative);
                if (isDirectory)
                {
                    snapshot.Directories.Add(new(source.Id, relative));
                    continue;
                }
                progress?.Report(new("Backing up", relative, snapshot.Files.Count, processedBytes));
                var stored = await StoreFileAsync(path, verifiedHashes, cancellationToken);
                snapshot.Files.Add(new(source.Id, relative, stored.Hash, stored.Size, stored.LastWriteTimeUtc));
                if (stored.IsNew) newObjects++; else reusedObjects++;
                processedBytes += stored.Size;
                progress?.Report(new("Backed up", relative, snapshot.Files.Count, processedBytes));
            }
        }

        ValidateSnapshot(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        // No manifest becomes visible until every referenced object has been verified.
        await JsonStorage.WriteAsync(ManifestPath(snapshot), snapshot, cancellationToken);
        return new(snapshot, newObjects, reusedObjects);
    }

    public async Task<IReadOnlyList<BackupSnapshot>> ListSnapshotsAsync(Guid? profileId = null,
        CancellationToken cancellationToken = default)
    {
        PathSafety.RejectLinks(Root);
        var directory = Path.Combine(Root, "snapshots");
        if (!Directory.Exists(directory)) return [];
        PathSafety.RejectLinks(directory);
        var result = new List<BackupSnapshot>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PathSafety.RejectLinks(path);
            var snapshot = await JsonStorage.ReadAsync<BackupSnapshot>(path, cancellationToken);
            ValidateSnapshot(snapshot);
            if (profileId is null || snapshot.ProfileId == profileId) result.Add(snapshot);
        }
        return result.OrderByDescending(snapshot => snapshot.CreatedUtc).ToList();
    }

    public async Task<VerificationResult> VerifyAsync(BackupSnapshot snapshot,
        IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateSnapshot(snapshot);
        using var repositoryLock = AcquireLock(cancellationToken);
        var issues = new List<VerificationIssue>();
        var files = snapshot.Files.GroupBy(file => file.Hash).Select(group => group.First());
        var count = 0;
        long bytes = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await VerifyObjectAsync(file.Hash, file.Size, cancellationToken); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                issues.Add(new(file.RelativePath, exception.Message));
            }
            count++;
            bytes += file.Size;
            progress?.Report(new("Verifying", file.RelativePath, count, bytes));
        }
        return new(count, issues);
    }

    public RestorePlan PlanRestore(BackupSnapshot snapshot, string destination,
        IEnumerable<SnapshotFile>? selectedFiles = null)
    {
        ValidateSnapshot(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        destination = PathSafety.Full(destination);
        if (PathSafety.Contains(Destination, destination) || PathSafety.Contains(destination, Destination))
            throw new ArgumentException("Restore destination must not overlap the backup destination.");
        // First milestone restores to an alternate location, never directly into live source data.
        if (snapshot.Sources.Any(source => PathSafety.Contains(source.Path, destination)
            || PathSafety.Contains(destination, source.Path)))
            throw new ArgumentException("Choose a restore folder separate from the original source folders.");
        PathSafety.RejectLinks(destination);
        if (File.Exists(destination)) throw new IOException("Restore destination is a file, not a folder.");
        var selected = selectedFiles?.ToList() ?? snapshot.Files.ToList();
        var knownFiles = snapshot.Files.ToHashSet();
        if (selected.Any(file => !knownFiles.Contains(file)) || selected.Distinct().Count() != selected.Count)
            throw new ArgumentException("Selected files must be unique members of this snapshot.");
        var sources = snapshot.Sources.ToDictionary(source => source.Id);
        var items = selected.Select(file =>
        {
            var relative = Path.Combine(SourceDirectory(sources[file.SourceId]), file.RelativePath);
            var target = PathSafety.Resolve(destination, relative);
            if (Directory.Exists(target)) throw new IOException($"A folder occupies a file restore path: {target}");
            return new RestoreItem(file, target, File.Exists(target));
        }).ToList();
        var directories = new HashSet<string>(PathSafety.Comparer);
        foreach (var item in items) directories.Add(Path.GetDirectoryName(item.TargetPath)!);
        if (selectedFiles is null)
        {
            directories.UnionWith(SnapshotRestoreDirectories(snapshot, destination));
        }
        foreach (var directory in directories)
        {
            PathSafety.RejectLinks(directory);
            if (File.Exists(directory)) throw new IOException($"A file occupies a folder restore path: {directory}");
        }
        return new(snapshot, destination, items, directories.ToList());
    }

    private static HashSet<string> SnapshotRestoreDirectories(BackupSnapshot snapshot, string destination)
    {
        // Metadata-only allowlist: unselected destination entries must not affect a selective restore.
        // The caller validates the snapshot first; link/conflict checks apply to directories actually used.
        var sources = snapshot.Sources.ToDictionary(source => source.Id, SourceDirectory);
        var directories = sources.Values.Select(source => PathSafety.Full(Path.Combine(destination, source)))
            .ToHashSet(PathSafety.Comparer);
        foreach (var directory in snapshot.Directories)
            directories.Add(PathSafety.Full(Path.Combine(destination, sources[directory.SourceId],
                PathSafety.SafeRelative(directory.RelativePath))));
        foreach (var file in snapshot.Files)
            directories.Add(Path.GetDirectoryName(PathSafety.Full(Path.Combine(destination, sources[file.SourceId],
                PathSafety.SafeRelative(file.RelativePath))))!);
        return directories;
    }

    public async Task<RestoreResult> RestoreAsync(RestorePlan plan,
        RestoreConflict conflict = RestoreConflict.Skip,
        IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(conflict)) throw new ArgumentOutOfRangeException(nameof(conflict));
        // Rebuild targets rather than trusting caller-supplied paths or stale conflict flags.
        var refreshed = PlanRestore(plan.Snapshot, plan.Destination, plan.Items.Select(item => item.File));
        var allowedDirectories = SnapshotRestoreDirectories(plan.Snapshot, refreshed.Destination);
        foreach (var directory in plan.Directories)
        {
            if (!allowedDirectories.Contains(directory))
                throw new InvalidDataException("Restore directory is not part of this snapshot.");
            PathSafety.RejectLinks(directory);
            if (File.Exists(directory)) throw new IOException($"A file occupies a folder restore path: {directory}");
        }
        using var repositoryLock = AcquireLock(cancellationToken);
        // Verify every selected object before changing the restore destination.
        foreach (var file in refreshed.Items.Select(item => item.File).DistinctBy(file => file.Hash))
            await VerifyObjectAsync(file.Hash, file.Size, cancellationToken);

        var restored = 0;
        var skipped = 0;
        long bytes = 0;
        string? rollback = null;
        foreach (var directory in plan.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PathSafety.RejectLinks(directory);
            Directory.CreateDirectory(directory);
        }
        foreach (var item in refreshed.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PathSafety.RejectLinks(item.TargetPath);
            if (File.Exists(item.TargetPath) && conflict == RestoreConflict.Skip)
            {
                skipped++;
                continue;
            }
            var parent = Path.GetDirectoryName(item.TargetPath)!;
            Directory.CreateDirectory(parent);
            var temporary = Path.Combine(parent, ".savevault-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                // The staged copy is hashed too; corrupt or changed objects never reach the target.
                var copy = await CopyAndHashAsync(ObjectPath(item.File.Hash), temporary, cancellationToken);
                if (copy.Hash != item.File.Hash || copy.Size != item.File.Size)
                    throw new InvalidDataException($"Object changed during restore: {item.File.RelativePath}");
                File.SetLastWriteTimeUtc(temporary, item.File.LastWriteTimeUtc);
                cancellationToken.ThrowIfCancellationRequested();
                PathSafety.RejectLinks(item.TargetPath);
                if (File.Exists(item.TargetPath))
                {
                    if (conflict == RestoreConflict.Skip) { skipped++; continue; }
                    rollback ??= Path.Combine(plan.Destination, ".savevault-rollback-" + Guid.NewGuid().ToString("N"));
                    var saved = PathSafety.Resolve(rollback, Path.GetRelativePath(plan.Destination, item.TargetPath));
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    File.Copy(item.TargetPath, saved, overwrite: false);
                    // Never delete the previous version; keep a durable rollback copy before replacement.
                    using (var durable = new FileStream(saved, FileMode.Open, FileAccess.Write, FileShare.None))
                        durable.Flush(flushToDisk: true);
                    await JsonStorage.WriteAsync(Path.Combine(rollback, "restore-info.json"), new
                    {
                        SnapshotId = plan.Snapshot.Id,
                        Destination = plan.Destination,
                        Description = "Previous files, preserved at their relative restore paths."
                    }, CancellationToken.None);
                }
                PathSafety.RejectLinks(item.TargetPath);
                File.Move(temporary, item.TargetPath, overwrite: conflict == RestoreConflict.OverwriteWithRollback);
                restored++;
                bytes += item.File.Size;
                progress?.Report(new("Restoring", item.File.RelativePath, restored, bytes));
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        return new(restored, skipped, rollback);
    }

    private FileStream AcquireLock(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PathSafety.RejectLinks(Root);
        Directory.CreateDirectory(Root);
        try
        {
            var path = Path.Combine(Root, ".operation.lock");
            PathSafety.RejectLinks(path);
            return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new IOException("Cannot lock the backup repository. Another operation may be running, or the drive may be unavailable.", exception);
        }
    }

    private string ManifestPath(BackupSnapshot snapshot) =>
        PathSafety.Resolve(Root, $"snapshots/{snapshot.Id:N}.json");

    private string ObjectPath(string hash)
    {
        ValidateHash(hash);
        return PathSafety.Resolve(Root, $"objects/{hash[..2]}/{hash}");
    }

    private async Task<(string Hash, long Size, DateTime LastWriteTimeUtc, bool IsNew)> StoreFileAsync(
        string source, HashSet<string> verifiedHashes, CancellationToken cancellationToken)
    {
        PathSafety.RejectLinks(source);
        var before = new FileInfo(source);
        var size = before.Length;
        var modified = before.LastWriteTimeUtc;
        var work = PathSafety.Resolve(Root, "work");
        Directory.CreateDirectory(work);
        var temporary = Path.Combine(work, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var copy = await CopyAndHashAsync(source, temporary, cancellationToken);
            var after = new FileInfo(source);
            if (size != copy.Size || size != after.Length || modified != after.LastWriteTimeUtc)
                throw new IOException($"File changed during backup; close its application and retry: {source}");
            var target = ObjectPath(copy.Hash);
            var isNew = !File.Exists(target);
            if (isNew)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(temporary, target);
            }
            if (verifiedHashes.Add(copy.Hash)) await VerifyObjectAsync(copy.Hash, copy.Size, cancellationToken);
            return (copy.Hash, copy.Size, modified, isNew);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task VerifyObjectAsync(string hash, long size, CancellationToken cancellationToken)
    {
        var path = ObjectPath(hash);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != size) throw new InvalidDataException($"Backup object has an incorrect size: {hash}");
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        if (actual != hash) throw new InvalidDataException($"Backup object failed its SHA-256 check: {hash}");
    }

    private static async Task<(string Hash, long Size)> CopyAndHashAsync(string source, string target,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            131072, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[131072];
        long size = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            size += count;
        }
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), size);
    }

    private static IEnumerable<(string Path, bool IsDirectory)> Walk(string root,
        IReadOnlyList<string> includes, IReadOnlyList<string> exclusions, List<string> skippedLinks, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PathSafety.RejectLinks(directory);
            // Do not silently ignore permission errors or disconnected folders.
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(PathSafety.Comparer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                var excluded = exclusions.Any(pattern => FileSystemName.MatchesSimpleExpression(
                    pattern.Replace('\\', '/'), pattern.Contains('/') || pattern.Contains('\\')
                        ? relative : Path.GetFileName(path), ignoreCase: true));
                if (excluded) continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    skippedLinks.Add(path);
                    continue;
                }
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (!SourceSelection.Matches(relative, isDirectory, includes)) continue;
                yield return (path, isDirectory);
                if (isDirectory) pending.Push(path);
            }
        }
    }

    private static void ValidateHash(string hash)
    {
        if (hash is null || hash.Length != 64 || hash.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("Invalid content hash in snapshot.");
    }

    private static void ValidateSnapshot(BackupSnapshot snapshot)
    {
        if (snapshot.FormatVersion != 1) throw new InvalidDataException("Unsupported backup format version.");
        if (snapshot.Id == Guid.Empty || snapshot.ProfileId == Guid.Empty || snapshot.Sources is null
            || snapshot.Files is null || snapshot.Directories is null || snapshot.Sources.Count == 0)
            throw new InvalidDataException("Snapshot metadata is incomplete.");
        var sourceIds = snapshot.Sources.Select(source => source.Id).ToHashSet();
        if (sourceIds.Contains(Guid.Empty) || sourceIds.Count != snapshot.Sources.Count)
            throw new InvalidDataException("Invalid or duplicated source IDs in snapshot.");
        foreach (var source in snapshot.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Name) || !Path.IsPathFullyQualified(source.Path))
                throw new InvalidDataException("Invalid source metadata in snapshot.");
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashSizes = new Dictionary<string, long>();
        foreach (var file in snapshot.Files)
        {
            ValidateHash(file.Hash);
            if (!sourceIds.Contains(file.SourceId) || file.Size < 0
                || !paths.Add(file.SourceId + "/" + PathSafety.SafeRelative(file.RelativePath)))
                throw new InvalidDataException("Invalid or duplicated file in snapshot.");
            if (hashSizes.TryGetValue(file.Hash, out var size) && size != file.Size)
                throw new InvalidDataException("Inconsistent object sizes in snapshot.");
            hashSizes[file.Hash] = file.Size;
            filePaths.Add(file.SourceId + "/" + file.RelativePath.Replace('\\', '/'));
        }
        foreach (var directory in snapshot.Directories)
            if (!sourceIds.Contains(directory.SourceId)
                || !paths.Add(directory.SourceId + "/" + PathSafety.SafeRelative(directory.RelativePath)))
                throw new InvalidDataException("Invalid or duplicated directory in snapshot.");
        foreach (var path in snapshot.Files.Select(file => (file.SourceId, file.RelativePath))
            .Concat(snapshot.Directories.Select(directory => (directory.SourceId, directory.RelativePath))))
        {
            var relative = path.RelativePath.Replace('\\', '/');
            while (relative.LastIndexOf('/') is var separator && separator >= 0)
            {
                relative = relative[..separator];
                if (filePaths.Contains(path.SourceId + "/" + relative))
                    throw new InvalidDataException("A file is used as a parent folder in snapshot.");
            }
        }
    }

    private static string SourceDirectory(SourceFolder source)
    {
        var label = new string(source.Name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(40).ToArray());
        return $"{(label.Length == 0 ? "Source" : label)}-{source.Id:N}";
    }
}
