using System.Text.Json;
using System.IO;

namespace Lampa.Desktop.Models;

public sealed class AppSettings
{
    public string SubscriptionUrl { get; set; } = "";
    public string CorePath { get; set; } = "core\\sing-box.exe";
    public bool DesiredConnected { get; set; }
    public bool StartWithWindows { get; set; } = true;
    public bool AutoReconnect { get; set; } = true;
    public bool PauseVpnOnSleep { get; set; } = true;
    public bool UseTun { get; set; } = true;
    public int LocalHttpPort { get; set; } = 10809;
    // Пользовательские доменные правила для маршрутизации.
    // Добавляются поверх базового routing (выше catch-all).
    public List<string> CustomProxyDomains { get; set; } = [];
    public List<string> CustomDirectDomains { get; set; } = [];
    public DateTimeOffset? LastSubscriptionUpdate { get; set; }
    public string SubscriptionTitle { get; set; } = "";
    public long SubscriptionUpload { get; set; }
    public long SubscriptionDownload { get; set; }
    public long SubscriptionTotal { get; set; }
    public long SubscriptionExpire { get; set; }
    public int SubscriptionUpdateHours { get; set; } = 24;
    /// <summary>Increment when the worker subscription format or required User-Agent changes.</summary>
    public int SubscriptionSchemaVersion { get; set; }
    public DateTimeOffset? LastGeoUpdate { get; set; }
    public int GeoUpdateDays { get; set; } = 3;
    public string ProfileRouting { get; set; } = "";
    public string GeoIpUrl { get; set; } = "";
    public string GeoSiteUrl { get; set; } = "";
    public List<string> BypassApplications { get; set; } = [];
    public int ActivePriority { get; set; }
    public List<ProxyProfile> Profiles { get; set; } = [];
    public int SelectedProfile { get; set; }
    public bool UseFullBlockList { get; set; } = true;
    /// <summary>
    /// true = всё в прокси кроме geoip:ru / сайтов только из РФ;
    /// false = в прокси только ru-blocked-all (старый split).
    /// </summary>
    public bool RouteExceptRussia { get; set; } = true;
    public int AppUpdateDays { get; set; } = 7;
    public DateTimeOffset? LastAppUpdateCheck { get; set; }
    public string PendingUpdateVersion { get; set; } = "";
    public string PendingUpdateUrl { get; set; } = "";
    public long PendingUpdateSize { get; set; }
    public string PendingUpdateSha256 { get; set; } = "";
    public string PendingUpdateStatus { get; set; } = "";

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lampa");
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var text = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(text) ?? new();
            if (settings.SubscriptionUpdateHours is < 6 or > 72) settings.SubscriptionUpdateHours = 24;
            if (settings.GeoUpdateDays is < 1 or > 7) settings.GeoUpdateDays = 3;
            if (settings.AppUpdateDays is < 3 or > 30) settings.AppUpdateDays = 7;
            // Режим маршрутизации больше не выбирается в интерфейсе.
            settings.UseFullBlockList = true;
            // Старые settings без ключа → новый режим по умолчанию.
            if (!text.Contains("\"RouteExceptRussia\"", StringComparison.Ordinal))
                settings.RouteExceptRussia = true;
            if (settings.CorePath.EndsWith("xray.exe", StringComparison.OrdinalIgnoreCase))
                settings.CorePath = "core\\sing-box.exe";
            return settings;
        }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed class ProxyProfile
{
    public string Name { get; set; } = "Сервер";
    public string Link { get; set; } = "";
    /// <summary>Original managed /auto/ configuration. It must stay intact: it contains balancers and routing.</summary>
    public string ConfigJson { get; set; } = "";
}
