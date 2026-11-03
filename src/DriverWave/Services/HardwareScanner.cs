using System.Management;

namespace DriverWave.Services;

public sealed record PnpDevice(
    string Name,
    string DeviceId,
    string ClassName,
    int ProblemCode,
    string[] HardwareIds,
    string[] CompatibleIds)
{
    public IEnumerable<string> AllIds => HardwareIds.Concat(CompatibleIds);
}

/// <summary>Reads every Plug and Play device from WMI and works out which ones need a driver.</summary>
public static class HardwareScanner
{
    // Problem codes that don't mean "needs a driver"
    private static readonly HashSet<int> IgnoredCodes =
    [
        0,  // working
        22, // disabled by the user
        24, // device not present / removed
        45, // not currently connected (phantom device)
    ];

    // Fallback drivers Windows uses when the real one is missing
    private static readonly string[] GenericNames =
    [
        "Microsoft Basic Display Adapter",
        "Microsoft Basic Render Driver",
        "Standard VGA Graphics Adapter",
    ];

    public static List<PnpDevice> GetAllDevices()
    {
        var list = new List<PnpDevice>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, DeviceID, PNPClass, ConfigManagerErrorCode, HardwareID, CompatibleID, Present FROM Win32_PnPEntity");

        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo)
            {
                try
                {
                    if (mo["Present"] is bool present && !present) continue;

                    var id = mo["DeviceID"] as string ?? "";
                    var name = mo["Name"] as string;
                    if (string.IsNullOrWhiteSpace(name)) name = id;

                    list.Add(new PnpDevice(
                        name!,
                        id,
                        mo["PNPClass"] as string ?? "Unknown",
                        mo["ConfigManagerErrorCode"] is uint code ? (int)code : 0,
                        mo["HardwareID"] as string[] ?? [],
                        mo["CompatibleID"] as string[] ?? []));
                }
                catch (Exception ex)
                {
                    Log.Write($"Skipped a device while scanning: {ex.Message}");
                }
            }
        }

        Log.Write($"Scan found {list.Count} devices");
        return list;
    }

    public static bool NeedsDriver(PnpDevice d) => !IgnoredCodes.Contains(d.ProblemCode);

    public static bool IsOnGenericDriver(PnpDevice d) =>
        d.ProblemCode == 0 && GenericNames.Any(g => d.Name.Contains(g, StringComparison.OrdinalIgnoreCase));

    /// <summary>True if any network adapter (Ethernet/Wi-Fi) is missing its driver.</summary>
    public static bool HasMissingNetworkDriver(IEnumerable<PnpDevice> devices) =>
        devices.Any(d => NeedsDriver(d) &&
            (string.Equals(d.ClassName, "Net", StringComparison.OrdinalIgnoreCase) ||
             d.HardwareIds.Any(h => h.StartsWith("PCI\\CC_02", StringComparison.OrdinalIgnoreCase)) ||
             d.CompatibleIds.Any(h => h.StartsWith("PCI\\CC_02", StringComparison.OrdinalIgnoreCase))));
}
