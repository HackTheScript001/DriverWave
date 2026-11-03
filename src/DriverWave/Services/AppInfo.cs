using System.IO;
using System.Reflection;

namespace DriverWave.Services;

public static class AppInfo
{
    public const string GitHubOwner = "HackTheScript001";
    public const string GitHubRepo = "DriverWave";

    public static Version Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public static string VersionText => $"{Version.Major}.{Version.Minor}.{Version.Build}";

    /// <summary>Folder the exe lives in (works for single-file publish).</summary>
    public static string AppFolder => AppContext.BaseDirectory;

    public static string DataFolder
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DriverWave");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
