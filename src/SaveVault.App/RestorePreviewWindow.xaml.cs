using System.Windows;
using SaveVault.App.ViewModels;
using SaveVault.Core;

namespace SaveVault.App;

public partial class RestorePreviewWindow : Window
{
    public RestorePreviewWindow(RestorePlan plan, RestoreConflict conflict)
    {
        InitializeComponent();
        DataContext = new
        {
            Summary = $"{plan.Items.Count} selected file(s) · {plan.Conflicts} conflict(s) · {MainViewModel.FormatBytes(plan.TotalBytes)}",
            Destination = plan.Destination,
            ConflictDescription = conflict == RestoreConflict.Skip
                ? "Existing files will be skipped. Every selected backup object is verified before restore starts."
                : "Existing files will be replaced after their previous versions are preserved in a rollback folder.",
            plan.Items
        };
    }

    private void OnRestore(object sender, RoutedEventArgs e) => DialogResult = true;
}
