using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DriverWave.Models;
using DriverWave.Services;

namespace DriverWave.Views;

public sealed class CleanupRow : ObservableItem
{
    private long _size = -1;

    public required CleanupTarget Target { get; init; }

    public long Size
    {
        get => _size;
        set { Set(ref _size, value); Raise(nameof(SizeText)); }
    }

    public string SizeText => _size < 0 ? "" : _size == 0 ? "Nothing" : SystemTools.FormatSize(_size);
}

public partial class CleanupView : UserControl
{
    private readonly ObservableCollection<CleanupRow> _rows = [];
    private bool _started;
    private bool _busy;

    public CleanupView()
    {
        InitializeComponent();
        foreach (var t in Cleanup.Targets())
            _rows.Add(new CleanupRow { Target = t, Selected = t.OnByDefault });
        TargetGrid.ItemsSource = _rows;
        foreach (var r in _rows)
            r.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CleanupRow.Selected)) UpdateTotal(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;
        await AnalyseAsync();
    }

    private async void OnAnalyse(object sender, RoutedEventArgs e) => await AnalyseAsync();

    private async Task AnalyseAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            foreach (var r in _rows)
            {
                StatusText.Text = $"Checking {r.Target.Name.ToLowerInvariant()}…";
                r.Status = "Checking…";
                r.Size = await Task.Run(() => Cleanup.Measure(r.Target));
                r.Status = "";
            }
            UpdateTotal();
            StatusText.Text = "Tick what you want removed, then click Clean selected.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnClean(object sender, RoutedEventArgs e)
    {
        var chosen = _rows.Where(r => r.Selected).ToList();
        if (_busy || chosen.Count == 0) return;

        if (chosen.Any(r => r.Target.IsRecycleBin) &&
            MessageBox.Show(Window.GetWindow(this)!, "Empty the Recycle Bin too? Files in it can't be recovered afterwards.",
                "System cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        SetBusy(true);
        long freed = 0;
        try
        {
            foreach (var r in chosen)
            {
                StatusText.Text = $"Cleaning {r.Target.Name.ToLowerInvariant()}…";
                r.Status = "Cleaning…";
                var f = await Task.Run(() => Cleanup.Clean(r.Target));
                freed += f;
                r.Status = f > 0 ? $"Freed {SystemTools.FormatSize(f)}" : "Done";
                r.Size = await Task.Run(() => Cleanup.Measure(r.Target));
            }
            StatusText.Text = $"Done – freed {SystemTools.FormatSize(freed)}. Anything still listed was in use.";
            UpdateTotal();
        }
        catch (Exception ex)
        {
            Log.Write($"Cleanup failed: {ex}");
            StatusText.Text = $"Cleanup failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void UpdateTotal()
    {
        TotalText.Text = SystemTools.FormatSize(_rows.Where(r => r.Selected && r.Size > 0).Sum(r => r.Size)) is { Length: > 0 } s ? s : "0 KB";
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            FreeText.Text = SystemTools.FormatSize(drive.AvailableFreeSpace);
        }
        catch { }
    }

    private void SetBusy(bool busy)
    {
        if (busy && !_busy) AppState.Enter();
        if (!busy && _busy) AppState.Exit();
        _busy = busy;
        BusyBar.IsIndeterminate = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        AnalyseButton.IsEnabled = CleanButton.IsEnabled = !busy;
    }
}
