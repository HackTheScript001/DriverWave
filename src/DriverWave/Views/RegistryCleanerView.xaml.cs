using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DriverWave.Models;
using DriverWave.Services;
using Microsoft.Win32;

namespace DriverWave.Views;

public sealed class IssueRow : ObservableItem
{
    public required RegistryIssue Issue { get; init; }
}

public partial class RegistryCleanerView : UserControl
{
    private readonly ObservableCollection<IssueRow> _rows = [];
    private bool _busy;

    public RegistryCleanerView()
    {
        InitializeComponent();
        IssueGrid.ItemsSource = _rows;
    }

    private Window? OwnerWindow => Window.GetWindow(this);

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true, "Scanning the registry…");
        _rows.Clear();
        try
        {
            var issues = await Task.Run(() => RegistryCleaner.Scan(s => Dispatcher.Invoke(() => StatusText.Text = s)));
            foreach (var i in issues) _rows.Add(new IssueRow { Issue = i, Selected = true });

            EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = "No problems found.";
            StatusText.Text = _rows.Count == 0 ? "No problems found." : $"Found {_rows.Count} problem(s). Untick anything you want to keep, then click Fix selected.";
        }
        catch (Exception ex)
        {
            Log.Write($"Registry scan failed: {ex}");
            StatusText.Text = $"Scan failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnFix(object sender, RoutedEventArgs e)
    {
        var chosen = _rows.Where(r => r.Selected && r.Status != "Fixed").ToList();
        if (_busy || chosen.Count == 0) return;

        if (MessageBox.Show(OwnerWindow!, $"Fix {chosen.Count} registry problem(s)?\n\nA restore point and a registry backup are made first.",
                "Registry cleaner", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        SetBusy(true, "Creating a restore point…");
        try
        {
            await Task.Run(() => RestorePoint.TryCreate("DriverWave – registry cleaner"));
            StatusText.Text = "Backing up and fixing…";
            var (fixedCount, failed, backup) = await RegistryCleaner.FixAsync(chosen.Select(r => r.Issue));

            foreach (var r in chosen) r.Status = "Fixed";
            StatusText.Text = $"Fixed {fixedCount}" + (failed > 0 ? $", {failed} couldn't be fixed (see Details)" : "") + ". Backup saved.";
            Log.Write($"Registry backup: {backup}");
        }
        catch (Exception ex)
        {
            Log.Write($"Registry fix failed: {ex}");
            StatusText.Text = $"Fix failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a DriverWave backup to restore",
            InitialDirectory = SystemTools.BackupsRoot,
        };
        if (dialog.ShowDialog(OwnerWindow!) != true) return;

        var files = Directory.EnumerateFiles(dialog.FolderName, "*.reg").ToList();
        if (files.Count == 0)
        {
            MessageBox.Show(OwnerWindow!, "That folder doesn't contain any registry backups (.reg files).", "Restore");
            return;
        }

        if (MessageBox.Show(OwnerWindow!, $"Put back {files.Count} registry backup file(s) from\n{dialog.FolderName}?", "Restore",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        SetBusy(true, "Restoring…");
        try
        {
            var ok = 0;
            foreach (var f in files)
                if (await SystemTools.ImportRegAsync(f)) ok++;
            StatusText.Text = $"Restored {ok} of {files.Count} backup file(s).";
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
        ScanButton.IsEnabled = RestoreButton.IsEnabled = !busy;
        FixButton.IsEnabled = !busy && _rows.Count > 0;
        if (status != null) StatusText.Text = status;
    }
}
