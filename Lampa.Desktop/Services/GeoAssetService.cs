using System.IO;
using System.Net.Http;

namespace Lampa.Desktop.Services;

public static class GeoAssetService
{
    // Same primary databases as Android (runetfreedom/russia-v2ray-rules-dat).
    public const string FullGeoSiteUrl =
        "https://raw.githubusercontent.com/runetfreedom/russia-v2ray-rules-dat/release/geosite.dat";
    public const string FullGeoIpUrl =
        "https://raw.githubusercontent.com/runetfreedom/russia-v2ray-rules-dat/release/geoip.dat";

    public const long MinGeoSiteBytes = 1_000_000;
    public const long MinGeoIpBytes = 18_000_000;

    public static string AssetDirectory => Path.Combine(Lampa.Desktop.Models.AppSettings.DataDirectory, "assets");

    public static bool IsPrimaryGeoSiteValid(string path) =>
        File.Exists(path) && new FileInfo(path).Length >= MinGeoSiteBytes;

    public static bool IsPrimaryGeoIpValid(string path) =>
        File.Exists(path) && new FileInfo(path).Length >= MinGeoIpBytes;

    public static async Task EnsurePrimaryAssetsAsync(CancellationToken token)
    {
        var directory = AssetDirectory;
        Directory.CreateDirectory(directory);
        EnsureCompatAssets(directory);
        CopyBundledPrimary(directory);

        var geoSitePath = Path.Combine(directory, "geosite.dat");
        var geoIpPath = Path.Combine(directory, "geoip.dat");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        if (!IsPrimaryGeoSiteValid(geoSitePath) || new FileInfo(geoSitePath).Length < 20_000_000)
            await DownloadAsync(http, FullGeoSiteUrl, geoSitePath, MinGeoSiteBytes, token);
        if (!IsPrimaryGeoIpValid(geoIpPath) || new FileInfo(geoIpPath).Length < MinGeoIpBytes)
            await DownloadAsync(http, FullGeoIpUrl, geoIpPath, MinGeoIpBytes, token);

        if (!IsPrimaryGeoSiteValid(geoSitePath) || !IsPrimaryGeoIpValid(geoIpPath))
            throw new InvalidOperationException("Не удалось загрузить полные geo-базы маршрутизации");
    }

    public static async Task RefreshAsync(CancellationToken token)
    {
        var directory = AssetDirectory;
        Directory.CreateDirectory(directory);
        EnsureCompatAssets(directory);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        await DownloadAsync(http, FullGeoSiteUrl, Path.Combine(directory, "geosite.dat"), MinGeoSiteBytes, token);
        await DownloadAsync(http, FullGeoIpUrl, Path.Combine(directory, "geoip.dat"), MinGeoIpBytes, token);
    }

    public static DateTimeOffset? InferLastUpdate()
    {
        var path = Path.Combine(AssetDirectory, "geosite.dat");
        if (!File.Exists(path)) return null;
        return new FileInfo(path).LastWriteTimeUtc;
    }

    public static void CopyBundledPrimary(string assetDirectory)
    {
        var bundledCore = Path.Combine(AppContext.BaseDirectory, "core");
        foreach (var file in new[] { "geoip.dat", "geosite.dat" })
        {
            var destination = Path.Combine(assetDirectory, file);
            var minBytes = file == "geosite.dat" ? MinGeoSiteBytes : MinGeoIpBytes;
            if (File.Exists(destination) && new FileInfo(destination).Length >= minBytes)
                continue;
            var source = Path.Combine(bundledCore, file);
            if (File.Exists(source) && new FileInfo(source).Length >= minBytes)
                File.Copy(source, destination, true);
        }
    }

    public static void EnsureCompatAssets(string assetDirectory)
    {
        var bundledCore = Path.Combine(AppContext.BaseDirectory, "core");
        foreach (var file in new[] { "geosite-compat.dat", "geoip-compat.dat" })
        {
            var source = Path.Combine(bundledCore, file);
            var destination = Path.Combine(assetDirectory, file);
            if (!File.Exists(source)) continue;
            if (!File.Exists(destination) || new FileInfo(destination).Length < 1024)
                File.Copy(source, destination, true);
        }
    }

    private static async Task DownloadAsync(HttpClient http, string url, string destination, long minBytes, CancellationToken token)
    {
        var temp = destination + ".download";
        using var response = (await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)).EnsureSuccessStatusCode();
        await using (var source = await response.Content.ReadAsStreamAsync(token))
        await using (var target = File.Create(temp))
            await source.CopyToAsync(target, token);

        var size = new FileInfo(temp).Length;
        if (size < minBytes)
        {
            File.Delete(temp);
            throw new InvalidOperationException($"Geo-база слишком мала ({size} bytes): {Path.GetFileName(destination)}");
        }

        File.Move(temp, destination, true);
    }
}
