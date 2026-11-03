using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DriverWave.Services;

public enum GpuVendor { Nvidia, Amd, Intel, Other }

public sealed record GpuInfo(string Name, GpuVendor Vendor, string PnpId, string DriverVersion, DateTime? DriverDate)
{
    /// <summary>True when Windows is using its fallback display driver, so the real name isn't known yet.</summary>
    public bool OnBasicDriver =>
        Name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("Standard VGA", StringComparison.OrdinalIgnoreCase);
}

public sealed record NvidiaDriver(string Version, string Name, string DownloadUrl, long Size);

/// <summary>
/// Graphics card drivers straight from the manufacturer:
/// NVIDIA via NVIDIA's own driver lookup service (downloaded and installed silently),
/// AMD and Intel via their official auto-detect tools.
/// </summary>
public static class GraphicsDrivers
{
    public const string NvidiaDriversPage = "https://www.nvidia.com/en-gb/drivers/";
    public const string AmdDriversPage = "https://www.amd.com/en/support/download/drivers.html";
    public const string IntelDetectPage = "https://www.intel.com/content/www/us/en/support/detect.html";
    private const string IntelDsaInstaller = "https://dsadata.intel.com/installer";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        // Vendor sites reject requests that don't look like a browser
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-GB,en;q=0.9");
        return c;
    }

    private static string DownloadFolder
    {
        get
        {
            var dir = Path.Combine(AppInfo.DataFolder, "Downloads");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    // ───────────── Detection ─────────────

    public static List<GpuInfo> Detect()
    {
        var list = new List<GpuInfo>();
        using var searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, DriverVersion, DriverDate FROM Win32_VideoController");
        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo)
            {
                var pnp = mo["PNPDeviceID"] as string ?? "";
                if (!pnp.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue; // skip remote/virtual displays

                var vendor = pnp.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Nvidia
                    : pnp.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Amd
                    : pnp.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Intel
                    : GpuVendor.Other;

                list.Add(new GpuInfo(
                    (mo["Name"] as string ?? "Graphics card").Trim(),
                    vendor,
                    pnp,
                    mo["DriverVersion"] as string ?? "",
                    ParseCimDate(mo["DriverDate"] as string)));
            }
        }
        Log.Write($"Graphics: {string.Join(", ", list.Select(g => $"{g.Name} ({g.Vendor}, driver {g.DriverVersion})"))}");
        return list;
    }

    private static DateTime? ParseCimDate(string? s) =>
        s is { Length: >= 8 } && DateTime.TryParseExact(s[..8], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    /// <summary>Windows reports NVIDIA drivers as e.g. 32.0.15.6094 – NVIDIA calls that 560.94.</summary>
    public static string NvidiaFriendlyVersion(string windowsVersion)
    {
        var parts = windowsVersion.Split('.');
        if (parts.Length < 4) return windowsVersion;
        var digits = parts[2] + parts[3];
        if (digits.Length < 5) return windowsVersion;
        digits = digits[^5..];
        return $"{digits[..3]}.{digits[3..]}";
    }

    // ───────────── NVIDIA ─────────────

    private sealed record LookupValue(string Name, string Value, string ParentId);

    private static async Task<List<LookupValue>> NvidiaLookupAsync(int typeId, string? parentId = null)
    {
        var url = $"https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID={typeId}" +
                  (parentId != null ? $"&ParentID={parentId}" : "");
        try
        {
            var xml = XDocument.Parse(await Http.GetStringAsync(url));
            return xml.Descendants("LookupValue")
                .Select(e => new LookupValue(
                    (string?)e.Element("Name") ?? "",
                    (string?)e.Element("Value") ?? "",
                    (string?)e.Attribute("ParentID") ?? parentId ?? ""))
                .Where(v => v.Value.Length > 0)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Write($"NVIDIA lookup TypeID={typeId} ParentID={parentId}: {ex.Message}");
            return [];
        }
    }

    private static string NormaliseName(string s) =>
        Regex.Replace(s.Replace("NVIDIA", "", StringComparison.OrdinalIgnoreCase), @"\s+", " ").Trim();

    /// <summary>
    /// The names NVIDIA's product list might use for a card: Windows says "GeForce GTX 1060 6GB",
    /// NVIDIA's list may just say "GeForce GTX 1060"; laptops may or may not say "Laptop GPU" / "with Max-Q Design".
    /// </summary>
    private static List<string> NameVariants(string gpuName)
    {
        var n = NormaliseName(gpuName);
        var list = new List<string> { n };
        var noMem = Regex.Replace(n, @"\s*\d+\s*GB$", "", RegexOptions.IgnoreCase).Trim();
        list.Add(noMem);
        foreach (var b in new[] { n, noMem })
        {
            list.Add(Regex.Replace(b, @"\s*(Laptop GPU|with Max-Q Design|Max-Q)$", "", RegexOptions.IgnoreCase).Trim());
            if (!b.Contains("Laptop", StringComparison.OrdinalIgnoreCase) && IsLaptop) list.Add(b + " Laptop GPU");
        }
        return list.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Finds NVIDIA's product (pfid) and series (psid) IDs for a card name like "NVIDIA GeForce RTX 4070".</summary>
    private static async Task<(string Psid, string Pfid)?> FindNvidiaProductAsync(string gpuName)
    {
        var wanted = NameVariants(gpuName);
        LookupValue? Best(List<LookupValue> values)
        {
            // Try the most specific name first
            foreach (var w in wanted)
            {
                var m = values.FirstOrDefault(v => string.Equals(NormaliseName(v.Name), w, StringComparison.OrdinalIgnoreCase));
                if (m != null) return m;
            }
            return null;
        }

        // Fast path: the full product list
        var match = Best(await NvidiaLookupAsync(3));
        if (match != null && match.ParentId.Length > 0) return (match.ParentId, match.Value);

        // Slow path: walk product types → series → products
        foreach (var type in await NvidiaLookupAsync(1))
        {
            foreach (var series in await NvidiaLookupAsync(2, type.Value))
            {
                match = Best(await NvidiaLookupAsync(3, series.Value));
                if (match != null) return (series.Value, match.Value);
            }
        }
        return null;
    }

    /// <summary>True on laptops – NVIDIA ships different driver packages for notebooks.</summary>
    public static bool IsLaptop { get; } = DetectLaptop();

    private static bool DetectLaptop()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (ManagementObject mo in s.Get())
            {
                using (mo)
                {
                    if (mo["ChassisTypes"] is ushort[] types &&
                        types.Any(t => t is 8 or 9 or 10 or 11 or 12 or 14 or 18 or 21 or 30 or 31 or 32))
                        return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>"PCI\VEN_10DE&amp;DEV_1C03&amp;SUBSYS_…" → "1C03_10DE", the form NVIDIA's lookup expects.</summary>
    public static string? NvidiaDeviceId(string pnpId)
    {
        var m = Regex.Match(pnpId, @"VEN_([0-9A-F]{4}).*?DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase);
        return m.Success ? $"{m.Groups[2].Value.ToUpperInvariant()}_{m.Groups[1].Value.ToUpperInvariant()}" : null;
    }

    /// <summary>
    /// Asks NVIDIA for the driver that matches this exact card, by hardware ID – the same lookup the
    /// NVIDIA app uses. Works even before a driver is installed, because it doesn't need the card's name.
    /// </summary>
    private static async Task<NvidiaDriver?> FindByDeviceIdAsync(GpuInfo gpu)
    {
        var devId = NvidiaDeviceId(gpu.PnpId);
        if (devId == null) return null;

        var os = Environment.OSVersion.Version;
        var query = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["dIDa"] = new[] { devId },
            ["osC"] = $"{os.Major}.{os.Minor}",
            ["osB"] = os.Build.ToString(),
            ["is6"] = Environment.Is64BitOperatingSystem ? "1" : "0",
            ["lg"] = "1033",
            ["iLp"] = IsLaptop ? "1" : "0",
            ["prvMd"] = "0",
            ["gcV"] = "3.18.0.94",
            ["gIsB"] = "0",
            ["dch"] = "1",
            ["upCRD"] = "0",
            ["isCRD"] = "0",
        });
        const string endpoint = "https://gfwsl.geforce.com/nvidia_web_services/controller.gfeclientcontent.NG.php/com.nvidia.services.GFEClientContent_NG.getDispDrvrByDevid/";

        // The JSON goes in the URL path. Send it as-is first (like NVIDIA's own app), then percent-encoded.
        var attempts = new Func<Uri>[]
        {
            () => new Uri(endpoint + query, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true }),
            () => new Uri(endpoint + Uri.EscapeDataString(query)),
        };

        foreach (var makeUri in attempts)
        {
            try
            {
                var uri = makeUri();
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.UserAgent.Clear();
                req.Headers.UserAgent.ParseAdd("NvBackend/36.0.0.0");
                using var resp = await Http.SendAsync(req);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    Log.Write($"NVIDIA has no driver listed for device {devId}");
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                {
                    Log.Write($"NVIDIA device lookup returned {(int)resp.StatusCode}");
                    continue;
                }

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (!TryFind(doc.RootElement, "DriverAttributes", out var attrs)) continue;

                string Get(string name) => attrs.TryGetProperty(name, out var v) ? v.ToString() : "";
                var url = Get("DownloadURLAdmin");
                if (url.Length == 0) url = Get("DownloadURL");
                var version = Get("Version");
                if (url.Length == 0 || version.Length == 0) continue;

                var driver = new NvidiaDriver(version, $"GeForce driver {version}", url, 0);
                Log.Write($"NVIDIA latest for {gpu.Name} ({devId}): {driver.Version} ({driver.DownloadUrl})");
                return driver;
            }
            catch (Exception ex)
            {
                Log.Write($"NVIDIA device lookup failed: {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>Finds a property anywhere in the JSON (NVIDIA has wrapped this response differently over time).</summary>
    private static bool TryFind(JsonElement e, string name, out JsonElement found)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty(name, out found)) return true;
            foreach (var p in e.EnumerateObject())
                if (TryFind(p.Value, name, out found)) return true;
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
                if (TryFind(item, name, out found)) return true;
        }
        found = default;
        return false;
    }

    /// <summary>The latest NVIDIA driver for this card: by hardware ID first, then by name on NVIDIA's driver site.</summary>
    public static async Task<NvidiaDriver?> FindLatestNvidiaAsync(GpuInfo gpu)
    {
        var byDevice = await FindByDeviceIdAsync(gpu);
        if (byDevice != null) return byDevice;
        if (gpu.OnBasicDriver) return null; // no name to search by yet

        try
        {
            return await FindByNameAsync(gpu.Name);
        }
        catch (Exception ex)
        {
            Log.Write($"NVIDIA name lookup failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<NvidiaDriver?> FindByNameAsync(string gpuName)
    {
        var ids = await FindNvidiaProductAsync(gpuName);
        if (ids == null)
        {
            Log.Write($"NVIDIA has no product listed as \"{gpuName}\"");
            return null;
        }

        var osId = Environment.OSVersion.Version.Build >= 22000 ? 135 : 57; // Windows 11 / Windows 10, 64-bit
        var url = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
                  $"?func=DriverManualLookup&psid={ids.Value.Psid}&pfid={ids.Value.Pfid}&osID={osId}" +
                  "&languageCode=1033&beta=0&isWHQL=1&dltype=-1&dch=1&upCRD=0&qnf=0&sort1=0&numberOfResults=1";

        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        if (!doc.RootElement.TryGetProperty("IDS", out var idsArr) || idsArr.GetArrayLength() == 0) return null;

        var info = idsArr[0].GetProperty("downloadInfo");
        string Get(string name) => info.TryGetProperty(name, out var v) ? v.ToString() : "";

        var size = 0L;
        var sizeText = Get("DownloadURLFileSize");
        var m = Regex.Match(sizeText, @"([\d.]+)\s*(MB|GB)", RegexOptions.IgnoreCase);
        if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n))
            size = (long)(n * (m.Groups[2].Value.Equals("GB", StringComparison.OrdinalIgnoreCase) ? 1024d * 1024 * 1024 : 1024d * 1024));

        var driver = new NvidiaDriver(Get("Version"), Uri.UnescapeDataString(Get("Name")), Get("DownloadURL"), size);
        Log.Write($"NVIDIA latest for {gpuName} (by name): {driver.Version} ({driver.DownloadUrl})");
        return string.IsNullOrEmpty(driver.DownloadUrl) ? null : driver;
    }

    public static bool IsNewer(string latest, string installed) =>
        Version.TryParse(latest, out var a) && Version.TryParse(installed, out var b) ? a > b : !string.Equals(latest, installed);

    /// <summary>Downloads the NVIDIA driver and installs it silently (driver only, no reboot).</summary>
    public static async Task<InstallOutcome> InstallNvidiaAsync(NvidiaDriver driver, IProgress<double>? progress, Action<string> status)
    {
        var file = Path.Combine(DownloadFolder, Path.GetFileName(new Uri(driver.DownloadUrl).LocalPath));
        status($"Downloading NVIDIA {driver.Version}…");
        await DownloadAsync(driver.DownloadUrl, file, progress);

        status($"Installing NVIDIA {driver.Version} – the screen may flicker…");
        Log.Write($"Running {file} -s -noreboot -noeula");
        var psi = new ProcessStartInfo(file, "-s -noreboot -noeula") { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start the NVIDIA installer");
        await p.WaitForExitAsync();
        Log.Write($"NVIDIA installer exited with {p.ExitCode}");

        TryDelete(file);
        return p.ExitCode switch
        {
            0 => new InstallOutcome(true, true, "Installed"),
            1 or 3010 => new InstallOutcome(true, true, "Installed – restart needed"),
            _ => new InstallOutcome(false, false, $"NVIDIA installer returned code {p.ExitCode}"),
        };
    }

    // ───────────── AMD ─────────────

    /// <summary>Downloads AMD's official auto-detect installer and opens it; it picks and installs the right driver.</summary>
    public static async Task<bool> RunAmdAutoDetectAsync(IProgress<double>? progress, Action<string> status)
    {
        try
        {
            status("Finding AMD's auto-detect tool…");
            var html = await Http.GetStringAsync(AmdDriversPage);
            var m = Regex.Match(html, @"https://drivers\.amd\.com/drivers/installer/[^""'\s<>]+?minimalsetup[^""'\s<>]+?\.exe", RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                Log.Write("AMD auto-detect link not found on AMD's page");
                return false;
            }

            var file = Path.Combine(DownloadFolder, Path.GetFileName(new Uri(m.Value).LocalPath));
            status("Downloading AMD Software auto-detect…");
            await DownloadAsync(m.Value, file, progress, referer: "https://www.amd.com/");

            status("AMD's installer is open – follow it to finish.");
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"AMD auto-detect failed: {ex.Message}");
            return false;
        }
    }

    // ───────────── Intel ─────────────

    /// <summary>Downloads and opens Intel Driver &amp; Support Assistant, which finds Intel graphics, Wi-Fi and chipset drivers.</summary>
    public static async Task<bool> RunIntelAssistantAsync(IProgress<double>? progress, Action<string> status)
    {
        try
        {
            var file = Path.Combine(DownloadFolder, "Intel-Driver-and-Support-Assistant-Installer.exe");
            status("Downloading Intel Driver & Support Assistant…");
            await DownloadAsync(IntelDsaInstaller, file, progress);

            status("Intel's installer is open – follow it to finish.");
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Intel assistant download failed: {ex.Message}");
            return false;
        }
    }

    // ───────────── Helpers ─────────────

    private static async Task DownloadAsync(string url, string file, IProgress<double>? progress, string? referer = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (referer != null) req.Headers.Referrer = new Uri(referer);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using (var dst = File.Create(file))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        Log.Write($"Downloaded {Path.GetFileName(file)} ({SystemTools.FormatSize(new FileInfo(file).Length)})");
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch { }
    }
}
