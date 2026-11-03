using System.Windows;
using System.Windows.Threading;
using DriverWave.Services;

namespace DriverWave;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Write($"Fatal: {args.ExceptionObject}");
        Log.Write($"DriverWave {AppInfo.Version} starting on {Environment.OSVersion}");
        base.OnStartup(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write($"Unhandled: {e.Exception}");
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails are in {Log.FilePath}",
            "DriverWave", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
