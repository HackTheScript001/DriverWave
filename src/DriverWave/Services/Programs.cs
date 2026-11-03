using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DriverWave.Services;

public sealed record InstalledProgram(
    string Name,
    string Publisher,
    string Version,
    string InstallLocation,
    string UninstallString,
    string QuietUninstallString,
    long SizeBytes,
    DateTime? InstallDate,
    RegistryHive Hive,
    RegistryView View,
    string KeyName)
{
    public string UninstallKeyPath => $@"{Programs.UninstallPath}\{KeyName}";
    public bool IsMsi => Regex.IsMatch(KeyName, @"^\{[0-9A-F\-]{36}\}$", RegexOptions.IgnoreCase) &&
                         UninstallString.Contains("msiexec", StringComparison.OrdinalIgnoreCase);
}

public enum LeftoverKind { Folder, File, RegistryKey }

public sealed record Leftover(LeftoverKind Kind, string Path, RegistryHive Hive, RegistryView View, string Reason, long SizeBytes)
{
    public string Display => Kind == LeftoverKind.RegistryKey
        ? $@"{SystemTools.ShortHive(Hive)}\{Path}{(View == RegistryView.Registry32 && Hive == RegistryHive.LocalMachine ? "  (32-bit)" : "")}"
        : Path;
}

/// <summary>
/// Lists installed programs, runs their own uninstallers, then finds what they left behind
/// (folders, registry keys) the way Revo Uninstaller's "moderate" scan does – exact name matches only.
/// </summary>
public static class Programs
{
    public const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    // Never offer these as leftovers, whatever the program is called
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Windows NT", "Classes", "Policies", "Wow6432Node", "Clients", "RegisteredApplications",
        "Common Files", "Internet Explorer", "Windows Defender", "WindowsApps", "Microsoft.NET", "dotnet", "Intel", "AMD",
        "NVIDIA Corporation", "Packages", "Temp", "Programs", "Startup", "Start Menu", "Desktop", "Documents",
        "Google", "Mozilla", "Apple", "Adobe", "Package Cache", "Installer", "System32", "SysWOW64",
        "Program Files", "Program Files (x86)", "ProgramData", "Users", "AppData", "Local", "LocalLow", "Roaming",
        "Steam", "steamapps", "common", "Epic Games", "Games", "Software", "Default", "Public",
    };

    public static List<InstalledProgram> List()
    {
        var all = new List<InstalledProgram>();
        Read(RegistryHive.LocalMachine, RegistryView.Registry64, all);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32, all);
        Read(RegistryHive.CurrentUser, RegistryView.Default, all);

        // Same program can appear in more than one place
        return all
            .GroupBy(p => (p.Name.ToLowerInvariant(), p.Version))
            .Select(g => g.First())
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void Read(RegistryHive hive, RegistryView view, List<InstalledProgram> into)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallPath);
            if (uninstall == null) return;

            foreach (var keyName in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var k = uninstall.OpenSubKey(keyName);
                    if (k == null) continue;

                    var name = (k.GetValue("DisplayName") as string)?.Trim();
                    var uninstallString = k.GetValue("UninstallString") as string ?? "";
                    if (string.IsNullOrEmpty(name)) continue;
                    if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;   // hidden from Apps & Features
                    if (k.GetValue("ParentKeyName") != null) continue;                    // updates/patches
                    if (k.GetValue("ReleaseType") is string rt && rt.Contains("Update", StringComparison.OrdinalIgnoreCase)) continue;
                    if (uninstallString.Length == 0 && k.GetValue("WindowsInstaller") is not int) continue;

                    DateTime? date = null;
                    if (k.GetValue("InstallDate") is string ds && DateTime.TryParseExact(ds, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d))
                        date = d;

                    long size = k.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0;

                    into.Add(new InstalledProgram(
                        name,
                        (k.GetValue("Publisher") as string ?? "").Trim(),
                        (k.GetValue("DisplayVersion") as string ?? "").Trim(),
                        (k.GetValue("InstallLocation") as string ?? "").Trim().Trim('"').TrimEnd('\\'),
                        uninstallString,
                        k.GetValue("QuietUninstallString") as string ?? "",
                        size,
                        date,
                        hive,
                        view,
                        keyName));
                }
                catch { /* unreadable entry */ }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't read programs from {hive}/{view}: {ex.Message}");
        }
    }

    /// <summary>Runs the program's own uninstaller and waits for it (and anything it spawns) to finish.</summary>
    public static async Task<int> RunUninstallerAsync(InstalledProgram p)
    {
        string file, args;
        if (p.IsMsi)
        {
            file = SystemTools.System32("msiexec.exe");
            args = $"/x {p.KeyName}";
        }
        else
        {
            (file, args) = SystemTools.SplitCommand(p.UninstallString);
        }

        Log.Write($"Uninstalling {p.Name}: {file} {args}");
        if (!File.Exists(file) && !p.IsMsi)
        {
            Log.Write("The uninstaller is missing – use 'Remove leftovers' to clean up instead.");
            return -1;
        }

        var before = DateTime.Now;
        using var proc = Process.Start(new ProcessStartInfo(file, args)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(file) ?? Environment.SystemDirectory,
        }) ?? throw new InvalidOperationException("Couldn't start the uninstaller");

        await proc.WaitForExitAsync();
        var code = proc.ExitCode;

        // Many uninstallers (NSIS, Inno) copy themselves to %TEMP% and relaunch, so the first process exits straight away.
        await WaitForTempUninstallersAsync(before);
        Log.Write($"Uninstaller for {p.Name} finished (code {code})");
        return code;
    }

    private static async Task WaitForTempUninstallersAsync(DateTime since)
    {
        var temp = Path.GetTempPath();
        for (var i = 0; i < 1800; i++) // up to an hour
        {
            var running = Process.GetProcesses().Where(pr =>
            {
                try
                {
                    var path = pr.MainModule?.FileName ?? "";
                    return pr.StartTime >= since.AddSeconds(-2) &&
                           (path.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                            Regex.IsMatch(Path.GetFileName(path), @"^(Au_|Un_A|unins\d+|uninst)", RegexOptions.IgnoreCase));
                }
                catch { return false; }
                finally { pr.Dispose(); }
            }).Any();

            if (!running) return;
            await Task.Delay(2000);
        }
    }

    public static bool StillInstalled(InstalledProgram p)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(p.Hive, p.View);
            using var k = root.OpenSubKey(p.UninstallKeyPath);
            return k != null;
        }
        catch { return false; }
    }

    // ───────────── Leftover scan ─────────────

    /// <summary>Names the program is likely to use for its folders and keys, e.g. "VLC media player 3.0.21 (64-bit)" → "VLC media player".</summary>
    private static List<string> CandidateNames(InstalledProgram p)
    {
        var names = new List<string> { p.Name };
        var cleaned = Regex.Replace(p.Name, @"\s*\((x64|x86|64-bit|32-bit|64 bit|32 bit)\)", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+v?\d+(\.\d+)+.*$", "").Trim();
        cleaned = Regex.Replace(cleaned, @"\s+(x64|x86|64-bit|32-bit)$", "", RegexOptions.IgnoreCase).Trim();
        names.Add(cleaned);

        if (p.InstallLocation.Length > 3)
            names.Add(Path.GetFileName(p.InstallLocation.TrimEnd('\\')));

        return names
            .Select(n => n.Trim())
            .Where(n => n.Length >= 4 && !Protected.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? PublisherFolderName(InstalledProgram p)
    {
        var pub = Regex.Replace(p.Publisher, @"[,.]?\s*(Inc|Ltd|LLC|GmbH|Corp|Corporation|Limited|Co|S\.A|B\.V)\.?$", "", RegexOptions.IgnoreCase).Trim();
        return pub.Length >= 3 && !Protected.Contains(pub) && !Protected.Contains(p.Publisher) ? pub : null;
    }

    public static List<Leftover> FindLeftovers(InstalledProgram p)
    {
        var found = new List<Leftover>();
        var names = CandidateNames(p);
        var publisher = PublisherFolderName(p);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddFolder(string path, string reason)
        {
            if (!Directory.Exists(path) || !seen.Add(path)) return;
            var leaf = Path.GetFileName(path.TrimEnd('\\'));
            if (Protected.Contains(leaf)) return;
            found.Add(new Leftover(LeftoverKind.Folder, path, default, default, reason, SystemTools.FolderSize(path)));
        }

        // 1. The install folder itself
        if (p.InstallLocation.Length > 3 && Path.IsPathFullyQualified(p.InstallLocation) &&
            Path.GetPathRoot(p.InstallLocation)?.TrimEnd('\\').Length != p.InstallLocation.TrimEnd('\\').Length)
            AddFolder(p.InstallLocation, "Install folder");

        // 2. Folders with the program's exact name in the usual places
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        }.Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            foreach (var n in names)
            {
                AddFolder(Path.Combine(root, n), "Program data");
                if (publisher != null) AddFolder(Path.Combine(root, publisher, n), "Program data");
            }
        }

        // 3. Shortcuts named after the program
        foreach (var dir in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                     Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                 })
        {
            foreach (var n in names)
            {
                var lnk = Path.Combine(dir, n + ".lnk");
                if (File.Exists(lnk) && seen.Add(lnk))
                    found.Add(new Leftover(LeftoverKind.File, lnk, default, default, "Shortcut", 0));
            }
        }

        // 4. Registry keys with the exact name
        void AddKey(RegistryHive hive, RegistryView view, string path, string reason)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var k = root.OpenSubKey(path);
                if (k == null) return;
                var id = $"{hive}|{view}|{path}";
                if (!seen.Add(id)) return;
                found.Add(new Leftover(LeftoverKind.RegistryKey, path, hive, view, reason, 0));
            }
            catch { }
        }

        var regViews = new[]
        {
            (RegistryHive.CurrentUser, RegistryView.Default),
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
        };

        foreach (var (hive, view) in regViews)
        {
            foreach (var n in names)
            {
                AddKey(hive, view, $@"SOFTWARE\{n}", "Settings");
                if (publisher != null) AddKey(hive, view, $@"SOFTWARE\{publisher}\{n}", "Settings");
            }
        }

        // 5. The uninstall entry, if the uninstaller didn't remove it
        AddKey(p.Hive, p.View, p.UninstallKeyPath, "Apps & Features entry");

        return found;
    }

    /// <summary>Removes the chosen leftovers. Registry keys are backed up to .reg files first; files go to the Recycle Bin.</summary>
    public static async Task<(int Removed, int Failed, string BackupFolder)> RemoveLeftoversAsync(IEnumerable<Leftover> items, string label)
    {
        var backup = SystemTools.NewBackupFolder(label);
        int removed = 0, failed = 0;

        foreach (var item in items)
        {
            try
            {
                switch (item.Kind)
                {
                    case LeftoverKind.RegistryKey:
                        if (!await SystemTools.ExportKeyAsync(item.Hive, item.View, item.Path, backup))
                        {
                            failed++;
                            continue; // never delete a key we couldn't back up
                        }
                        using (var root = RegistryKey.OpenBaseKey(item.Hive, item.View))
                            root.DeleteSubKeyTree(item.Path, throwOnMissingSubKey: false);
                        break;

                    default:
                        if (!SystemTools.SendToRecycleBin(item.Path)) { failed++; continue; }
                        break;
                }
                Log.Write($"Removed {item.Display}");
                removed++;
            }
            catch (Exception ex)
            {
                Log.Write($"Couldn't remove {item.Display}: {ex.Message}");
                failed++;
            }
        }
        return (removed, failed, backup);
    }
}
