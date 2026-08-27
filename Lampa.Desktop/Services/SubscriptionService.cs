using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using Lampa.Desktop.Models;

namespace Lampa.Desktop.Services;

public sealed class SubscriptionService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<SubscriptionResult> DownloadAsync(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Некорректная ссылка подписки");

        Exception? lastError = null;
        foreach (var candidate in SubscriptionUrlResolver.CandidateUrls(url))
        {
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                attempt.CancelAfter(TimeSpan.FromSeconds(15));
                var result = await FetchOneAsync(candidate, attempt.Token);
                if (result is not null)
                    return result;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                lastError = new TimeoutException($"Шлюз не ответил: {candidate}");
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException("Не удалось обновить подписку");
    }

    private async Task<SubscriptionResult?> FetchOneAsync(string url, CancellationToken token, bool singBoxCompatibilityRetry = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd(singBoxCompatibilityRetry ? "LxBox-android/2.20.12" : "Lampa-Desktop-SB");
        request.Headers.TryAddWithoutValidation("X-Lampa-Client", "desktop");
        using var response = await _http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) return null;
        var body = (await response.Content.ReadAsStringAsync(token)).Trim();
        if (body.Length == 0) return null;
        if (!singBoxCompatibilityRetry && IsLegacyXrayResponse(body))
            return await FetchOneAsync(url, token, true);
        var metadata = ReadMetadata(response);
        var customProfiles = TryReadCustomConfigs(body);
        if (customProfiles.Count > 0) return new SubscriptionResult(customProfiles, metadata);
        var decoded = TryBase64(body) ?? body;
        var profiles = decoded.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)
                     || x.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)
                     || x.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)
                     || x.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
            .Select((link, index) => new ProxyProfile { Link = link, Name = GetName(link, index + 1) }).ToList();
        if (profiles.Count == 0) return null;
        return new SubscriptionResult(profiles, metadata);
    }

    private static bool IsLegacyXrayResponse(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            return node is JsonArray array && array.Count > 0 &&
                   array[0] is JsonObject first && first["routing"] is not null && first["inbounds"] is not null;
        }
        catch { return false; }
    }

    private static List<ProxyProfile> TryReadCustomConfigs(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonArray outbounds && outbounds.Any(x => x is JsonObject o && o["type"] is not null))
            {
                return
                [
                    new ProxyProfile
                    {
                        Name = "Автовыбор",
                        ConfigJson = outbounds.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                    }
                ];
            }
            var configs = node is JsonObject obj ? new List<JsonNode?> { obj } : [];
            return configs.Select((item, index) => {
                var config = item!.AsObject();
                if (config["inbounds"] is null || config["outbounds"] is null || config["routing"] is null) return null;
                return new ProxyProfile {
                    Name = config["remarks"]?.GetValue<string>() ?? $"Автоконфиг {index + 1}",
                    ConfigJson = config.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                };
            }).Where(x => x is not null).Cast<ProxyProfile>().ToList();
        }
        catch { return []; }
    }

    private static SubscriptionMetadata ReadMetadata(HttpResponseMessage response)
    {
        string Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() ?? "" : "";
        var title = Header("Profile-Title");
        if (title.StartsWith("base64:", StringComparison.OrdinalIgnoreCase)) title = TryBase64(title[7..]) ?? title;
        var profileRouting = RoutingBundle.Resolve(Header("Profile-Routing"), Header("Routing"));
        var metadata = new SubscriptionMetadata { Title = title, ProfileRouting = profileRouting };
        ReadRoutingAssetUrls(metadata);
        if (int.TryParse(Header("Profile-Update-Interval"), out var hours)) metadata.UpdateHours = hours;
        foreach (var part in Header("Subscription-Userinfo").Split(';', StringSplitOptions.RemoveEmptyEntries)) {
            var pair = part.Split('=', 2); if (pair.Length != 2 || !long.TryParse(pair[1].Trim(), out var value)) continue;
            switch (pair[0].Trim().ToLowerInvariant()) { case "upload": metadata.Upload = value; break; case "download": metadata.Download = value; break; case "total": metadata.Total = value; break; case "expire": metadata.Expire = value; break; }
        }
        return metadata;
    }

    private static void ReadRoutingAssetUrls(SubscriptionMetadata metadata)
    {
        try {
            var value = metadata.ProfileRouting;
            if (value.StartsWith("base64:", StringComparison.OrdinalIgnoreCase)) value = TryBase64(value[7..]) ?? "";
            var root = JsonNode.Parse(value)?.AsObject();
            metadata.GeoIpUrl = root?["geoipUrl"]?.GetValue<string>() ?? "";
            metadata.GeoSiteUrl = root?["geositeUrl"]?.GetValue<string>() ?? "";
        } catch { }
    }


    private static string? TryBase64(string value)
    {
        try
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized += new string('=', (4 - normalized.Length % 4) % 4);
            return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
        }
        catch { return null; }
    }

    private static string GetName(string link, int fallback)
    {
        try
        {
            var hash = link.LastIndexOf('#');
            if (hash >= 0) return Uri.UnescapeDataString(link[(hash + 1)..]);
        }
        catch { }
        return $"Сервер {fallback}";
    }
}

public sealed record SubscriptionResult(List<ProxyProfile> Profiles, SubscriptionMetadata Metadata);
public sealed class SubscriptionMetadata
{
    public string Title { get; set; } = "";
    public long Upload { get; set; }
    public long Download { get; set; }
    public long Total { get; set; }
    public long Expire { get; set; }
    public int UpdateHours { get; set; } = 12;
    public string ProfileRouting { get; set; } = "";
    public string GeoIpUrl { get; set; } = "";
    public string GeoSiteUrl { get; set; } = "";
}
