using System.Windows;
using DriverWave.Services;

namespace DriverWave.Views;

/// <summary>"Uninstall X?" with the choice of making a restore point first.</summary>
public partial class UninstallConfirmWindow : Window
{
    public bool MakeBackup => BackupBox.IsChecked == true;

    public UninstallConfirmWindow(Window? owner, InstalledProgram p)
    {
        InitializeComponent();
        Owner = owner;
        HeadText.Text = $"Uninstall {p.Name}?";
        SubText.Text = string.Join("  ·  ", new[] { p.Publisher, p.Version }.Where(s => s.Length > 0));
        SubText.Visibility = SubText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnUninstall(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
