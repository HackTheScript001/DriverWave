using System.Collections.ObjectModel;
using System.Windows;
using DriverWave.Services;

namespace DriverWave.Views;

/// <summary>
/// Shown after a program's uninstaller finishes, like Revo Uninstaller: the registry keys and files it
/// left behind, all ticked, with "Yes, delete selected" or "No, keep them".
/// </summary>
public partial class LeftoversWindow : Window
{
    private readonly ObservableCollection<LeftoverRow> _registry = [];
    private readonly ObservableCollection<LeftoverRow> _files = [];

    /// <summary>What the person chose to delete (empty if they kept everything).</summary>
    public List<Leftover> Chosen { get; private set; } = [];

    public LeftoversWindow(Window? owner, string programName, IEnumerable<Leftover> items)
    {
        InitializeComponent();
        Owner = owner;
        foreach (var i in items)
        {
            var row = new LeftoverRow { Item = i, Selected = true };
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LeftoverRow.Selected)) UpdateCounts(); };
            (i.Kind == LeftoverKind.RegistryKey ? _registry : _files).Add(row);
        }
        RegistryGrid.ItemsSource = _registry;
        FileGrid.ItemsSource = _files;

        HeadText.Text = $"{programName} left {_registry.Count + _files.Count} item(s) behind";
        RegistryHead.Text = $"Registry entries ({_registry.Count})";
        FilesHead.Text = $"Files and folders ({_files.Count})";
        if (_registry.Count == 0) AllRegistry.IsEnabled = false;
        if (_files.Count == 0) AllFiles.IsEnabled = false;
        UpdateCounts();
    }

    private void OnAllRegistry(object sender, RoutedEventArgs e)
    {
        foreach (var r in _registry) r.Selected = AllRegistry.IsChecked == true;
    }

    private void OnAllFiles(object sender, RoutedEventArgs e)
    {
        foreach (var r in _files) r.Selected = AllFiles.IsChecked == true;
    }

    private void UpdateCounts()
    {
        if (CountText == null) return;
        var n = _registry.Count(r => r.Selected) + _files.Count(r => r.Selected);
        var size = _files.Where(r => r.Selected).Sum(r => r.Item.SizeBytes);
        CountText.Text = $"{n} selected" + (size > 0 ? $"  ·  {SystemTools.FormatSize(size)}" : "");
        DeleteButton.IsEnabled = n > 0;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        Chosen = _registry.Concat(_files).Where(r => r.Selected).Select(r => r.Item).ToList();
        DialogResult = true;
    }

    private void OnKeep(object sender, RoutedEventArgs e)
    {
        Chosen = [];
        DialogResult = false;
    }
}
