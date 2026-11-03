using System.IO;
using Microsoft.Win32;

namespace DriverWave.Services;

public enum StartupSource { RegistryRun, StartupFolder }

public sealed record StartupEntry(
    string Name,
    string Command,
    string Location,          // shown to the user
    StartupSource Source,
    RegistryHive Hive,        // for registry entries; for folders: CurrentUser = your folder, LocalMachine = all users
    RegistryView View,
    string ApprovedKey,       // StartupApproved key Windows uses to track enabled/disabled
    string ApprovedValue,
    bool Enabled)
{
    public string? TargetPath => SystemTools.ExtractPath(Command);
}

/// <summary>
/// Startup programs, enabled/disabled the same way Task Manager does it (StartupApproved),
/// so changes show up in Task Manager too and nothing is deleted.
/// </summary>
public static class Startup
{
    private const string RunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static List<StartupEntry> List()
    {
        var list = new List<StartupEntry>();

        AddRun(list, RegistryHive.CurrentUser, RegistryView.Default, "Run", "Your account");
        AddRun(list, RegistryHive.LocalMachine, RegistryView.Registry64, "Run", "All users");
        AddRun(list, RegistryHive.LocalMachine, RegistryView.Registry32, "Run32", "All users (32-bit)");

        AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser, "Your Startup folder");
        AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine, "All users' Startup folder");

        return list.OrderBy(e => !e.Enabled).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddRun(List<StartupEntry> list, RegistryHive hive, RegistryView view, string approvedName, string where)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var run = root.OpenSubKey(RunPath);
            if (run == null) return;

            foreach (var name in run.GetValueNames())
            {
                if (string.IsNullOrEmpty(name)) continue;
                var cmd = run.GetValue(name)?.ToString() ?? "";
                var approvedKey = $@"{ApprovedRoot}\{approvedName}";
                list.Add(new StartupEntry(name, cmd, where, StartupSource.RegistryRun, hive, view,
                    approvedKey, name, IsEnabled(hive, approvedKey, name)));
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't read startup entries ({where}): {ex.Message}");
        }
    }

    private static void AddFolder(List<StartupEntry> list, string folder, RegistryHive hive, string where)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var approvedKey = $@"{ApprovedRoot}\StartupFolder";
            list.Add(new StartupEntry(Path.GetFileNameWithoutExtension(file), file, where, StartupSource.StartupFolder,
                hive, RegistryView.Registry64, approvedKey, fileName, IsEnabled(hive, approvedKey, fileName)));
        }
    }

    /// <summary>StartupApproved values: first byte even (02/06) = enabled, odd (03/07) = disabled. Missing = enabled.</summary>
    private static bool IsEnabled(RegistryHive hive, string approvedKey, string valueName)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var k = root.OpenSubKey(approvedKey);
            return k?.GetValue(valueName) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
        }
        catch { return true; }
    }

    public static void SetEnabled(StartupEntry e, bool enabled)
    {
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled)
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4); // when it was disabled, as Task Manager records it

        using var root = RegistryKey.OpenBaseKey(e.Hive, RegistryView.Registry64);
        using var k = root.CreateSubKey(e.ApprovedKey, writable: true);
        k.SetValue(e.ApprovedValue, data, RegistryValueKind.Binary);
        Log.Write($"{(enabled ? "Enabled" : "Disabled")} startup item {e.Name}");
    }
}
