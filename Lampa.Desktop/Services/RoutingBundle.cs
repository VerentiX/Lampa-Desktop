using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Lampa.Desktop.Services;

public static class RoutingBundle
{
    public static string Bundled()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "routing");
        var defaultProfile = ReadJson(Path.Combine(directory, "default.json"));
        var exceptRuProfile = ReadJson(Path.Combine(directory, "except-ru.json"));
        var whitelistProfile = ReadJson(Path.Combine(directory, "whitelist.json"));
        if (defaultProfile is null || whitelistProfile is null)
            throw new InvalidOperationException("Не найдены встроенные профили маршрутизации. Переустановите Lampa VPN.");
        exceptRuProfile ??= defaultProfile.DeepClone()!.AsObject();
        return new JsonObject
        {
            ["whitelistMinPriority"] = 5,
            ["default"] = defaultProfile,
            ["exceptRu"] = exceptRuProfile,
            ["full"] = exceptRuProfile.DeepClone(),
            ["whitelist"] = whitelistProfile
        }.ToJsonString();
    }

    public static string Resolve(string? profileRouting, string? happRouting = null)
    {
        if (LooksLikeBundle(profileRouting)) return profileRouting!;
        if (LooksLikeBundle(DecodeHappRouting(happRouting))) return DecodeHappRouting(happRouting)!;
        return Bundled();
    }

    public static string RefreshFromBundled(string? storedRouting)
    {
        if (!LooksLikeBundle(storedRouting)) return Bundled();
        try
        {
            var stored = JsonNode.Parse(storedRouting!)!.AsObject();
            var fresh = JsonNode.Parse(Bundled())!.AsObject();
            if (fresh["default"] is JsonObject d) stored["default"] = d.DeepClone();
            if (fresh["exceptRu"] is JsonObject e) stored["exceptRu"] = e.DeepClone();
            if (fresh["whitelist"] is JsonObject w) stored["whitelist"] = w.DeepClone();
            if (fresh["full"] is JsonObject f) stored["full"] = f.DeepClone();
            if (fresh["whitelistMinPriority"] is JsonNode p) stored["whitelistMinPriority"] = p.DeepClone();
            return stored.ToJsonString();
        }
        catch { return Bundled(); }
    }

    public static bool LooksLikeBundle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            var json = value;
            if (json.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
            {
                var raw = json[7..].Replace('-', '+').Replace('_', '/');
                raw += new string('=', (4 - raw.Length % 4) % 4);
                json = Encoding.UTF8.GetString(Convert.FromBase64String(raw));
            }
            var root = JsonNode.Parse(json) as JsonObject;
            return root?["default"] is JsonObject || root?["whitelist"] is JsonObject;
        }
        catch { return false; }
    }

    public static string? DecodeHappRouting(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        const string prefix = "happ://routing/onadd/";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var raw = value[prefix.Length..].Replace('-', '+').Replace('_', '/');
            raw += new string('=', (4 - raw.Length % 4) % 4);
            var profile = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(raw))) as JsonObject;
            if (profile is null) return null;
            var bundled = JsonNode.Parse(Bundled())!.AsObject();
            var name = profile["Name"]?.GetValue<string>() ?? "";
            if (name.Contains("whitelist", StringComparison.OrdinalIgnoreCase))
                bundled["whitelist"] = profile;
            else
            {
                bundled["default"] = profile;
                bundled["full"] = profile.DeepClone();
            }
            return bundled.ToJsonString();
        }
        catch { return null; }
    }

    private static JsonObject? ReadJson(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; }
        catch { return null; }
    }
}
