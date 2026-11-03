using System.IO;
using System.Diagnostics;

namespace DriverWave.Services;

/// <summary>
/// Network drivers shipped inside the installer (OfflineDrivers folder next to the exe),
/// so a freshly installed PC with no working Ethernet/Wi-Fi can get online first.
/// </summary>
public static class OfflineDriverPack
{
    public static string Folder => Path.Combine(AppInfo.AppFolder, "OfflineDrivers");

    public static bool IsAvailable =>
        Directory.Exists(Folder) &&
        Directory.EnumerateFiles(Folder, "*.inf", SearchOption.AllDirectories).Any();

    /// <summary>
    /// Adds every .inf in the pack to the Windows driver store and installs it on any matching device.
    /// pnputil only installs where the hardware actually matches, so unrelated drivers are just staged.
    /// </summary>
    public static async Task<InstallOutcome> InstallAsync(CancellationToken ct = default)
    {
        if (!IsAvailable) return new InstallOutcome(false, false, "No offline driver pack found");

        var pnputil = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
        var psi = new ProcessStartInfo(pnputil)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/add-driver");
        psi.ArgumentList.Add(Path.Combine(Folder, "*.inf"));
        psi.ArgumentList.Add("/subdirs");
        psi.ArgumentList.Add("/install");

        Log.Write($"Installing offline network drivers from {Folder}");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start pnputil");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);

        foreach (var line in ((await stdout) + (await stderr)).Split('\n'))
            if (!string.IsNullOrWhiteSpace(line)) Log.Write("  pnputil: " + line.TrimEnd());

        // 0 = ok, 3010 = ok but restart needed, 259 = nothing matched this PC's hardware
        return p.ExitCode switch
        {
            0 => new InstallOutcome(true, false, "Network drivers installed"),
            3010 => new InstallOutcome(true, true, "Network drivers installed – restart needed"),
            259 => new InstallOutcome(false, false, "None of the bundled drivers match this PC's network hardware"),
            _ => new InstallOutcome(false, false, $"pnputil exited with code {p.ExitCode}"),
        };
    }
}
