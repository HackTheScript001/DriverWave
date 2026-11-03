using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DriverWave.Services;

public sealed record AppUpdate(Version Version, string DownloadUrl, string FileName);

/// <summary>Checks GitHub Releases for a newer DriverWave and installs it silently from inside the app.</summary>
public static class Updater
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DriverWave", AppInfo.VersionText));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static async Task<AppUpdate?> CheckAsync()
    {
        try
        {
            var url = $"https://api.github.com/repos/{AppInfo.GitHubOwner}/{AppInfo.GitHubRepo}/releases/latest";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;

            var current = new Version(AppInfo.Version.Major, AppInfo.Version.Minor, Math.Max(0, AppInfo.Version.Build));
            var latest3 = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
            if (latest3 <= current) return null;

            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase))
                    return new AppUpdate(latest3, asset.GetProperty("browser_download_url").GetString()!, name);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Update check failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>Downloads the new installer, runs it silently and closes this copy. The installer relaunches the app.</summary>
    public static async Task DownloadAndInstallAsync(AppUpdate update, IProgress<double>? progress = null)
    {
        var path = Path.Combine(Path.GetTempPath(), update.FileName);
        Log.Write($"Downloading DriverWave {update.Version}");

        using (var resp = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        Log.Write("Running installer and closing");
        Process.Start(new ProcessStartInfo(path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS")
        {
            UseShellExecute = true,
        });
        System.Windows.Application.Current.Shutdown();
    }
}
