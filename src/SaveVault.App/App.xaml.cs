using System.IO;
using System.Windows;
using SaveVault.App.Services;
using SaveVault.App.ViewModels;
using SaveVault.Core;

namespace SaveVault.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SaveVault");
        var log = new ActivityLog(Path.Combine(settings, "logs"));
        var viewModel = new MainViewModel(new ProfileStore(Path.Combine(settings, "profiles.json")),
            new WindowsDialogs(), log, settings, WindowsGameEnvironment.Create());
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();
    }
}
