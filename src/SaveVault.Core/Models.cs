namespace SaveVault.Core;

public enum ProfileKind { PersonalFiles, GameSaves, Music }

public sealed record SourceFolder
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required string Path { get; init; }
    // Empty means the whole folder. Entries select source-relative files/folders;
    // * and ? match within a path component. Selecting a folder includes its subtree.
    public List<string> Includes { get; init; } = [];
    public string SelectionSummary => Includes.Count == 0 ? "Entire folder" : "Only: " + string.Join(", ", Includes);
}

public sealed record BackupProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public ProfileKind Kind { get; init; }
    public required string Destination { get; init; }
    public List<SourceFolder> Sources { get; init; } = [];
    public List<string> Exclusions { get; init; } = [];
    public string? GameInstallationPath { get; init; }
}

public sealed record SnapshotFile(Guid SourceId, string RelativePath, string Hash,
    long Size, DateTime LastWriteTimeUtc);

public sealed record SnapshotDirectory(Guid SourceId, string RelativePath);

public sealed record BackupSnapshot
{
    public int FormatVersion { get; init; } = 1;
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ProfileId { get; init; }
    public required string ProfileName { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public List<SourceFolder> Sources { get; init; } = [];
    public List<SnapshotFile> Files { get; init; } = [];
    public List<SnapshotDirectory> Directories { get; init; } = [];
    public List<string> SkippedLinks { get; init; } = [];
    public long TotalBytes => Files.Sum(file => file.Size);
}

public sealed record OperationProgress(string Stage, string Item, int CompletedFiles, long ProcessedBytes);
public sealed record BackupResult(BackupSnapshot Snapshot, int NewObjects, int ReusedObjects);
public sealed record VerificationIssue(string Item, string Message);
public sealed record VerificationResult(int CheckedObjects, IReadOnlyList<VerificationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public enum RestoreConflict { Skip, OverwriteWithRollback }
public sealed record RestoreItem(SnapshotFile File, string TargetPath, bool AlreadyExists);
public sealed record RestorePlan(BackupSnapshot Snapshot, string Destination,
    IReadOnlyList<RestoreItem> Items, IReadOnlyList<string> Directories)
{
    public int Conflicts => Items.Count(item => item.AlreadyExists);
    public long TotalBytes => Items.Sum(item => item.File.Size);
}
public sealed record RestoreResult(int RestoredFiles, int SkippedFiles, string? RollbackFolder);
