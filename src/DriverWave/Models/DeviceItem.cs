using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DriverWave.Models;

public enum DeviceState
{
    MissingDriver,   // device has a problem code (e.g. code 28: no driver installed)
    GenericDriver,   // working, but on a basic Microsoft fallback driver
    UpdateAvailable, // working, Windows Update has a newer driver
}

public enum ItemStatus
{
    DriverFound,
    NoDriverFound,
    Downloading,
    Installing,
    Installed,
    InstalledRebootNeeded,
    Failed,
}

/// <summary>One row in the results grid: a device and (if found) the driver for it.</summary>
public sealed class DeviceItem : INotifyPropertyChanged
{
    private bool _selected;
    private ItemStatus _status;
    private string _statusDetail = "";

    public required string DeviceName { get; init; }
    public string Category { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string[] HardwareIds { get; init; } = [];
    public DeviceState State { get; init; }
    public int ProblemCode { get; init; }

    /// <summary>The Windows Update driver update (WUA COM object) or null if none found.</summary>
    public dynamic? Update { get; set; }
    public string DriverTitle { get; set; } = "";
    public long DownloadBytes { get; set; }

    public bool HasDriver => (object?)Update != null;

    public bool CanInstall => HasDriver && Status is ItemStatus.DriverFound or ItemStatus.Failed;

    public bool Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; OnChanged(); } }
    }

    public ItemStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnChanged();
            OnChanged(nameof(StatusText));
            OnChanged(nameof(CanInstall));
        }
    }

    public string StatusDetail
    {
        get => _statusDetail;
        set { _statusDetail = value; OnChanged(); OnChanged(nameof(StatusText)); }
    }

    public string StateText => State switch
    {
        DeviceState.MissingDriver => ProblemCode == 28 ? "Missing driver" : $"Problem (code {ProblemCode})",
        DeviceState.GenericDriver => "Basic driver only",
        _ => "Update available",
    };

    public string StatusText => Status switch
    {
        ItemStatus.DriverFound => "Ready to install",
        ItemStatus.NoDriverFound => "Not found on Windows Update",
        ItemStatus.Downloading => "Downloading…",
        ItemStatus.Installing => "Installing…",
        ItemStatus.Installed => "Installed ✓",
        ItemStatus.InstalledRebootNeeded => "Installed – restart needed",
        ItemStatus.Failed => string.IsNullOrEmpty(StatusDetail) ? "Failed" : $"Failed: {StatusDetail}",
        _ => "",
    };

    public string SizeText => DownloadBytes <= 0 ? "" :
        DownloadBytes >= 1024 * 1024 ? $"{DownloadBytes / 1024d / 1024d:0.0} MB" : $"{DownloadBytes / 1024d:0} KB";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
