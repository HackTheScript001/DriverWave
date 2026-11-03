using System.Windows;
using System.Windows.Controls;
using DriverWave.Services;

namespace DriverWave.Views;

public partial class SettingsView : UserControl
{
    private readonly bool _ready;

    public SettingsView()
    {
        InitializeComponent();
        VersionLine.Text = $"Version {AppInfo.VersionText}";
        CheckOnStart.IsChecked = Settings.Current.CheckForUpdates;
        InstallOnClose.IsChecked = Settings.Current.InstallUpdatesOnClose;
        _ready = true;
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        Settings.Current.CheckForUpdates = CheckOnStart.IsChecked == true;
        Settings.Current.InstallUpdatesOnClose = InstallOnClose.IsChecked == true;
        Settings.Current.Save();
    }

    private async void OnCheckNow(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow main) return;
        CheckButton.IsEnabled = false;
        try { await main.CheckUpdatesAsync(manual: true); }
        finally { CheckButton.IsEnabled = true; }
    }

    private void OnOpenBackups(object sender, RoutedEventArgs e) => SystemTools.OpenInExplorer(SystemTools.BackupsRoot);

    private void OnOpenLog(object sender, RoutedEventArgs e) => SystemTools.OpenUrl(Log.FilePath);

    private void OnReport(object sender, RoutedEventArgs e) =>
        SystemTools.OpenUrl($"https://github.com/{AppInfo.GitHubOwner}/{AppInfo.GitHubRepo}/issues/new");

    private void OnLicence(object sender, RoutedEventArgs e) =>
        SystemTools.OpenUrl($"https://github.com/{AppInfo.GitHubOwner}/{AppInfo.GitHubRepo}/blob/main/LICENSE");
}
