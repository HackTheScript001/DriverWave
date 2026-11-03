using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DriverWave.Services;
using DriverWave.Views;

namespace DriverWave;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages = [];

    // Update state – read by the update window and the Settings page
    public AppRelease? UpdateRelease { get; private set; }
    public string UpdatePath { get; private set; } = "";
    public string UpdateError { get; private set; } = "";
    public (long Done, long Total) UpdateDone { get; private set; }
    public event Action<long, long>? UpdateProgress;
    public event Action? UpdateStateChanged;
    private bool _downloading;

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
        UpdateStatus.Text = $"DriverWave {AppInfo.VersionText}";

        // Like Hatchling: look for an update a few seconds after opening
        if (Settings.Current.CheckForUpdates)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += async (_, _) => { t.Stop(); await CheckUpdatesAsync(manual: false); };
            t.Start();
        }
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
                "settings" => new SettingsView(),
                _ => new DriversView(),
            };
            _pages[key] = page;
        }
        PageHost.Content = page;
    }

    // ───────────────────────────── Updates ─────────────────────────────
    // A new version is downloaded in the background as soon as it's found. Clicking the update button
    // opens the "Updating DriverWave" window: it shows the rest of the download, then starts the
    // installer (its own progress window, no questions) and DriverWave reopens on the new version.

    public async Task CheckUpdatesAsync(bool manual)
    {
        if (UpdateRelease != null)
        {
            if (manual) OpenUpdateWindow();
            return;
        }

        try
        {
            var rel = await Updater.CheckAsync();
            if (rel == null)
            {
                Updater.CleanOld();
                UpdateStatus.Text = $"DriverWave {AppInfo.VersionText} – up to date";
                if (manual) MessageBox.Show(this, $"You're up to date (version {AppInfo.VersionText}).", "DriverWave");
                return;
            }

            Log.Write($"Update available: DriverWave {rel.VersionText}");
            UpdateRelease = rel;
            UpdateStatus.Text = $"Update available: {rel.VersionText}";

            if (rel.CanInstall)
            {
                ShowUpdateButtons($"Update to v{rel.VersionText}");
                StartUpdateDownload();
                if (manual) OpenUpdateWindow();
            }
            else
            {
                // Nothing we can install ourselves – fall back to the release page
                ShowUpdateButtons($"Update available: v{rel.VersionText}");
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Update check skipped: {ex.Message}");
            if (manual) MessageBox.Show(this, $"Couldn't check for updates: {ex.Message}", "DriverWave");
        }
    }

    private void ShowUpdateButtons(string text)
    {
        SidebarUpdateButton.Content = text;
        SidebarUpdateButton.Visibility = Visibility.Visible;
        UpdateAppButton.Content = text;
        UpdateBannerText.Text = $"DriverWave {UpdateRelease!.VersionText} is available (you have {AppInfo.VersionText}). It's downloading in the background.";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void SetUpdateButtonText(string text)
    {
        SidebarUpdateButton.Content = text;
        UpdateAppButton.Content = text;
    }

    public async void StartUpdateDownload()
    {
        var rel = UpdateRelease;
        if (rel == null || _downloading) return;
        _downloading = true;
        UpdateError = "";

        var progress = new Progress<(long Done, long Total)>(p =>
        {
            UpdateDone = p;
            if (UpdatePath.Length == 0)
                SetUpdateButtonText($"Update to v{rel.VersionText} ({(p.Total > 0 ? $"{100 * p.Done / p.Total}%" : $"{p.Done / 1048576} MB")})");
            UpdateProgress?.Invoke(p.Done, p.Total);
        });

        try
        {
            UpdatePath = await Task.Run(() => Updater.DownloadAsync(rel, progress));
            Log.Write($"Update v{rel.VersionText} downloaded and checked: {UpdatePath}");
            SetUpdateButtonText($"Update to v{rel.VersionText} – ready");
            UpdateBannerText.Text = $"DriverWave {rel.VersionText} is ready to install. It'll install when you close DriverWave, or click to install now.";
            Updater.CleanOld(keep: UpdatePath);
        }
        catch (Exception ex)
        {
            UpdateError = ex.Message;
            Log.Write($"Update download failed: {ex.Message}");
            SetUpdateButtonText($"Update to v{rel.VersionText} – try again");
        }
        finally
        {
            _downloading = false;
            UpdateStateChanged?.Invoke();
        }
    }

    private void OnUpdateApp(object sender, RoutedEventArgs e) => OpenUpdateWindow();

    public void OpenUpdateWindow()
    {
        var rel = UpdateRelease;
        if (rel == null) return;

        if (!rel.CanInstall)
        {
            SystemTools.OpenUrl(rel.Page.Length > 0 ? rel.Page : rel.Url);
            return;
        }
        if (AppState.IsBusy)
        {
            MessageBox.Show(this, "DriverWave is still working on something. Try again when it's finished.", "DriverWave");
            return;
        }
        if (UpdateError.Length > 0 && UpdatePath.Length == 0) StartUpdateDownload();
        new UpdateWindow(this).ShowDialog();
    }

    /// <summary>Starts the installer with its progress window and closes DriverWave. Returns an error or "".</summary>
    public string InstallUpdateNow()
    {
        try
        {
            Updater.Install(UpdatePath, relaunch: true, showProgress: true);
        }
        catch (UpdateException ex)
        {
            return ex.Message;
        }
        UpdatePath = ""; // don't start it a second time on close
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        t.Tick += (_, _) => { t.Stop(); Close(); };
        t.Start();
        return "";
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Don't let a driver install or uninstall get cut off by accident
        if (AppState.IsBusy && !AppState.ShuttingDown &&
            MessageBox.Show(this,
                "DriverWave is still working (for example installing a driver). Closing now can leave it half-finished.\n\nClose anyway?",
                "DriverWave", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        // Downloaded but not installed yet: install quietly now, without reopening
        if (UpdatePath.Length > 0 && Settings.Current.InstallUpdatesOnClose)
        {
            try { Updater.Install(UpdatePath, relaunch: false, showProgress: false); }
            catch (UpdateException ex) { Log.Write($"Update on close didn't start: {ex.Message}"); }
        }
    }
}
