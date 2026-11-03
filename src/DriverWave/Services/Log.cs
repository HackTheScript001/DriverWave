using System.IO;
namespace DriverWave.Services;

/// <summary>Writes to %ProgramData%\DriverWave\DriverWave.log and raises an event for the on-screen log.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppInfo.DataFolder, "DriverWave.log");

    public static event Action<string>? LineWritten;

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        lock (Gate)
        {
            try
            {
                // Keep the log from growing forever
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch { /* logging must never crash the app */ }
        }
        LineWritten?.Invoke(line);
    }
}
