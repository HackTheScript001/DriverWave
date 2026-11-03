using System.Windows;
using System.Windows.Threading;
using DriverWave.Services;

namespace DriverWave;

/// <summary>"Updating DriverWave": finishes the download, then installs and reopens.</summary>
public partial class UpdateWindow : Window
{
    private readonly MainWindow _main;

    public UpdateWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        Owner = main;
        var rel = main.UpdateRelease!;
        HeadText.Text = $"Updating DriverWave to {rel.VersionText}";

        var notes = Updater.WhatsNew(rel.Notes, rel.Version);
        NotesText.Text = notes;
        NotesPanel.Visibility = notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        main.UpdateProgress += OnProgress;
        main.UpdateStateChanged += Step;
        Closed += (_, _) =>
        {
            main.UpdateProgress -= OnProgress;
            main.UpdateStateChanged -= Step;
        };

        OnProgress(main.UpdateDone.Done, main.UpdateDone.Total);
        Step();
    }

    private void OnProgress(long done, long total)
    {
        if (_main.UpdatePath.Length > 0) return;
        if (total > 0)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = 100.0 * done / total;
            StatusText.Text = $"Downloading… {done / 1048576.0:0.0} of {total / 1048576.0:0.0} MB";
        }
        else
        {
            Bar.IsIndeterminate = true;
            StatusText.Text = "Downloading…";
        }
    }

    private void Step()
    {
        if (_main.UpdatePath.Length > 0)
        {
            Bar.IsIndeterminate = true;
            StatusText.Text = "Installing… DriverWave will close and reopen on the new version in a moment.";
            LaterButton.IsEnabled = false;
            RetryButton.Visibility = Visibility.Collapsed;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            t.Tick += (_, _) => { t.Stop(); Install(); };
            t.Start();
        }
        else if (_main.UpdateError.Length > 0)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = 0;
            StatusText.Text = $"The download didn't finish: {_main.UpdateError}";
            RetryButton.Visibility = Visibility.Visible;
        }
    }

    private void Install()
    {
        var err = _main.InstallUpdateNow();
        if (err.Length > 0)
        {
            Bar.IsIndeterminate = false;
            StatusText.Text = $"The update couldn't start: {err}";
            LaterButton.IsEnabled = true;
            LaterButton.Content = "Close";
            return;
        }
        Close();
    }

    private void OnRetry(object sender, RoutedEventArgs e)
    {
        RetryButton.Visibility = Visibility.Collapsed;
        StatusText.Text = "Downloading…";
        _main.StartUpdateDownload();
    }

    private void OnLater(object sender, RoutedEventArgs e) => Close();
}
