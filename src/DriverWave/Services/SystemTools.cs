using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DriverWave.Services;

public sealed record RunResult(int ExitCode, string Output);

/// <summary>Small helpers shared by the tools: running programs, paths, recycle bin, registry backups.</summary>
public static class SystemTools
{
    // ───────────── Running programs ─────────────

    public static async Task<RunResult> RunAsync(string file, IEnumerable<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {file}");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return new RunResult(p.ExitCode, (await stdout) + (await stderr));
    }

    public static string System32(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    public static void OpenInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write($"Couldn't open {path}: {ex.Message}"); }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"Couldn't open {url}: {ex.Message}"); }
    }

    // ───────────── Command lines ─────────────

    /// <summary>
    /// Pulls the program path out of a command line such as
    /// <c>"C:\Program Files\App\app.exe" --flag</c> or <c>C:\App\app.exe /x</c>.
    /// Returns null if no path can be worked out.
    /// </summary>
    public static string? ExtractPath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var cmd = Environment.ExpandEnvironmentVariables(command.Trim());

        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd[1..end] : null;
        }

        foreach (var ext in new[] { ".exe", ".dll", ".cmd", ".bat", ".lnk", ".com", ".vbs", ".ps1" })
        {
            var i = cmd.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
            if (i > 0) return cmd[..(i + ext.Length)];
        }

        var space = cmd.IndexOf(' ');
        return space > 0 ? cmd[..space] : cmd;
    }

    /// <summary>Splits a command line into program and arguments.</summary>
    public static (string File, string Args) SplitCommand(string command)
    {
        var cmd = Environment.ExpandEnvironmentVariables(command.Trim());
        var file = ExtractPath(cmd) ?? cmd;
        var rest = cmd.StartsWith('"') ? cmd[Math.Min(cmd.Length, file.Length + 2)..] : cmd[Math.Min(cmd.Length, file.Length)..];
        return (file, rest.Trim());
    }

    /// <summary>
    /// True only when we can be sure the target is gone: it's a full local path and the file doesn't exist.
    /// Bare names (e.g. "rundll32.exe") or network paths are treated as present.
    /// </summary>
    public static bool IsDefinitelyMissing(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\")) return false;
        if (path.Length < 4 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;

        try
        {
            // The drive must exist – otherwise it may just be an unplugged USB drive
            var root = Path.GetPathRoot(path);
            if (root == null || !Directory.Exists(root)) return false;
            return !File.Exists(path) && !Directory.Exists(path);
        }
        catch { return false; }
    }

    // ───────────── Sizes ─────────────

    public static long FolderSize(string path)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "",
        < 1024 * 1024 => $"{bytes / 1024d:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024d:0.0} MB",
        _ => $"{bytes / 1024d / 1024d / 1024d:0.00} GB",
    };

    // ───────────── Recycle bin ─────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400;

    /// <summary>Moves a file or folder to the Recycle Bin, so it can be restored.</summary>
    public static bool SendToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        var r = SHFileOperation(ref op);
        if (r != 0) Log.Write($"Couldn't recycle {path} (error {r})");
        return r == 0 && !op.fAnyOperationsAborted;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint flags);

    public static long RecycleBinSize()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
    }

    public static void EmptyRecycleBin()
    {
        const uint SHERB_NOCONFIRMATION = 1, SHERB_NOPROGRESSUI = 2, SHERB_NOSOUND = 4;
        SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
    }

    // ───────────── Registry ─────────────

    public static string HiveName(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
        RegistryHive.ClassesRoot => "HKEY_CLASSES_ROOT",
        RegistryHive.Users => "HKEY_USERS",
        _ => hive.ToString(),
    };

    public static string ShortHive(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.ClassesRoot => "HKCR",
        _ => hive.ToString(),
    };

    /// <summary>A new timestamped folder under %ProgramData%\DriverWave\Backups for this job's .reg files.</summary>
    public static string NewBackupFolder(string label)
    {
        var dir = Path.Combine(AppInfo.DataFolder, "Backups", $"{DateTime.Now:yyyy-MM-dd_HHmmss} {label}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string BackupsRoot
    {
        get
        {
            var dir = Path.Combine(AppInfo.DataFolder, "Backups");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Exports a registry key (and everything under it) to a .reg file with reg.exe.</summary>
    public static async Task<bool> ExportKeyAsync(RegistryHive hive, RegistryView view, string subKey, string folder)
    {
        var safe = new StringBuilder();
        foreach (var c in $"{ShortHive(hive)}_{subKey}")
            safe.Append(Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c);
        var name = safe.ToString();
        if (name.Length > 120) name = name[..120];
        var file = Path.Combine(folder, $"{name}_{Guid.NewGuid().ToString("N")[..6]}.reg");

        var args = new List<string> { "export", $"{HiveName(hive)}\\{subKey}", file, "/y" };
        if (hive == RegistryHive.LocalMachine)
            args.Add(view == RegistryView.Registry32 ? "/reg:32" : "/reg:64");

        var r = await RunAsync(System32("reg.exe"), args);
        if (r.ExitCode != 0) Log.Write($"Backup of {ShortHive(hive)}\\{subKey} failed: {r.Output.Trim()}");
        return r.ExitCode == 0;
    }

    public static async Task<bool> ImportRegAsync(string file)
    {
        var r = await RunAsync(System32("reg.exe"), ["import", file]);
        Log.Write(r.ExitCode == 0 ? $"Restored {Path.GetFileName(file)}" : $"Restore of {file} failed: {r.Output.Trim()}");
        return r.ExitCode == 0;
    }
}
