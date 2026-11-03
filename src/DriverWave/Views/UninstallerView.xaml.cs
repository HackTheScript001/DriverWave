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
    public string TypeText => Item.Kind switch
    {
        LeftoverKind.Folder => "Folder",
        LeftoverKind.File => "File",
        _ => "Registry key",
    };
    public string SizeText => SystemTools.FormatSize(Item.SizeBytes);
}

public partial class UninstallerView : UserControl
{
    private readonly ObservableCollection<ProgramRow> _programs = [];
    private readonly ObservableCollection<LeftoverRow> _leftovers = [];
    private ICollectionView? _view;
    private InstalledProgram? _current;
    private bool _started;
    private bool _busy;

    public UninstallerView()
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(_programs);
        _view.Filter = o => o is ProgramRow r && Matches(r);
        ProgramGrid.ItemsSource = _view;
        LeftoverGrid.ItemsSource = _leftovers;
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
        SetBusy(true, "Reading installed programs…");
        try
        {
            var list = await Task.Run(() => Programs.List());
            _programs.Clear();
            foreach (var p in list) _programs.Add(new ProgramRow(p));
            UpdateCount();
            SetStatus("Select a program, then click Uninstall (or double-click it).");
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't list programs: {ex}");
            SetStatus($"Couldn't list programs: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
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
        _view?.Refresh();
        UpdateCount();
    }

    private void UpdateCount()
    {
        var shown = _view?.Cast<object>().Count() ?? 0;
        CountText.Text = shown == _programs.Count ? $"{_programs.Count} programs" : $"{shown} of {_programs.Count} programs";
    }

    // ───────────── Uninstall ─────────────

    private void OnUninstallDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OnUninstall(sender, e);

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (_busy || ProgramGrid.SelectedItem is not ProgramRow row) return;
        var p = row.Program;

        if (MessageBox.Show(OwnerWindow!, $"Uninstall {p.Name}?\n\nIts own uninstaller will open first. Afterwards DriverWave will look for anything it left behind.",
                "Uninstall", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        SetBusy(true, $"Creating a restore point before removing {p.Name}…");
        try
        {
            await Task.Run(() => RestorePoint.TryCreate($"DriverWave – uninstall {p.Name}"));
            SetStatus($"Running {p.Name}'s uninstaller – follow its window…");
            var code = await Programs.RunUninstallerAsync(p);

            if (code == -1)
                SetStatus("The uninstaller is missing. Scanning for leftovers instead…");
            else if (Programs.StillInstalled(p) && code != 0)
            {
                SetStatus($"The uninstaller was cancelled or failed (code {code}).");
                if (MessageBox.Show(OwnerWindow!, $"{p.Name} still looks installed. Scan for leftovers anyway?", "Uninstall",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
            }

            await ScanLeftoversAsync(p);
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

    private async void OnForce(object sender, RoutedEventArgs e)
    {
        if (_busy || ProgramGrid.SelectedItem is not ProgramRow row) return;
        if (MessageBox.Show(OwnerWindow!,
                $"Skip {row.Name}'s uninstaller and just find its files and registry keys?\n\nUse this when the normal uninstaller is broken or missing.",
                "Remove leftovers", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        SetBusy(true, "Looking for leftovers…");
        try { await ScanLeftoversAsync(row.Program); }
        finally { SetBusy(false); }
    }

    private async Task ScanLeftoversAsync(InstalledProgram p)
    {
        SetStatus($"Looking for anything {p.Name} left behind…");
        var items = await Task.Run(() => Programs.FindLeftovers(p));
        _current = p;
        _leftovers.Clear();
        foreach (var i in items) _leftovers.Add(new LeftoverRow { Item = i, Selected = true });

        if (_leftovers.Count == 0)
        {
            SetStatus($"{p.Name} was removed cleanly – nothing left behind.");
            await LoadProgramsAsync();
            return;
        }

        LeftoverTitle.Text = $"{p.Name} left {_leftovers.Count} item(s) behind";
        ShowLeftovers(true);
        SetStatus("Review what was found, then click Remove selected.");
    }

    private void ShowLeftovers(bool show)
    {
        LeftoverPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        var list = show ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.Visibility = ForceButton.Visibility = UninstallButton.Visibility = list;
        SkipButton.Visibility = RemoveButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnSkipLeftovers(object sender, RoutedEventArgs e)
    {
        ShowLeftovers(false);
        await LoadProgramsAsync();
    }

    private async void OnRemoveLeftovers(object sender, RoutedEventArgs e)
    {
        var chosen = _leftovers.Where(l => l.Selected).Select(l => l.Item).ToList();
        if (_busy || chosen.Count == 0 || _current == null) return;

        SetBusy(true, $"Removing {chosen.Count} item(s)…");
        try
        {
            var (removed, failed, backup) = await Programs.RemoveLeftoversAsync(chosen, $"uninstall {_current.Name}");
            SetStatus($"Removed {removed} item(s)" + (failed > 0 ? $", {failed} couldn't be removed (see Details)" : "") +
                      ". Registry backup saved.");
            Log.Write($"Registry backup: {backup}");
            ShowLeftovers(false);
            await LoadProgramsAsync();
        }
        catch (Exception ex)
        {
            Log.Write($"Leftover removal failed: {ex}");
            SetStatus($"Failed: {ex.Message}");
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
        RefreshButton.IsEnabled = ForceButton.IsEnabled = UninstallButton.IsEnabled = !busy;
        SkipButton.IsEnabled = RemoveButton.IsEnabled = !busy;
        if (status != null) SetStatus(status);
    }

    private void SetStatus(string text) => StatusText.Text = text;
}
