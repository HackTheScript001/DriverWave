using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DriverWave.Models;
using DriverWave.Services;

namespace DriverWave.Views;

public sealed class GpuItem : ObservableItem
{
    private string _latestText = "";
    private string _actionText = "";
    private bool _canAct;
    private double _progress;
    private Visibility _progressVisibility = Visibility.Collapsed;

    public required GpuInfo Gpu { get; init; }
    public NvidiaDriver? Latest { get; set; }

    public string Name => Gpu.OnBasicDriver ? $"{VendorText} graphics card (no driver yet)" : Gpu.Name;

    public string VendorText => Gpu.Vendor switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Amd => "AMD",
        GpuVendor.Intel => "INTEL",
        _ => "OTHER",
    };

    public string InstalledText
    {
        get
        {
            if (Gpu.OnBasicDriver) return "Installed driver: none (using Windows' basic display driver)";
            var v = Gpu.Vendor == GpuVendor.Nvidia ? GraphicsDrivers.NvidiaFriendlyVersion(Gpu.DriverVersion) : Gpu.DriverVersion;
            return $"Installed driver: {v}" + (Gpu.DriverDate is { } d ? $" ({d:d MMM yyyy})" : "");
        }
    }

    public string LatestText { get => _latestText; set => Set(ref _latestText, value); }
    public string ActionText { get => _actionText; set => Set(ref _actionText, value); }
    public bool CanAct { get => _canAct; set => Set(ref _canAct, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public Visibility ProgressVisibility { get => _progressVisibility; set => Set(ref _progressVisibility, value); }
}

public partial class GraphicsView : UserControl
{
    private readonly ObservableCollection<GpuItem> _gpus = [];
    private bool _started;
    private bool _busy;

    public GraphicsView()
    {
        InitializeComponent();
        GpuList.ItemsSource = _gpus;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;
        await RefreshAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        RefreshButton.IsEnabled = false;
        _gpus.Clear();
        EmptyText.Visibility = Visibility.Visible;
        EmptyText.Text = "Looking for graphics cards…";

        try
        {
            var gpus = await Task.Run(() => GraphicsDrivers.Detect());
            foreach (var g in gpus.Where(g => g.Vendor != GpuVendor.Other))
                _gpus.Add(new GpuItem { Gpu = g });

            EmptyText.Visibility = _gpus.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = "No NVIDIA, AMD or Intel graphics found. Use Missing drivers for anything else.";

            foreach (var item in _gpus) await PrepareAsync(item);
            StatusText.Text = $"Found {_gpus.Count} graphics card(s).";
        }
        catch (Exception ex)
        {
            Log.Write($"Graphics check failed: {ex}");
            StatusText.Text = $"Check failed: {ex.Message}";
        }
        finally
        {
            _busy = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private static async Task PrepareAsync(GpuItem item)
    {
        switch (item.Gpu.Vendor)
        {
            case GpuVendor.Nvidia:
                if (item.Gpu.OnBasicDriver)
                {
                    item.LatestText = "Install the driver from Missing drivers first so the exact model can be identified,";
                    item.Status = "then come back here for NVIDIA's latest driver.";
                    item.ActionText = "Install latest driver";
                    item.CanAct = false;
                    return;
                }

                item.LatestText = "Latest from NVIDIA: checking…";
                try
                {
                    item.Latest = await GraphicsDrivers.FindLatestNvidiaAsync(item.Gpu.Name);
                }
                catch (Exception ex)
                {
                    Log.Write($"NVIDIA check failed: {ex.Message}");
                }

                if (item.Latest == null)
                {
                    item.LatestText = "Latest from NVIDIA: couldn't look it up automatically";
                    item.ActionText = "Install latest driver";
                    item.CanAct = false;
                    item.Status = "Use \"Open manufacturer's page\" to download it from NVIDIA.";
                    return;
                }

                var installed = GraphicsDrivers.NvidiaFriendlyVersion(item.Gpu.DriverVersion);
                var newer = GraphicsDrivers.IsNewer(item.Latest.Version, installed);
                item.LatestText = $"Latest from NVIDIA: {item.Latest.Version}" +
                                  (item.Latest.Size > 0 ? $"  ·  {SystemTools.FormatSize(item.Latest.Size)}" : "");
                item.ActionText = newer ? $"Install {item.Latest.Version}" : "Reinstall driver";
                item.CanAct = true;
                item.Status = newer ? "A newer driver is available." : "You're on the latest driver.";
                break;

            case GpuVendor.Amd:
                item.LatestText = "AMD's auto-detect tool finds and installs the right Adrenalin driver.";
                item.ActionText = "Run AMD auto-detect";
                item.CanAct = true;
                break;

            case GpuVendor.Intel:
                item.LatestText = "Intel Driver & Support Assistant updates Intel graphics, Wi-Fi and chipset drivers.";
                item.ActionText = "Run Intel assistant";
                item.CanAct = true;
                break;
        }
    }

    private async void OnAction(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GpuItem item } || _busy) return;
        _busy = true;
        AppState.Enter();
        item.CanAct = false;
        item.Progress = 0;
        item.ProgressVisibility = Visibility.Visible;
        var progress = new Progress<double>(p => item.Progress = p);
        void Status(string s) => Dispatcher.Invoke(() => { item.Status = s; StatusText.Text = s; });

        try
        {
            switch (item.Gpu.Vendor)
            {
                case GpuVendor.Nvidia when item.Latest != null:
                    await Task.Run(() => RestorePoint.TryCreate($"DriverWave – NVIDIA {item.Latest.Version}"));
                    var outcome = await GraphicsDrivers.InstallNvidiaAsync(item.Latest, progress, Status);
                    Status(outcome.Success ? $"NVIDIA {item.Latest.Version} installed. Restart to finish." : $"Install failed: {outcome.Message}");
                    if (outcome.Success) AskRestart();
                    break;

                case GpuVendor.Amd:
                    if (!await GraphicsDrivers.RunAmdAutoDetectAsync(progress, Status))
                    {
                        Status("Couldn't download AMD's tool automatically – opening AMD's driver page instead.");
                        SystemTools.OpenUrl(GraphicsDrivers.AmdDriversPage);
                    }
                    break;

                case GpuVendor.Intel:
                    if (!await GraphicsDrivers.RunIntelAssistantAsync(progress, Status))
                    {
                        Status("Couldn't download Intel's tool automatically – opening Intel's page instead.");
                        SystemTools.OpenUrl(GraphicsDrivers.IntelDetectPage);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Graphics driver install failed: {ex}");
            Status($"Failed: {ex.Message}");
        }
        finally
        {
            item.ProgressVisibility = Visibility.Collapsed;
            item.CanAct = true;
            AppState.Exit();
            _busy = false;
        }
    }

    private void OnOpenPage(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GpuItem item }) return;
        SystemTools.OpenUrl(item.Gpu.Vendor switch
        {
            GpuVendor.Nvidia => GraphicsDrivers.NvidiaDriversPage,
            GpuVendor.Amd => GraphicsDrivers.AmdDriversPage,
            _ => GraphicsDrivers.IntelDetectPage,
        });
    }

    private void AskRestart()
    {
        var owner = Window.GetWindow(this);
        if (MessageBox.Show(owner!, "The new graphics driver is installed. Restart now to finish?", "Restart needed",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5") { UseShellExecute = false, CreateNoWindow = true });
        Application.Current.Shutdown();
    }
}
