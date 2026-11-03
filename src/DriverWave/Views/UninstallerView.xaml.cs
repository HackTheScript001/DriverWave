using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DriverWave.Models;
using DriverWave.Services;

namespace DriverWave.Views;

public sealed class ProgramRow(InstalledProgram p)
{
    public InstalledProgram Program { get; } = p;
    public string Name => Program.Name;
    public string Publisher => Program.Publisher;
    public string Version => Program.Version;
    public long SizeBytes => Program.SizeBytes;
    public DateTime? InstallDate => Program.InstallDate;
    public string SizeText => SystemTools.FormatSize(Program.SizeBytes);
    public string DateText => Program.InstallDate?.ToString("dd/MM/yyyy") ?? "";
}

public sealed class LeftoverRow : ObservableItem
{
    public required Leftover Item { get; init; }
    public string SizeText => SystemTools.FormatSize(Item.SizeBytes);
}

/// <summary>
/// Uninstall the way Revo Uninstaller does it: confirm (with an optional restore point), run the program's
/// own uninstaller, then pop up what it left behind in the registry and on disk and ask whether to delete it.
/// </summary>
public partial class UninstallerView : UserControl
{
    private readonly ObservableCollection<ProgramRow> _programs = [];
    private readonly ICollectionView _view;
    private bool _started;
    private bool _busy;

    public UninstallerView()
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(_programs);
        _view.Filter = o => o is ProgramRow r && Matches(r);
        ProgramGrid.ItemsSource = _view;
    }

    private Window? OwnerWindow => Window.GetWindow(this);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;
        await LoadProgramsAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await LoadProgramsAsync();

    private async Task LoadProgramsAsync()
    {
        var wasBusy = _busy;
        if (!wasBusy) SetBusy(true, "Reading installed programs…");
        try
        {
            var list = await Task.Run(() => Programs.List());
            _programs.Clear();
            foreach (var p in list) _programs.Add(new ProgramRow(p));
            UpdateCount();
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't list programs: {ex}");
            SetStatus($"Couldn't list programs: {ex.Message}");
        }
        finally
        {
            if (!wasBusy) SetBusy(false);
        }
    }

    private bool Matches(ProgramRow r)
    {
        var q = SearchBox?.Text?.Trim() ?? "";
        return q.Length == 0 ||
               r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               r.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void OnSearch(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
        UpdateCount();
    }

    private void UpdateCount()
    {
        var shown = _view.Cast<object>().Count();
        CountText.Text = shown == _programs.Count ? $"{_programs.Count} programs" : $"{shown} of {_programs.Count} programs";
    }

    // ───────────── Uninstall ─────────────

    private void OnUninstallDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OnUninstall(sender, e);

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (_busy || ProgramGrid.SelectedItem is not ProgramRow row) return;
        var p = row.Program;

        // 1. Confirm, with the choice of a backup first
        var confirm = new UninstallConfirmWindow(OwnerWindow, p);
        if (confirm.ShowDialog() != true) return;

        SetBusy(true);
        try
        {
            if (confirm.MakeBackup)
            {
                SetStatus($"Making a restore point before removing {p.Name}…");
                await Task.Run(() => RestorePoint.TryCreate($"DriverWave – uninstall {p.Name}"));
            }

            // 2. The program's own uninstaller
            SetStatus($"Running {p.Name}'s uninstaller – follow its window…");
            var code = await Programs.RunUninstallerAsync(p);

            if (code == -1)
            {
                Log.Write($"{p.Name}'s uninstaller is missing – looking for what's left instead");
            }
            else if (code != 0 && Programs.StillInstalled(p))
            {
                SetStatus($"{p.Name}'s uninstaller was cancelled or didn't finish (code {code}).");
                if (MessageBox.Show(OwnerWindow!, $"{p.Name} still looks installed – its uninstaller may have been cancelled.\n\nLook for leftovers anyway?",
                        "Uninstall", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
            }

            // 3. What it left behind
            SetStatus($"Looking for anything {p.Name} left behind…");
            var leftovers = await Task.Run(() => Programs.FindLeftovers(p));

            if (leftovers.Count == 0)
            {
                SetStatus($"{p.Name} was removed cleanly – nothing left behind.");
                MessageBox.Show(OwnerWindow!, $"{p.Name} has been removed. Nothing was left behind.", "Uninstall",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                // 4. Ask: delete these? yes / no
                var window = new LeftoversWindow(OwnerWindow, p.Name, leftovers);
                if (window.ShowDialog() == true && window.Chosen.Count > 0)
                {
                    SetStatus($"Deleting {window.Chosen.Count} leftover item(s)…");
                    var (removed, failed, backup) = await Programs.RemoveLeftoversAsync(window.Chosen, $"uninstall {p.Name}");
                    Log.Write($"Registry backup: {backup}");
                    SetStatus($"{p.Name} removed, plus {removed} leftover item(s)" +
                              (failed > 0 ? $". {failed} couldn't be deleted (see Details)." : "."));
                }
                else
                {
                    SetStatus($"{p.Name} removed. Leftovers were kept.");
                }
            }

            await LoadProgramsAsync();
        }
        catch (Exception ex)
        {
            Log.Write($"Uninstall failed: {ex}");
            SetStatus($"Uninstall failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ───────────── Helpers ─────────────

    private void SetBusy(bool busy, string? status = null)
    {
        if (busy && !_busy) AppState.Enter();
        if (!busy && _busy) AppState.Exit();
        _busy = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        RefreshButton.IsEnabled = UninstallButton.IsEnabled = !busy;
        if (status != null) SetStatus(status);
    }

    private void SetStatus(string text) => StatusText.Text = text;
}
