using System.Windows;
using Microsoft.Win32;
using SaveVault.Core;

namespace SaveVault.App.Services;

public sealed class WindowsDialogs : IUserDialogs
{
    public string? PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, Multiselect = false, CheckFileExists = true };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
    }

    public bool Confirm(string title, string message) => MessageBox.Show(Application.Current.MainWindow,
        message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void ShowError(string message) => MessageBox.Show(Application.Current.MainWindow,
        message, "SaveVault", MessageBoxButton.OK, MessageBoxImage.Error);

    public bool ConfirmRestore(RestorePlan plan, RestoreConflict conflict)
    {
        var window = new RestorePreviewWindow(plan, conflict) { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true;
    }
}
