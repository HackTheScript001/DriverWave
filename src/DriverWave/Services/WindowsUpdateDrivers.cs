namespace DriverWave.Services;

public sealed record DriverUpdate(
    dynamic Update,
    string Title,
    string HardwareId,
    string Manufacturer,
    string Model,
    string DriverClass,
    long DownloadBytes);

public sealed record InstallOutcome(bool Success, bool RebootRequired, string Message);

/// <summary>
/// Talks to the Windows Update Agent (the same engine Settings › Windows Update uses)
/// to find, download and install drivers signed and published by Microsoft.
/// Uses late-bound COM so no interop assembly is needed.
/// </summary>
public static class WindowsUpdateDrivers
{
    private const int ServerSelectionWindowsUpdate = 2; // always ask Microsoft, never a WSUS server

    // OperationResultCode
    private const int OrcSucceeded = 2;
    private const int OrcSucceededWithErrors = 3;

    private static dynamic CreateSession()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session")
                   ?? throw new InvalidOperationException("Windows Update Agent is not available on this PC.");
        dynamic session = Activator.CreateInstance(type)!;
        session.ClientApplicationID = "DriverWave";
        return session;
    }

    private static dynamic CreateCollection()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!;
        return Activator.CreateInstance(type)!;
    }

    /// <summary>Asks Windows Update for every driver that applies to this PC and isn't installed yet.</summary>
    public static List<DriverUpdate> Search()
    {
        Log.Write("Searching Windows Update for drivers…");
        dynamic session = CreateSession();
        dynamic searcher = session.CreateUpdateSearcher();
        searcher.ServerSelection = ServerSelectionWindowsUpdate;
        searcher.Online = true;

        dynamic result = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
        int count = result.Updates.Count;
        var list = new List<DriverUpdate>(count);

        for (int i = 0; i < count; i++)
        {
            dynamic u = result.Updates[i];
            try
            {
                list.Add(new DriverUpdate(
                    (object)u,
                    Str(() => u.Title),
                    Str(() => u.DriverHardwareID),
                    Str(() => u.DriverManufacturer),
                    Str(() => u.DriverModel),
                    Str(() => u.DriverClass),
                    Lng(() => u.MaxDownloadSize)));
            }
            catch (Exception ex)
            {
                Log.Write($"Couldn't read a driver update: {ex.Message}");
            }
        }

        Log.Write($"Windows Update offered {list.Count} driver(s)");
        foreach (var d in list) Log.Write($"  • {d.Title}  [{d.HardwareId}]");
        return list;
    }

    /// <summary>Downloads and installs one driver update.</summary>
    public static InstallOutcome Install(dynamic update, Action<string>? phase = null)
    {
        dynamic session = CreateSession();
        string title = Str(() => update.Title);

        try
        {
            if (!(bool)update.EulaAccepted) update.AcceptEula();
        }
        catch { /* drivers rarely carry an EULA */ }

        dynamic coll = CreateCollection();
        coll.Add(update);

        if (!(bool)update.IsDownloaded)
        {
            phase?.Invoke("download");
            Log.Write($"Downloading {title}");
            dynamic downloader = session.CreateUpdateDownloader();
            downloader.Updates = coll;
            dynamic dl = downloader.Download();
            int dlCode = (int)dl.ResultCode;
            if (dlCode != OrcSucceeded && dlCode != OrcSucceededWithErrors)
                return Fail(title, $"download failed ({DescribeHResult(dl)})");
        }

        phase?.Invoke("install");
        Log.Write($"Installing {title}");
        dynamic installer = session.CreateUpdateInstaller();
        installer.Updates = coll;
        try { installer.ForceQuiet = true; } catch { }

        dynamic res = installer.Install();
        int code = (int)res.GetUpdateResult(0).ResultCode;
        bool reboot = (bool)res.RebootRequired;

        if (code == OrcSucceeded || code == OrcSucceededWithErrors)
        {
            Log.Write($"Installed {title}{(reboot ? " (restart needed)" : "")}");
            return new InstallOutcome(true, reboot, "Installed");
        }

        return Fail(title, $"install failed ({DescribeHResult(res.GetUpdateResult(0))})");
    }

    private static InstallOutcome Fail(string title, string why)
    {
        Log.Write($"{title}: {why}");
        return new InstallOutcome(false, false, why);
    }

    private static string DescribeHResult(dynamic result)
    {
        try
        {
            int hr = (int)result.HResult;
            return hr == 0 ? "no error code" : $"0x{hr:X8}";
        }
        catch { return "unknown error"; }
    }

    private static string Str(Func<object?> get)
    {
        try { return get()?.ToString() ?? ""; } catch { return ""; }
    }

    private static long Lng(Func<object?> get)
    {
        try { return Convert.ToInt64(get() ?? 0); } catch { return 0; }
    }
}
