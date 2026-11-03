using System.IO;
using System.Text.Json;

namespace DriverWave.Services;

/// <summary>Small settings file at %ProgramData%\DriverWave\settings.json.</summary>
public sealed class Settings
{
    public bool InstallUpdatesOnClose { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;

    private static string FilePath => Path.Combine(AppInfo.DataFolder, "settings.json");
    private static Settings? _current;

    public static Settings Current => _current ??= Load();

    private static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Write($"Settings couldn't be read, using defaults: {ex.Message}");
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Write($"Settings couldn't be saved: {ex.Message}");
        }
    }
}
