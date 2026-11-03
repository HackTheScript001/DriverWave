using System.IO;
using Microsoft.Win32;

namespace DriverWave.Services;

public sealed record RegistryIssue(
    string Category,
    RegistryHive Hive,
    RegistryView View,
    string KeyPath,
    string? ValueName,   // null = delete the whole key
    string Target,       // the missing file/folder it points to
    string Description)
{
    public string Location => $@"{SystemTools.ShortHive(Hive)}\{KeyPath}" + (ValueName != null ? $"  ›  {(ValueName.Length == 0 ? "(Default)" : ValueName)}" : "");
}

/// <summary>
/// A deliberately cautious registry cleaner. It only flags entries that point at a program or file
/// that definitely no longer exists, in a few well-understood places. Every fix is backed up to a .reg file first.
/// </summary>
public static class RegistryCleaner
{
    private static readonly (RegistryHive, RegistryView)[] MachineAndUser =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Default),
    ];

    public static List<RegistryIssue> Scan(Action<string>? progress = null)
    {
        var issues = new List<RegistryIssue>();
        progress?.Invoke("Checking installed program entries…");
        ScanUninstall(issues);
        progress?.Invoke("Checking application paths…");
        ScanAppPaths(issues);
        progress?.Invoke("Checking startup entries…");
        ScanRun(issues);
        progress?.Invoke("Checking shared files…");
        ScanSharedDlls(issues);
        progress?.Invoke("Checking program history…");
        ScanMuiCache(issues);

        // The same key can turn up from both 32/64-bit views on HKCU
        return issues.DistinctBy(i => (i.Hive, i.View, i.KeyPath.ToLowerInvariant(), i.ValueName)).ToList();
    }

    private static void Each(RegistryHive hive, RegistryView view, string path, Action<RegistryKey> action)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var k = root.OpenSubKey(path);
            if (k != null) action(k);
        }
        catch (Exception ex)
        {
            Log.Write($"Registry scan skipped {SystemTools.ShortHive(hive)}\\{path}: {ex.Message}");
        }
    }

    /// <summary>Apps &amp; Features entries whose program folder and uninstaller have both gone.</summary>
    private static void ScanUninstall(List<RegistryIssue> issues)
    {
        foreach (var (hive, view) in MachineAndUser)
        {
            Each(hive, view, Programs.UninstallPath, uninstall =>
            {
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var k = uninstall.OpenSubKey(name);
                        if (k == null) continue;
                        if (k.GetValue("WindowsInstaller") is int wi && wi == 1) continue; // MSI – leave to Windows Installer
                        var display = k.GetValue("DisplayName") as string;
                        var uninstallExe = SystemTools.ExtractPath(k.GetValue("UninstallString") as string);
                        var location = (k.GetValue("InstallLocation") as string)?.Trim().Trim('"');
                        if (string.IsNullOrWhiteSpace(display) || uninstallExe == null) continue;
                        if (uninstallExe.Contains("msiexec", StringComparison.OrdinalIgnoreCase) ||
                            uninstallExe.Contains("rundll32", StringComparison.OrdinalIgnoreCase)) continue;

                        var exeGone = SystemTools.IsDefinitelyMissing(uninstallExe);
                        var folderGone = string.IsNullOrWhiteSpace(location) || SystemTools.IsDefinitelyMissing(location);
                        if (exeGone && folderGone)
                            issues.Add(new RegistryIssue("Orphaned program entry", hive, view, $@"{Programs.UninstallPath}\{name}", null,
                                uninstallExe, $"\"{display}\" is listed in Apps & Features but its files are gone"));
                    }
                    catch { }
                }
            });
        }
    }

    /// <summary>App Paths entries (what lets you type a program's name in Run) pointing at missing programs.</summary>
    private static void ScanAppPaths(List<RegistryIssue> issues)
    {
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        foreach (var (hive, view) in MachineAndUser)
        {
            Each(hive, view, path, appPaths =>
            {
                foreach (var name in appPaths.GetSubKeyNames())
                {
                    try
                    {
                        using var k = appPaths.OpenSubKey(name);
                        var target = SystemTools.ExtractPath(k?.GetValue("") as string);
                        if (target != null && SystemTools.IsDefinitelyMissing(target))
                            issues.Add(new RegistryIssue("Missing application path", hive, view, $@"{path}\{name}", null,
                                target, $"{name} points to a program that no longer exists"));
                    }
                    catch { }
                }
            });
        }
    }

    /// <summary>Startup entries that launch a program which no longer exists.</summary>
    private static void ScanRun(List<RegistryIssue> issues)
    {
        foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" })
        {
            foreach (var (hive, view) in MachineAndUser)
            {
                Each(hive, view, path, run =>
                {
                    foreach (var value in run.GetValueNames())
                    {
                        var target = SystemTools.ExtractPath(run.GetValue(value) as string);
                        if (target != null && SystemTools.IsDefinitelyMissing(target))
                            issues.Add(new RegistryIssue("Broken startup entry", hive, view, path, value,
                                target, $"\"{value}\" tries to start a program that no longer exists"));
                    }
                });
            }
        }
    }

    /// <summary>Shared DLL reference counts for files that have been deleted.</summary>
    private static void ScanSharedDlls(List<RegistryIssue> issues)
    {
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs";
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            Each(RegistryHive.LocalMachine, view, path, k =>
            {
                foreach (var value in k.GetValueNames())
                {
                    if (SystemTools.IsDefinitelyMissing(value))
                        issues.Add(new RegistryIssue("Missing shared file", RegistryHive.LocalMachine, view, path, value,
                            value, "Reference to a shared file that has been deleted"));
                }
            });
        }
    }

    /// <summary>The list of program names Windows remembers, for programs that have been deleted.</summary>
    private static void ScanMuiCache(List<RegistryIssue> issues)
    {
        const string path = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
        Each(RegistryHive.CurrentUser, RegistryView.Default, path, k =>
        {
            foreach (var value in k.GetValueNames())
            {
                // Values look like "C:\Path\app.exe.FriendlyAppName"
                var exe = SystemTools.ExtractPath(value);
                if (exe != null && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && SystemTools.IsDefinitelyMissing(exe))
                    issues.Add(new RegistryIssue("Old program history", RegistryHive.CurrentUser, RegistryView.Default, path, value,
                        exe, "Windows remembers the name of a program that has been deleted"));
            }
        });
    }

    /// <summary>Backs up then fixes the chosen issues. Returns how many were fixed and where the backup is.</summary>
    public static async Task<(int Fixed, int Failed, string BackupFolder)> FixAsync(IEnumerable<RegistryIssue> chosen)
    {
        var backup = SystemTools.NewBackupFolder("registry cleaner");
        int ok = 0, failed = 0;

        foreach (var group in chosen.GroupBy(i => (i.Hive, i.View, i.KeyPath)))
        {
            // One backup per key, taken before anything in it changes
            if (!await SystemTools.ExportKeyAsync(group.Key.Hive, group.Key.View, group.Key.KeyPath, backup))
            {
                failed += group.Count();
                continue;
            }

            foreach (var issue in group)
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(issue.Hive, issue.View);
                    if (issue.ValueName == null)
                    {
                        root.DeleteSubKeyTree(issue.KeyPath, throwOnMissingSubKey: false);
                    }
                    else
                    {
                        using var k = root.OpenSubKey(issue.KeyPath, writable: true);
                        k?.DeleteValue(issue.ValueName, throwOnMissingValue: false);
                    }
                    Log.Write($"Fixed: {issue.Location}");
                    ok++;
                }
                catch (Exception ex)
                {
                    Log.Write($"Couldn't fix {issue.Location}: {ex.Message}");
                    failed++;
                }
            }
        }
        return (ok, failed, backup);
    }

    public static List<string> Backups() =>
        Directory.EnumerateDirectories(SystemTools.BackupsRoot)
            .Where(d => Directory.EnumerateFiles(d, "*.reg").Any())
            .OrderByDescending(d => d)
            .ToList();
}
