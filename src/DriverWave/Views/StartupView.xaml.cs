using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DriverWave.Models;
using DriverWave.Services;

namespace DriverWave.Views;

public sealed class StartupRow : ObservableItem
{
    private bool _enabled;
    private bool _loading = true;

    public required StartupEntry Entry { get; init; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            if (!_loading)
            {
                try
                {
                    Startup.SetEnabled(Entry, value);
                    Status = value ? "Turned on" : "Turned off";
                }
                catch (Exception ex)
                {
                    Log.Write($"Couldn't change {Entry.Name}: {ex.Message}");
                    Status = "Couldn't change";
                    Raise();
                    return;
                }
            }
            _enabled = value;
            Raise();
        }
    }

    public void FinishLoading() => _loading = false;
}

public partial class StartupView : UserControl
{
    private readonly ObservableCollection<StartupRow> _rows = [];
    private bool _started;

    public StartupView()
    {
        InitializeComponent();
        EntryGrid.ItemsSource = _rows;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;
        Load();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Load();

    private void Load()
    {
        _rows.Clear();
        foreach (var entry in Startup.List())
        {
            var row = new StartupRow { Entry = entry, Enabled = entry.Enabled };
            row.Status = entry.Enabled ? "" : "Off";
            row.FinishLoading();
            _rows.Add(row);
        }
        var on = _rows.Count(r => r.Enabled);
        StatusText.Text = $"{on} of {_rows.Count} programs start with Windows. Untick one to stop it.";
    }

    private void OnOpenLocation(object sender, RoutedEventArgs e)
    {
        if (EntryGrid.SelectedItem is not StartupRow row) return;
        var path = row.Entry.TargetPath;
        if (path != null) SystemTools.OpenInExplorer(path);
    }
}
