using SaveVault.Core;

namespace SaveVault.App.Services;

public interface IUserDialogs
{
    string? PickFolder(string title);
    string? PickFile(string title, string filter);
    bool Confirm(string title, string message);
    void ShowError(string message);
    bool ConfirmRestore(RestorePlan plan, RestoreConflict conflict);
}
