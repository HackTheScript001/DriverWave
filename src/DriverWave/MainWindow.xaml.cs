using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using DriverWave.Models;
using DriverWave.Services;

namespace DriverWave;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DeviceItem> _items = [];
    // Which Windows Update entry each row uses, so identical devices share one install
    private readonly Dictionary<DeviceItem, DriverUpdate> _driverFor = [];
    private AppUpdate? _appUpdate;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _items;
        VersionText.Text = $"v{AppInfo.VersionText}  ·  © {DateTime.Now.Year} SecondWave IT Systems";
        Log.LineWritten += line => Dispatcher.InvokeAsync(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = CheckForAppUpdateAsync();
        await ScanAsync();
    }

    // ───────────────────────────── Scanning ─────────────────────────────

    private async void OnScan(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_busy) return;
        SetBusy(true);
        _items.Clear();
        _driverFor.Clear();
        OfflineBanner.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Visible;
        EmptyText.Text = "Reading your hardware…";
        DevicesCount.Text = MissingCount.Text = FoundCount.Text = UpdatesCount.Text = "–";

        try
        {
            SetStatus("Reading your hardware…", indeterminate: true);
            var devices = await Task.Run(() => HardwareScanner.GetAllDevices());
            var problem = devices.Where(d => HardwareScanner.NeedsDriver(d) || HardwareScanner.IsOnGenericDriver(d)).ToList();
            DevicesCount.Text = devices.Count.ToString();
            MissingCount.Text = problem.Count.ToString();

            SetStatus("Checking internet connection…", indeterminate: true);
            if (!await Connectivity.HasInternetAsync())
            {
                ShowOffline(devices, problem);
                return;
            }

            SetStatus("Asking Windows Update for drivers – this can take a few minutes on a fresh install…", indeterminate: true);
            EmptyText.Text = "Checking Windows Update for drivers that match your hardware.\nOn a fresh install this can take a few minutes.";
            var updates = await Task.Run(() => WindowsUpdateDrivers.Search());

            BuildResults(devices, problem, updates);
        }
        catch (Exception ex)
        {
            Log.Write($"Scan failed: {ex}");
            SetStatus($"Scan failed: {ex.Message}");
            EmptyText.Text = $"The scan didn't finish:\n{ex.Message}\n\nOpen Details below for more.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void BuildResults(List<PnpDevice> devices, List<PnpDevice> problem, List<DriverUpdate> updates)
    {
        var used = new HashSet<DriverUpdate>();

        // 1. Devices with a missing or basic driver – match them to a Windows Update driver
        foreach (var d in problem)
        {
            var match = updates.FirstOrDefault(u => Matches(u, d));
            var item = new DeviceItem
            {
                DeviceName = d.Name,
                Category = Friendly(d.ClassName),
                DeviceId = d.DeviceId,
                HardwareIds = d.HardwareIds,
                ProblemCode = d.ProblemCode,
                State = d.ProblemCode == 0 ? DeviceState.GenericDriver : DeviceState.MissingDriver,
            };

            if (match != null)
            {
                Attach(item, match);
                item.Selected = true;
                used.Add(match);
            }
            else
            {
                item.Status = ItemStatus.NoDriverFound;
            }
            _items.Add(item);
        }

        // 2. Newer drivers for devices that already work
        foreach (var u in updates.Where(u => !used.Contains(u)))
        {
            var device = devices.FirstOrDefault(d => Matches(u, d));
            var item = new DeviceItem
            {
                DeviceName = device?.Name ?? (string.IsNullOrEmpty(u.Model) ? u.Title : u.Model),
                Category = device != null ? Friendly(device.ClassName) : u.DriverClass,
                DeviceId = device?.DeviceId ?? u.HardwareId,
                State = DeviceState.UpdateAvailable,
            };
            Attach(item, u);
            item.Selected = IncludeUpdates.IsChecked == true;
            _items.Add(item);
        }

        var found = _items.Count(i => i.State != DeviceState.UpdateAvailable && i.HasDriver);
        var notFound = _items.Count(i => i.Status == ItemStatus.NoDriverFound);
        var upd = _items.Count(i => i.State == DeviceState.UpdateAvailable);
        FoundCount.Text = found.ToString();
        UpdatesCount.Text = upd.ToString();

        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = "Every device has a driver and Windows Update has nothing newer. You're all set.";

        SetStatus(problem.Count == 0 && upd == 0
            ? "All good – nothing to install."
            : $"{found} driver(s) ready to install" +
              (notFound > 0 ? $", {notFound} device(s) not covered by Windows Update" : "") +
              (upd > 0 ? $", {upd} optional update(s)" : "") + ".");
        Log.Write($"Results: {problem.Count} need drivers, {found} matched, {notFound} unmatched, {upd} optional updates");
    }

    private void Attach(DeviceItem item, DriverUpdate u)
    {
        item.Update = u.Update;
        item.DriverTitle = u.Title;
        item.DownloadBytes = u.DownloadBytes;
        item.Status = ItemStatus.DriverFound;
        _driverFor[item] = u;
    }

    private static bool Matches(DriverUpdate u, PnpDevice d) =>
        !string.IsNullOrEmpty(u.HardwareId) &&
        d.AllIds.Any(id => string.Equals(id, u.HardwareId, StringComparison.OrdinalIgnoreCase));

    private static string Friendly(string pnpClass) => pnpClass switch
    {
        "Net" => "Network",
        "Display" => "Graphics",
        "MEDIA" or "AudioEndpoint" => "Audio",
        "HIDClass" => "Input device",
        "USB" => "USB",
        "System" => "System / chipset",
        "Bluetooth" => "Bluetooth",
        "Camera" or "Image" => "Camera",
        "SCSIAdapter" or "HDC" => "Storage controller",
        "Biometric" => "Fingerprint / biometric",
        "SmartCardReader" => "Card reader",
        "Unknown" or "" => "Unknown device",
        _ => pnpClass,
    };

    // ──────────────────────── No internet / offline pack ────────────────────────

    private void ShowOffline(List<PnpDevice> devices, List<PnpDevice> problem)
    {
        foreach (var d in problem)
        {
            _items.Add(new DeviceItem
            {
                DeviceName = d.Name,
                Category = Friendly(d.ClassName),
                DeviceId = d.DeviceId,
                ProblemCode = d.ProblemCode,
                State = d.ProblemCode == 0 ? DeviceState.GenericDriver : DeviceState.MissingDriver,
                Status = ItemStatus.NoDriverFound,
                StatusDetail = "Needs internet",
            });
        }
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = "No internet connection, and no devices are missing drivers.";

        var nicMissing = HardwareScanner.HasMissingNetworkDriver(devices);
        if (OfflineDriverPack.IsAvailable)
        {
            OfflineBannerText.Text = nicMissing
                ? "This PC isn't online because its network adapter has no driver. DriverWave has common Ethernet and Wi-Fi drivers built in – install them first, then it'll search for the rest."
                : "This PC isn't connected to the internet. If you can't get online, try DriverWave's built-in network drivers.";
            OfflineBanner.Visibility = Visibility.Visible;
        }

        SetStatus(nicMissing
            ? "No internet – the network adapter is missing its driver."
            : "No internet – connect to a network, then scan again.");
    }

    private async void OnInstallOffline(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        OfflineButton.IsEnabled = false;
        try
        {
            SetStatus("Installing built-in network drivers…", indeterminate: true);
            await Task.Run(() => RestorePoint.TryCreate("DriverWave – before network drivers"));
            var outcome = await OfflineDriverPack.InstallAsync();
            Log.Write(outcome.Message);

            if (!outcome.Success)
            {
                SetStatus(outcome.Message);
                MessageBox.Show(this, outcome.Message + "\n\nYou may need to download the network driver on another PC (from the manufacturer's website) and copy it over on a USB stick.",
                    "DriverWave", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SetStatus("Waiting for the network to come up…", indeterminate: true);
            for (var i = 0; i < 15 && !await Connectivity.HasInternetAsync(); i++)
                await Task.Delay(2000);

            OfflineBanner.Visibility = Visibility.Collapsed;
            if (outcome.RebootRequired && !await Connectivity.HasInternetAsync())
            {
                AskRestart("The network driver needs a restart before it works. After restarting, open DriverWave again to finish.");
                return;
            }
        }
        finally
        {
            OfflineButton.IsEnabled = true;
            SetBusy(false);
        }
        await ScanAsync();
    }

    // ───────────────────────────── Installing ─────────────────────────────

    private void OnIncludeUpdatesChanged(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items.Where(i => i.State == DeviceState.UpdateAvailable && i.CanInstall))
            i.Selected = IncludeUpdates.IsChecked == true;
        RefreshInstallButton();
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        var chosen = _items.Where(i => i.Selected && i.CanInstall && _driverFor.ContainsKey(i)).ToList();
        if (chosen.Count == 0 || _busy) return;

        SetBusy(true);
        var reboot = false;
        int ok = 0, failed = 0;

        try
        {
            SetStatus("Creating a restore point so this can be undone…", indeterminate: true);
            await Task.Run(() => RestorePoint.TryCreate($"DriverWave – {chosen.Count} driver(s)"));

            // Identical devices (e.g. two of the same USB controller) share one driver – install it once
            var groups = chosen.GroupBy(i => _driverFor[i]).ToList();
            BusyBar.IsIndeterminate = false;
            BusyBar.Maximum = groups.Count;
            BusyBar.Value = 0;

            for (var n = 0; n < groups.Count; n++)
            {
                var group = groups[n];
                var rows = group.ToList();
                SetStatus($"({n + 1}/{groups.Count}) {group.Key.Title}");
                foreach (var r in rows) r.Status = ItemStatus.Downloading;

                InstallOutcome outcome;
                try
                {
                    outcome = await Task.Run(() => WindowsUpdateDrivers.Install((object)group.Key.Update, phase =>
                        Dispatcher.Invoke(() =>
                        {
                            foreach (var r in rows)
                                r.Status = phase == "install" ? ItemStatus.Installing : ItemStatus.Downloading;
                        })));
                }
                catch (Exception ex)
                {
                    Log.Write($"{group.Key.Title}: {ex.Message}");
                    outcome = new InstallOutcome(false, false, ex.Message);
                }

                foreach (var r in rows)
                {
                    r.StatusDetail = outcome.Success ? "" : outcome.Message;
                    r.Status = !outcome.Success ? ItemStatus.Failed
                        : outcome.RebootRequired ? ItemStatus.InstalledRebootNeeded
                        : ItemStatus.Installed;
                    r.Selected = false;
                }

                if (outcome.Success) ok++; else failed++;
                reboot |= outcome.RebootRequired;
                BusyBar.Value = n + 1;
            }

            SetStatus($"Done – {ok} installed" + (failed > 0 ? $", {failed} failed" : "") +
                      (reboot ? ". Restart to finish." : ". Scan again to catch any devices that have just appeared."));
        }
        finally
        {
            SetBusy(false);
        }

        if (reboot)
            AskRestart("Some drivers need a restart to finish installing. Restart now?\n\nAfter restarting, run DriverWave again – newly detected devices often show up once the chipset driver is in.");
    }

    private void AskRestart(string message)
    {
        if (MessageBox.Show(this, message, "Restart needed", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        Log.Write("Restarting PC");
        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5 /c \"DriverWave is restarting to finish installing drivers\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Application.Current.Shutdown();
    }

    // ───────────────────────────── App updates ─────────────────────────────

    private async Task CheckForAppUpdateAsync()
    {
        _appUpdate = await Updater.CheckAsync();
        if (_appUpdate == null) return;
        UpdateBannerText.Text = $"DriverWave {_appUpdate.Version.ToString(3)} is available (you have {AppInfo.VersionText}).";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private async void OnUpdateApp(object sender, RoutedEventArgs e)
    {
        if (_appUpdate == null) return;
        if (_busy)
        {
            MessageBox.Show(this, "Wait for the current scan or install to finish first.", "DriverWave");
            return;
        }

        UpdateAppButton.IsEnabled = false;
        SetBusy(true);
        try
        {
            var progress = new Progress<double>(p =>
            {
                BusyBar.IsIndeterminate = false;
                BusyBar.Maximum = 1;
                BusyBar.Value = p;
                SetStatus($"Downloading update… {p:P0}");
            });
            await Updater.DownloadAndInstallAsync(_appUpdate, progress);
        }
        catch (Exception ex)
        {
            Log.Write($"Update failed: {ex}");
            SetStatus($"Update failed: {ex.Message}");
            UpdateAppButton.IsEnabled = true;
            SetBusy(false);
        }
    }

    // ───────────────────────────── UI helpers ─────────────────────────────

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ScanButton.IsEnabled = !busy;
        IncludeUpdates.IsEnabled = !busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        if (!busy) BusyBar.IsIndeterminate = false;
        RefreshInstallButton();
    }

    private void RefreshInstallButton()
    {
        var n = _items.Count(i => i.Selected && i.CanInstall);
        InstallButton.IsEnabled = !_busy && n > 0;
        InstallButton.Content = n > 0 ? $"Install selected ({n})" : "Install selected";
    }

    private void SetStatus(string text, bool indeterminate = false)
    {
        StatusText.Text = text;
        if (indeterminate) BusyBar.IsIndeterminate = true;
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        // Keep the Install button count in sync when rows are ticked/unticked
        _items.CollectionChanged += (_, args) =>
        {
            if (args.NewItems != null)
                foreach (DeviceItem i in args.NewItems)
                    i.PropertyChanged += (_, p) =>
                    {
                        if (p.PropertyName is nameof(DeviceItem.Selected) or nameof(DeviceItem.Status))
                            RefreshInstallButton();
                    };
            RefreshInstallButton();
        };
    }
}
