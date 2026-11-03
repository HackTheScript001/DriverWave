using System.Net.Http;

namespace DriverWave.Services;

public static class Connectivity
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    /// <summary>Same check Windows itself uses to decide if it's "connected to the internet".</summary>
    public static async Task<bool> HasInternetAsync()
    {
        try
        {
            var text = await Http.GetStringAsync("http://www.msftconnecttest.com/connecttest.txt");
            return text.StartsWith("Microsoft Connect Test", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
