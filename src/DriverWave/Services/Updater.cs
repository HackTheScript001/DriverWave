using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DriverWave.Services;

public sealed class UpdateException(string message) : Exception(message);

public sealed record AppRelease(Version Version, string Url, string Notes, string Sha256, long Size, string Page)
{
    public string VersionText => Version.ToString(3);
    public bool CanInstall => Url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Sha256.Length == 64;
}

/// <summary>
/// In-app updates from GitHub Releases, the same way Hatchling does it:
/// found a few seconds after opening, downloaded in the background straight away (resuming if the
/// connection drops) and checked against the SHA-256 GitHub publishes. Installing runs the setup with
/// only its progress window, then DriverWave reopens on the new version. If DriverWave is closed
/// first, a downloaded update installs quietly on close.
/// </summary>
public static class Updater
{
    private static readonly string[] GitHubHosts =
    [
        "https://github.com/", "https://objects.githubusercontent.com/", "https://release-assets.githubusercontent.com/",
    ];
    private const long MaxInstaller = 400L * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DriverWave", AppInfo.VersionText));
        return c;
    }

    public static Version Current => new(AppInfo.Version.Major, AppInfo.Version.Minor, Math.Max(0, AppInfo.Version.Build));

    public static Version ParseVersion(string s)
    {
        var nums = Regex.Matches(s ?? "", @"\d+").Select(m => int.Parse(m.Value)).Take(3).ToList();
        while (nums.Count < 3) nums.Add(0);
        return new Version(nums[0], nums[1], nums[2]);
    }

    // ───────────── Checking ─────────────

    /// <summary>The newer release, or null when up to date. Throws on network errors.</summary>
    public static async Task<AppRelease?> CheckAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{AppInfo.GitHubOwner}/{AppInfo.GitHubRepo}/releases/latest");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var resp = await Http.SendAsync(req, cts.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null; // no releases yet
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
        var root = doc.RootElement;
        var version = ParseVersion(root.GetProperty("tag_name").GetString() ?? "");
        if (version <= Current) return null;

        var notes = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "").Trim() : "";
        if (notes.Length > 8000) notes = notes[..8000];
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";

        var assets = root.GetProperty("assets").EnumerateArray().ToList();
        var exe = assets.FirstOrDefault(a => (a.GetProperty("name").GetString() ?? "").EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase));
        if (exe.ValueKind != JsonValueKind.Object)
            return new AppRelease(version, page, notes, "", 0, page);

        var name = exe.GetProperty("name").GetString()!;
        var url = exe.GetProperty("browser_download_url").GetString() ?? "";
        var size = exe.TryGetProperty("size", out var s) ? s.GetInt64() : 0;

        // GitHub's own digest first, then the .sha256 file published next to the installer
        var sha = "";
        if (exe.TryGetProperty("digest", out var d) && (d.GetString() ?? "").StartsWith("sha256:"))
            sha = d.GetString()![7..].ToLowerInvariant();
        if (sha.Length != 64)
        {
            var side = assets.FirstOrDefault(a => string.Equals(a.GetProperty("name").GetString(), name + ".sha256", StringComparison.OrdinalIgnoreCase));
            if (side.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    var text = await Http.GetStringAsync(side.GetProperty("browser_download_url").GetString(), cts.Token);
                    sha = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
                }
                catch { sha = ""; }
            }
        }
        if (!Regex.IsMatch(sha, "^[0-9a-f]{64}$")) sha = "";

        return new AppRelease(version, url, notes, sha, size, page);
    }

    // ───────────── Release notes ─────────────

    private static readonly Regex Heading = new(@"^(?:what's new in|what was new in|new in)\s+v?(\d+(?:\.\d+)*)", RegexOptions.IgnoreCase);
    private static readonly string[] Boilerplate = ["download **driverwave", "download `driverwave", "windows will ask for admin", "problems or ideas", "is in beta"];

    /// <summary>The notes for every version after the one running, newest first, without download instructions.</summary>
    public static string WhatsNew(string notes, Version version)
    {
        var lines = (notes ?? "").Replace("\r\n", "\n").Split('\n');
        var sections = new List<(Version V, List<string> Body)>();
        List<string>? cur = null;

        foreach (var l in lines)
        {
            var m = Heading.Match(l.Trim());
            if (m.Success)
            {
                cur = [];
                sections.Add((ParseVersion(m.Groups[1].Value), cur));
            }
            else if (cur != null)
            {
                if (l.Trim().StartsWith("how to install", StringComparison.OrdinalIgnoreCase) ||
                    Boilerplate.Any(x => l.Contains(x, StringComparison.OrdinalIgnoreCase)))
                {
                    cur = null;
                    continue;
                }
                cur.Add(l);
            }
        }

        var wanted = sections
            .Where(s => s.V > Current && s.V <= version)
            .Select(s => (s.V, Text: string.Join("\n", s.Body).Trim()))
            .Where(s => s.Text.Length > 0)
            .ToList();

        if (wanted.Count == 1) return Tidy(wanted[0].Text);
        if (wanted.Count > 1) return Tidy(string.Join("\n\n", wanted.Select(w => $"Version {w.V.ToString(3)}\n{w.Text}")));
        return Tidy(string.Join("\n", lines.Where(l => !Boilerplate.Any(x => l.Contains(x, StringComparison.OrdinalIgnoreCase)))).Trim());
    }

    /// <summary>The little Markdown the notes use, as plain text: bullets become •, bold markers dropped.</summary>
    private static string Tidy(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var l = raw.TrimEnd().Replace("**", "").Replace("`", "");
            var t = l.TrimStart();
            sb.AppendLine(t.StartsWith("- ") || t.StartsWith("* ") ? "•  " + t[2..] : l);
        }
        return sb.ToString().Trim();
    }

    // ───────────── Downloading ─────────────

    public static string DownloadDir => Path.Combine(Path.GetTempPath(), "DriverWave-update");

    public static string DownloadedPath(AppRelease rel) => Path.Combine(DownloadDir, $"DriverWave-Setup-{rel.VersionText}.exe");

    private static string Sha256Of(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }

    /// <summary>
    /// Downloads the installer and checks it. A dropped connection carries on from where it stopped
    /// (up to 5 times); a file that doesn't match its checksum is downloaded once more from scratch.
    /// Returns the installer's path.
    /// </summary>
    public static async Task<string> DownloadAsync(AppRelease rel, IProgress<(long Done, long Total)>? progress, CancellationToken ct = default)
    {
        if (!rel.Url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new UpdateException("this release has no installer to download");
        if (!GitHubHosts.Any(h => rel.Url.StartsWith(h, StringComparison.OrdinalIgnoreCase))) throw new UpdateException("the installer isn't on GitHub, so it wasn't downloaded");
        if (rel.Sha256.Length != 64) throw new UpdateException("the release has no checksum, so it can't be checked");

        var path = DownloadedPath(rel);
        if (File.Exists(path) && await Task.Run(() => Sha256Of(path), ct) == rel.Sha256) return path; // already downloaded

        Directory.CreateDirectory(DownloadDir);
        var part = path + ".part";
        if (File.Exists(part)) File.Delete(part);
        var last = "";

        for (var fresh = 0; fresh < 2; fresh++)
        {
            var complete = false;
            for (var attempt = 0; attempt < 6 && !complete; attempt++)
            {
                var have = File.Exists(part) ? new FileInfo(part).Length : 0;
                try
                {
                    complete = await FetchAsync(rel, part, have, progress, ct);
                    if (!complete) last = "the connection dropped part way";
                }
                catch (UpdateException ex) { last = ex.Message; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { last = $"download interrupted ({ex.Message})"; }

                if (!complete && attempt < 5) await Task.Delay(1500 * (attempt + 1), ct);
            }

            if (!complete) throw new UpdateException($"download failed – {last}. Check the internet connection and try again");

            if (await Task.Run(() => Sha256Of(part), ct) == rel.Sha256)
            {
                File.Move(part, path, overwrite: true);
                return path;
            }
            File.Delete(part);
            last = "the download didn't match its checksum";
        }
        throw new UpdateException("the download didn't match its checksum twice, so it was deleted – try again later");
    }

    private static async Task<bool> FetchAsync(AppRelease rel, string part, long have, IProgress<(long, long)>? progress, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, rel.Url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        resp.EnsureSuccessStatusCode();

        if (have > 0 && resp.StatusCode != HttpStatusCode.PartialContent) have = 0; // server ignored the range: start again
        var length = resp.Content.Headers.ContentLength ?? 0;
        var total = length > 0 ? have + length : rel.Size;
        if (total > MaxInstaller) throw new UpdateException("the download is unexpectedly large");

        var done = have;
        await using (var src = await resp.Content.ReadAsStreamAsync(timeout.Token))
        await using (var dst = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write))
        {
            var buffer = new byte[256 * 1024];
            int read;
            while ((read = await src.ReadAsync(buffer, timeout.Token)) > 0)
            {
                done += read;
                if (done > MaxInstaller) throw new UpdateException("the download is unexpectedly large");
                await dst.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                progress?.Report((done, total));
            }
        }

        var expected = rel.Size > 0 ? rel.Size : total;
        return expected <= 0 || new FileInfo(part).Length >= expected;
    }

    // ───────────── Installing ─────────────

    /// <summary>
    /// Starts the installer and returns straight away – the caller should then close DriverWave so its
    /// files can be replaced. With showProgress only the installer's progress window appears (no questions).
    /// The installer reopens DriverWave afterwards when relaunch is true.
    /// </summary>
    public static void Install(string path, bool relaunch, bool showProgress)
    {
        Directory.CreateDirectory(DownloadDir);
        var log = Path.Combine(DownloadDir, "install-log.txt");
        var args = $"{(showProgress ? "/SILENT" : "/VERYSILENT")} /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /SP- " +
                   $"/RELAUNCH={(relaunch ? 1 : 0)} /LOG=\"{log}\"";
        try
        {
            // DriverWave already runs as admin, so the installer starts without another prompt
            Process.Start(new ProcessStartInfo(path, args) { UseShellExecute = true });
            Log.Write($"Installing update {Path.GetFileName(path)}{(relaunch ? " – DriverWave will reopen when it's done" : " on close")}");
        }
        catch (Exception ex)
        {
            throw new UpdateException($"the installer couldn't start ({ex.Message})");
        }
    }

    /// <summary>Removes earlier downloads once DriverWave is running the newer version.</summary>
    public static void CleanOld(string keep = "")
    {
        try
        {
            if (!Directory.Exists(DownloadDir)) return;
            foreach (var f in Directory.EnumerateFiles(DownloadDir))
            {
                if ((f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) &&
                    !string.Equals(f, keep, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }
}
