using System.Windows;
using System.Windows.Controls;
using DriverWave.Services;
using DriverWave.Views;

namespace DriverWave;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages = [];
    private AppUpdate? _appUpdate;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{AppInfo.VersionText}  ·  © {DateTime.Now.Year} SecondWave IT Systems";
        Log.LineWritten += line => Dispatcher.InvokeAsync(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ShowPage("drivers");
        _ = CheckForAppUpdateAsync();
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (PageHost == null) return; // fires once during startup, before the page host exists
        if (sender is RadioButton { Tag: string key }) ShowPage(key);
    }

    private void ShowPage(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "graphics" => new GraphicsView(),
                "backup" => new BackupView(),
                "uninstaller" => new UninstallerView(),
                "cleanup" => new CleanupView(),
                "registry" => new RegistryCleanerView(),
                "startup" => new StartupView(),
                _ => new DriversView(),
            };
            _pages[key] = page;
        }
        PageHost.Content = page;
    }

    // ───────────────────────────── App updates ─────────────────────────────

    private async Task CheckForAppUpdateAsync()
    {
        _appUpdate = await Updater.CheckAsync();
        if (_appUpdate == null)
        {
            UpdateStatus.Text = $"DriverWave {AppInfo.VersionText} – up to date";
            return;
        }
        UpdateStatus.Text = $"Update available: {_appUpdate.Version.ToString(3)}";
        UpdateBannerText.Text = $"DriverWave {_appUpdate.Version.ToString(3)} is available (you have {AppInfo.VersionText}).";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private async void OnUpdateApp(object sender, RoutedEventArgs e)
    {
        if (_appUpdate == null) return;
        if (AppState.IsBusy)
        {
            MessageBox.Show(this, "Wait for the current job to finish first.", "DriverWave");
            return;
        }

        UpdateAppButton.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => UpdateBannerText.Text = $"Downloading update… {p:P0}");
            await Updater.DownloadAndInstallAsync(_appUpdate, progress);
        }
        catch (Exception ex)
        {
            Log.Write($"Update failed: {ex}");
            UpdateBannerText.Text = $"Update failed: {ex.Message}";
            UpdateAppButton.IsEnabled = true;
        }
    }
}
