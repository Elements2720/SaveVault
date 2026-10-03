using SaveVault.App.Infrastructure;
using SaveVault.Core;

namespace SaveVault.App.ViewModels;

public sealed class FileSelection(SnapshotFile file, string sourceName) : ObservableObject
{
    private bool isSelected = true;
    public SnapshotFile File { get; } = file;
    public string SourceName { get; } = sourceName;
    public string RelativePath => File.RelativePath;
    public string Size => MainViewModel.FormatBytes(File.Size);
    public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }
}
