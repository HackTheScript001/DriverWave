using System.IO;
using System.Windows;
using System.Windows.Controls;
using DriverWave.Services;
using Microsoft.Win32;

namespace DriverWave.Views;

public partial class BackupView : UserControl
{
    private bool _busy;

    public BackupView()
    {
        InitializeComponent();
    }

    private Window? OwnerWindow => Window.GetWindow(this);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var count = Directory.EnumerateFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF"), "oem*.inf").Count();
            DriverCountText.Text = $"This PC has {count} drivers added on top of Windows' built-in ones (graphics, Wi-Fi, chipset, printers…). They'll all be saved.";
        }
        catch
        {
            DriverCountText.Text = "Saves every driver added on top of Windows' built-in ones.";
        }
    }

    private async void OnBackup(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFolderDialog { Title = "Choose where to save the driver backup (a USB stick is ideal)" };
        if (dialog.ShowDialog(OwnerWindow!) != true) return;

        var target = Path.Combine(dialog.FolderName, $"DriverWave drivers - {Environment.MachineName} - {DateTime.Now:yyyy-MM-dd}");
        SetBusy(true, $"Saving drivers to {target}…");
        try
        {
            Directory.CreateDirectory(target);
            var r = await SystemTools.RunAsync(SystemTools.System32("pnputil.exe"), ["/export-driver", "*", target]);
            Log.Write(r.Output.Trim());

            var infs = Directory.EnumerateFiles(target, "*.inf", SearchOption.AllDirectories).Count();
            if (r.ExitCode == 0 && infs > 0)
            {
                StatusText.Text = $"Saved {infs} drivers ({SystemTools.FormatSize(SystemTools.FolderSize(target))}).";
                SystemTools.OpenInExplorer(target);
            }
            else
            {
                StatusText.Text = $"Backup didn't complete (pnputil code {r.ExitCode}). See Details.";
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Driver backup failed: {ex}");
            StatusText.Text = $"Backup failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFolderDialog { Title = "Choose the driver backup folder" };
        if (dialog.ShowDialog(OwnerWindow!) != true) return;

        var folder = dialog.FolderName;
        var infs = Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories).Count();
        if (infs == 0)
        {
            MessageBox.Show(OwnerWindow!, "That folder doesn't contain any drivers (.inf files).", "Restore drivers");
            return;
        }

        SetBusy(true, "Creating a restore point…");
        try
        {
            await Task.Run(() => RestorePoint.TryCreate("DriverWave – restore drivers"));
            StatusText.Text = $"Installing {infs} drivers – this can take a few minutes…";
            var r = await SystemTools.RunAsync(SystemTools.System32("pnputil.exe"),
                ["/add-driver", Path.Combine(folder, "*.inf"), "/subdirs", "/install"]);
            foreach (var line in r.Output.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
                Log.Write("  pnputil: " + line.TrimEnd());

            StatusText.Text = r.ExitCode switch
            {
                0 => "Drivers restored.",
                3010 => "Drivers restored – restart to finish.",
                259 => "None of the drivers in that backup match this PC's hardware.",
                _ => $"Finished with pnputil code {r.ExitCode} – some drivers may not have installed. See Details.",
            };
        }
        catch (Exception ex)
        {
            Log.Write($"Driver restore failed: {ex}");
            StatusText.Text = $"Restore failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        if (busy && !_busy) AppState.Enter();
        if (!busy && _busy) AppState.Exit();
        _busy = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        BackupButton.IsEnabled = RestoreButton.IsEnabled = !busy;
        if (status != null) StatusText.Text = status;
    }
}
