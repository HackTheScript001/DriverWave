using System.IO;

namespace DriverWave.Services;

public sealed record CleanupTarget(
    string Name,
    string Description,
    string[] Folders,          // may contain * for one folder level, e.g. ...\User Data\*\Cache
    string FilePattern,
    TimeSpan MinAge,           // only delete files older than this (protects things in use right now)
    bool OnByDefault,
    bool IsRecycleBin = false);

/// <summary>Junk-file cleanup: temp files, caches, update leftovers, crash dumps, Recycle Bin.</summary>
public static class Cleanup
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string WinDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string SystemDrive => (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + "\\";

    public static List<CleanupTarget> Targets() =>
    [
        new("Temporary files", "Files programs left in your temp folder (older than a day)",
            [Path.GetTempPath()], "*", TimeSpan.FromDays(1), true),
        new("Windows temporary files", "Windows' own temp folder (older than a day)",
            [Path.Combine(WinDir, "Temp")], "*", TimeSpan.FromDays(1), true),
        new("Windows Update downloads", "Update files that have already been installed",
            [Path.Combine(WinDir, @"SoftwareDistribution\Download")], "*", TimeSpan.FromDays(2), true),
        new("Delivery Optimisation cache", "Update pieces Windows keeps to share with other PCs",
            [Path.Combine(WinDir, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache")], "*", TimeSpan.Zero, true),
        new("Error reports", "Crash reports waiting to be sent to Microsoft",
            [Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportQueue")], "*", TimeSpan.Zero, true),
        new("Crash dumps", "Memory dumps from programs and Windows crashes",
            [Path.Combine(Local, "CrashDumps"), Path.Combine(WinDir, "Minidump"), Path.Combine(WinDir, "LiveKernelReports")], "*.dmp", TimeSpan.Zero, true),
        new("Thumbnail cache", "Picture previews – Windows rebuilds them when needed",
            [Path.Combine(Local, @"Microsoft\Windows\Explorer")], "thumbcache_*.db", TimeSpan.Zero, true),
        new("DirectX shader cache", "Rebuilt automatically by games and apps",
            [Path.Combine(Local, "D3DSCache"), Path.Combine(Local, @"NVIDIA\DXCache"), Path.Combine(Local, @"AMD\DxCache")], "*", TimeSpan.Zero, true),
        new("Driver installer leftovers", "Files NVIDIA and AMD unpack during driver installs",
            [Path.Combine(SystemDrive, "NVIDIA"), Path.Combine(SystemDrive, "AMD")], "*", TimeSpan.Zero, true),
        new("Browser caches", "Cached web pages in Chrome, Edge, Brave and Firefox (not passwords, history or cookies)",
            [
                Path.Combine(Local, @"Google\Chrome\User Data\*\Cache"),
                Path.Combine(Local, @"Google\Chrome\User Data\*\Code Cache"),
                Path.Combine(Local, @"Google\Chrome\User Data\*\GPUCache"),
                Path.Combine(Local, @"Microsoft\Edge\User Data\*\Cache"),
                Path.Combine(Local, @"Microsoft\Edge\User Data\*\Code Cache"),
                Path.Combine(Local, @"Microsoft\Edge\User Data\*\GPUCache"),
                Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data\*\Cache"),
                Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data\*\Code Cache"),
                Path.Combine(Local, @"Mozilla\Firefox\Profiles\*\cache2"),
            ], "*", TimeSpan.Zero, true),
        new("DriverWave downloads", "Driver installers DriverWave downloaded",
            [Path.Combine(AppInfo.DataFolder, "Downloads")], "*", TimeSpan.Zero, true),
        new("Recycle Bin", "Everything in the Recycle Bin – it can't be recovered afterwards",
            [], "*", TimeSpan.Zero, false, IsRecycleBin: true),
    ];

    /// <summary>Expands one level of * wildcards in a folder path.</summary>
    private static IEnumerable<string> ExpandFolder(string pattern)
    {
        if (!pattern.Contains('*'))
        {
            if (Directory.Exists(pattern)) yield return pattern;
            yield break;
        }

        var i = pattern.IndexOf('*');
        var parent = pattern[..i].TrimEnd('\\');
        var rest = pattern[(i + 1)..].TrimStart('\\');
        if (!Directory.Exists(parent)) yield break;

        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(parent).ToList(); }
        catch { yield break; }

        foreach (var d in dirs)
        {
            var candidate = rest.Length == 0 ? d : Path.Combine(d, rest);
            foreach (var x in ExpandFolder(candidate)) yield return x;
        }
    }

    private static IEnumerable<FileInfo> Files(CleanupTarget t)
    {
        var cutoff = DateTime.Now - t.MinAge;
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = t.FilePattern == "*",
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };

        foreach (var folder in t.Folders.SelectMany(ExpandFolder))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, t.FilePattern, opts); }
            catch { continue; }

            foreach (var f in files)
            {
                FileInfo fi;
                try { fi = new FileInfo(f); } catch { continue; }
                if (t.MinAge > TimeSpan.Zero && fi.LastWriteTime > cutoff) continue;
                yield return fi;
            }
        }
    }

    public static long Measure(CleanupTarget t)
    {
        if (t.IsRecycleBin) return SystemTools.RecycleBinSize();
        long total = 0;
        foreach (var f in Files(t))
        {
            try { total += f.Length; } catch { }
        }
        return total;
    }

    /// <summary>Deletes the files. Anything in use is skipped. Returns bytes freed.</summary>
    public static long Clean(CleanupTarget t)
    {
        if (t.IsRecycleBin)
        {
            var size = SystemTools.RecycleBinSize();
            SystemTools.EmptyRecycleBin();
            Log.Write($"Emptied Recycle Bin ({SystemTools.FormatSize(size)})");
            return size;
        }

        long freed = 0;
        int deleted = 0, skipped = 0;
        foreach (var f in Files(t).ToList())
        {
            try
            {
                var len = f.Length;
                if (f.IsReadOnly) f.IsReadOnly = false;
                f.Delete();
                freed += len;
                deleted++;
            }
            catch { skipped++; } // in use – leave it
        }

        // Tidy up empty sub-folders, but never the folder itself
        foreach (var folder in t.Folders.SelectMany(ExpandFolder))
            RemoveEmptySubfolders(folder);

        Log.Write($"Cleaned {t.Name}: {deleted} files, {SystemTools.FormatSize(freed)}" + (skipped > 0 ? $", {skipped} in use and skipped" : ""));
        return freed;
    }

    private static void RemoveEmptySubfolders(string folder)
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(folder))
            {
                if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                RemoveEmptySubfolders(d);
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
                }
                catch { }
            }
        }
        catch { }
    }
}
