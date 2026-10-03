using System.ComponentModel;
using System.Windows;
using SaveVault.App.ViewModels;

namespace SaveVault.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) await viewModel.InitializeAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        if (!viewModel.IsBusy) { viewModel.Games.Dispose(); return; }
        e.Cancel = true;
        MessageBox.Show(this, "Cancel the current operation and wait for it to finish before closing SaveVault.",
            "Operation in progress", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
