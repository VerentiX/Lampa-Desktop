using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lampa.Desktop.Models;

namespace Lampa.Desktop.Services;

public static class CoreConfigBuilder
{
    // Roscom-only lists that live in the small companion databases.
    private static readonly HashSet<string> GeositeCompatCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "whitelist", "category-geoblock-ru", "apple", "google-play", "google-deepmind",
        "microsoft", "github", "telegram", "youtube", "twitch", "twitch-ads", "pinterest", "steam",
        "epic-games", "epicgames", "riot", "escapefromtarkov", "faceit", "category-ads", "win-spy",
        "private", "torrent"
    };

    private static readonly HashSet<string> GeoipCompatCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "direct", "whitelist", "private"
    };

    private static readonly HashSet<string> UnsupportedGeositeRules = new(StringComparer.OrdinalIgnoreCase);

    public static string Build(ProxyProfile profile, int httpPort, bool useTun = true,
        string profileRouting = "", IReadOnlyCollection<string>? bypassApplications = null, int activePriority = 0,
        IReadOnlyCollection<string>? customProxyDomains = null, IReadOnlyCollection<string>? customDirectDomains = null,
        bool useFullBlockList = true, bool routeExceptRussia = false)
    {
        if (!string.IsNullOrWhiteSpace(profile.ConfigJson))
            return PrepareManagedConfig(profile.ConfigJson, httpPort, useTun, profileRouting, bypassApplications, activePriority, customProxyDomains, customDirectDomains, useFullBlockList, routeExceptRussia);
        var link = profile.Link;
        object outbound = link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)
            ? BuildVmess(link) : BuildUriProfile(link);
        var config = new
        {
            log = new { loglevel = "warning" },
            inbounds = new object[] {
                new { tag = "http-in", listen = "127.0.0.1", port = httpPort, protocol = "http", settings = new { } },
                new { tag = "socks-in", listen = "127.0.0.1", port = httpPort + 1, protocol = "socks", settings = new { udp = true } }
            },
            outbounds = new object[] { outbound, new { tag = "direct", protocol = "freedom", settings = new { } } }
        };
        var json = JsonSerializer.Serialize(config);
        return useTun ? JsonSerializer.Serialize(AddTunInbound(JsonNode.Parse(json)!.AsObject())) : json;
    }

    private static string PrepareManagedConfig(string json, int httpPort, bool useTun,
        string profileRouting, IReadOnlyCollection<string>? bypassApplications, int activePriority,
        IReadOnlyCollection<string>? customProxyDomains, IReadOnlyCollection<string>? customDirectDomains,
        bool useFullBlockList, bool routeExceptRussia)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("Повреждён автоконфиг подписки");
        if (root["log"] is JsonObject log) log["loglevel"] = "info";
        if (root["inbounds"] is JsonArray inbounds)
        {
            foreach (var inbound in inbounds.OfType<JsonObject>())
            {
                var protocol = inbound["protocol"]?.GetValue<string>();
                if (protocol == "tun") continue;
                inbound["listen"] = "127.0.0.1";
                if (protocol == "http") inbound["port"] = httpPort;
                else if (protocol is "mixed" or "socks") inbound["port"] = httpPort + 1;
            }
            DeduplicateLocalInbounds(inbounds);
        }

        var bundle = DecodeRoutingBundle(profileRouting);
        var profile = SelectRoutingProfile(bundle, activePriority, routeExceptRussia);

        ApplyRouting(root, profileRouting, bypassApplications ?? [], activePriority, customProxyDomains, customDirectDomains, useFullBlockList, routeExceptRussia);
        ApplyDesktopDns(root, profile);
        RewriteGeoCompatibility(root);
        RemoveFakeDnsSniffing(root);
        EnsureMetrics(root);
        TweakBurstObservatory(root);

        if (useTun) AddTunInbound(root);
        return JsonSerializer.Serialize(root);
    }

    private static void DeduplicateLocalInbounds(JsonArray inbounds)
    {
        JsonObject? keepMixed = null;
        JsonObject? keepHttp = null;
        List<JsonNode> remove = [];
        foreach (var inbound in inbounds.OfType<JsonObject>())
        {
            var protocol = inbound["protocol"]?.GetValue<string>();
            var tag = inbound["tag"]?.GetValue<string>() ?? "";
            if (protocol is "mixed" or "socks")
            {
                if (keepMixed is null || tag.Equals("auto-proxy-in", StringComparison.OrdinalIgnoreCase))
                {
                    if (keepMixed is not null && keepMixed != inbound) remove.Add(keepMixed);
                    keepMixed = inbound;
                }
                else remove.Add(inbound);
            }
            else if (protocol == "http")
            {
                if (keepHttp is null) keepHttp = inbound;
                else remove.Add(inbound);
            }
        }
        foreach (var node in remove) inbounds.Remove(node);
    }

    private static void ApplyRouting(JsonObject root, string encodedProfiles,
        IReadOnlyCollection<string> bypassApplications, int activePriority,
        IReadOnlyCollection<string>? customProxyDomains, IReadOnlyCollection<string>? customDirectDomains,
        bool useFullBlockList, bool routeExceptRussia)
    {
        var routing = root["routing"] as JsonObject;
        if (routing is null) return;
        var rules = routing["rules"] as JsonArray ?? new JsonArray();
        var preserved = rules.OfType<JsonObject>()
            .Where(x => x["inboundTag"] is not null || x["balancerTag"] is not null)
            .Select(x => x.DeepClone()).ToList();
        var next = new JsonArray();
        next.Add(new JsonObject
        {
            ["type"] = "field",
            ["inboundTag"] = new JsonArray("dns-in"),
            ["outboundTag"] = "direct"
        });
        next.Add(new JsonObject
        {
            ["type"] = "field",
            ["port"] = "53",
            ["network"] = "udp,tcp",
            ["outboundTag"] = "dns-out"
        });
        foreach (var rule in preserved) next.Add(rule);

        // Пользовательские домены — первыми и с domain:, чтобы ловить www/api и прочие поддомены.
        AddRule(next, "domain", ToDomainMatchers(customProxyDomains), "proxy");
        AddRule(next, "domain", ToDomainMatchers(customDirectDomains), "direct");

        if (bypassApplications.Count > 0) next.Add(new JsonObject {
            ["type"] = "field", ["process"] = new JsonArray(bypassApplications.Select(x => (JsonNode?)x.Replace('\\', '/')).ToArray()),
            ["outboundTag"] = "direct"
        });

        next.Add(new JsonObject {
            ["type"] = "field",
            ["protocol"] = new JsonArray("bittorrent"),
            ["outboundTag"] = "direct"
        });

        var bundle = DecodeRoutingBundle(encodedProfiles);
        var profile = SelectRoutingProfile(bundle, activePriority, routeExceptRussia);
        if (profile is not null)
        {
            // Domain rules are checked first; if none match, resolve the
            // destination so the profile's IP rules can still select the
            // correct outbound.  Keeping AsIs here silently bypassed those IP
            // lists for domain-based connections.
            routing["domainStrategy"] = "IPIfNonMatch";
            foreach (var step in ReadStrings(profile, "routeOrder", "RouteOrder").DefaultIfEmpty("block-proxy-direct")
                         .SelectMany(x => x.Split('-', StringSplitOptions.RemoveEmptyEntries)))
            {
                var prefix = step.Trim().ToLowerInvariant();
                var tag = prefix switch { "block" => "block", "proxy" => "proxy", "direct" => "direct", _ => "" };
                if (tag.Length == 0) continue;
                var domains = ReadStrings(profile, prefix + "Sites", char.ToUpperInvariant(prefix[0]) + prefix[1..] + "Sites");
                if (tag == "proxy")
                    domains = ApplyBlockListMode(domains, useFullBlockList);
                AddRule(next, "domain", domains, tag);
                var ipValues = ReadStrings(profile, prefix + "Ip", char.ToUpperInvariant(prefix[0]) + prefix[1..] + "Ip");
                AddRule(next, "ip", ipValues, tag);
                if (prefix == "proxy")
                {
                    next.Add(new JsonObject {
                        ["type"] = "field",
                        ["port"] = "50000-65535",
                        ["network"] = "udp",
                        ["outboundTag"] = "proxy"
                    });
                }
            }
            ApplyDnsHosts(root, profile);
        }
        // GlobalProxy=true → всё остальное в proxy (режим «всё кроме РФ»).
        // Иначе catch-all direct (split по ru-blocked).
        var globalProxy = profile is not null && IsTruthy(profile["GlobalProxy"] ?? profile["globalProxy"]);
        next.Add(new JsonObject
        {
            ["type"] = "field",
            ["network"] = "tcp,udp",
            ["outboundTag"] = globalProxy ? "proxy" : "direct"
        });
        routing["rules"] = next;
    }

    private static JsonObject? SelectRoutingProfile(JsonObject? bundle, int activePriority, bool routeExceptRussia)
    {
        if (bundle is null) return null;
        var threshold = bundle["whitelistMinPriority"]?.GetValue<int>() ?? 5;
        if (activePriority >= threshold)
            return bundle["whitelist"] as JsonObject ?? bundle["default"] as JsonObject;
        if (routeExceptRussia)
            return bundle["exceptRu"] as JsonObject ?? bundle["full"] as JsonObject ?? bundle["default"] as JsonObject;
        return bundle["default"] as JsonObject;
    }

    private static bool IsTruthy(JsonNode? node)
    {
        if (node is null) return false;
        try
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue<bool>(out var b)) return b;
                if (value.TryGetValue<string>(out var s))
                    return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
            }
        }
        catch { }
        return false;
    }

    private static void ApplyDesktopDns(JsonObject root, JsonObject? profile)
    {
        var dns = root["dns"] as JsonObject ?? new JsonObject();
        root["dns"] = dns;
        dns["queryStrategy"] = "UseIPv4";
        dns["tag"] = "dns-in";
        dns["servers"] = new JsonArray { "https+local://1.1.1.1/dns-query" };
        ApplyDnsHosts(root, profile);

        var outbounds = root["outbounds"] as JsonArray ?? new JsonArray();
        root["outbounds"] = outbounds;
        for (var i = outbounds.Count - 1; i >= 0; i--)
        {
            if (outbounds[i] is JsonObject outbound && outbound["tag"]?.GetValue<string>() == "dns-out")
                outbounds.RemoveAt(i);
        }
        outbounds.Add(new JsonObject
        {
            ["tag"] = "dns-out",
            ["protocol"] = "dns"
        });
    }

    private static void ApplyDnsHosts(JsonObject root, JsonObject? profile)
    {
        if (profile is null) return;
        var hosts = profile["dnsHosts"] as JsonObject ?? profile["DnsHosts"] as JsonObject;
        if (hosts is null) return;
        var dns = root["dns"] as JsonObject ?? new JsonObject();
        root["dns"] = dns;
        dns["hosts"] = hosts.DeepClone();
    }

    private static void RewriteGeoCompatibility(JsonObject root)
    {
        if (root["routing"]?["rules"] is JsonArray rules)
        {
            foreach (var rule in rules.OfType<JsonObject>())
            {
                RewriteGeoArray(rule, "domain", "geosite:", "geosite-compat.dat", GeositeCompatCategories);
                RewriteGeoArray(rule, "ip", "geoip:", "geoip-compat.dat", GeoipCompatCategories);
            }
        }

        if (root["dns"]?["servers"] is JsonArray servers)
        {
            foreach (var server in servers.OfType<JsonObject>())
            {
                RewriteGeoArray(server, "domains", "geosite:", "geosite-compat.dat", GeositeCompatCategories);
                RewriteGeoArray(server, "expectedIPs", "geoip:", "geoip-compat.dat", GeoipCompatCategories);
                RewriteGeoArray(server, "expectIPs", "geoip:", "geoip-compat.dat", GeoipCompatCategories);
            }
        }
    }

    private static void RewriteGeoArray(JsonObject parent, string field, string prefix, string compatFile, HashSet<string> categories)
    {
        if (parent[field] is not JsonArray array) return;
        var next = new JsonArray();
        foreach (var item in array)
        {
            var value = item?.GetValue<string>();
            if (value is not null && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var code = value[prefix.Length..].ToLowerInvariant();
                next.Add(categories.Contains(code) ? $"ext:{compatFile}:{code}" : value);
            }
            else if (item is not null)
            {
                next.Add(item.DeepClone());
            }
        }
        parent[field] = next;
    }

    private static void RemoveFakeDnsSniffing(JsonObject root)
    {
        var hasFakeDns = root["fakedns"] is not null;
        if (!hasFakeDns && root["dns"]?["servers"] is JsonArray servers)
        {
            hasFakeDns = servers.Any(x => x is JsonValue value && value.TryGetValue<string>(out var text) &&
                                          string.Equals(text, "fakedns", StringComparison.OrdinalIgnoreCase));
        }
        if (hasFakeDns) return;

        if (root["inbounds"] is not JsonArray inbounds) return;
        foreach (var inbound in inbounds.OfType<JsonObject>())
        {
            if (inbound["sniffing"]?["destOverride"] is not JsonArray overrides) continue;
            var filtered = new JsonArray(overrides
                .Select(x => x is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
                .Where(x => !string.Equals(x, "fakedns", StringComparison.OrdinalIgnoreCase))
                .Select(x => (JsonNode?)x!)
                .ToArray());
            inbound["sniffing"]!["destOverride"] = filtered;
        }
    }

    private static void TweakBurstObservatory(JsonObject root)
    {
        if (root["burstObservatory"] is not JsonObject observatory) return;

        // Пингуем только первую ступень автовыбора, а не все запасные ноды сразу.
        observatory["subjectSelector"] = new JsonArray("route-p0000-");
        var ping = observatory["pingConfig"] as JsonObject ?? new JsonObject();
        observatory["pingConfig"] = ping;
        ping["interval"] = "1m";
        ping["sampling"] = 1;
        ping["timeout"] = "3s";
    }

    private static void EnsureMetrics(JsonObject root)
    {
        // Включаем встроенные Prometheus-метрики xray на localhost,
        // чтобы мы могли точно увидеть, какой outbound/tag раздувает RAM.
        if (root["stats"] is null) root["stats"] = new JsonObject();

        if (root["metrics"] is null)
        {
            root["metrics"] = new JsonObject
            {
                ["tag"] = "metrics",
                ["listen"] = "127.0.0.1:19099"
            };
        }

        if (root["policy"] is not JsonObject policy) {
            policy = new JsonObject();
            root["policy"] = policy;
        }

        if (policy["system"] is not JsonObject system) {
            system = new JsonObject();
            policy["system"] = system;
        }

        // Включаем максимальный сбор статистики для counters/метрик.
        system["statsInboundUplink"] = true;
        system["statsInboundDownlink"] = true;
        system["statsOutboundUplink"] = true;
        system["statsOutboundDownlink"] = true;
    }

    private static JsonObject? DecodeRoutingBundle(string value)
    {
        try
        {
            if (value.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
            {
                var raw = value[7..].Replace('-', '+').Replace('_', '/'); raw += new string('=', (4 - raw.Length % 4) % 4);
                value = Encoding.UTF8.GetString(Convert.FromBase64String(raw));
            }
            return JsonNode.Parse(value) as JsonObject;
        }
        catch { return null; }
    }

    private static string? ReadString(JsonObject obj, params string[] keys) => keys.Select(k => obj[k]?.GetValue<string>()).FirstOrDefault(x => x is not null);
    private static IEnumerable<string> ToDomainMatchers(IEnumerable<string>? values)
    {
        foreach (var raw in values ?? [])
        {
            var value = raw.Trim();
            if (value.Length == 0) continue;
            if (value.Contains(':', StringComparison.Ordinal))
            {
                yield return value;
                continue;
            }

            yield return $"domain:{value}";
        }
    }

    private static IEnumerable<string> ReadStrings(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj[key] is JsonArray array) return array.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x))!;
            if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text)) return new[] { text };
        }
        return [];
    }

    private static IEnumerable<string> ApplyBlockListMode(IEnumerable<string> domains, bool useFullBlockList)
    {
        var rewritten = domains.Select(value =>
            value.Equals("geosite:ru-blocked", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("geosite:ru-blocked-all", StringComparison.OrdinalIgnoreCase)
                ? (useFullBlockList ? "geosite:ru-blocked-all" : "geosite:ru-blocked")
                : value);
        return rewritten.Append("domain:gstatic.com");
    }

    private static void AddRule(JsonArray rules, string field, IEnumerable<string> values, string tag)
    {
        var items = values
            .Where(x => !UnsupportedGeositeRules.Contains(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (items.Length == 0) return;
        rules.Add(new JsonObject { ["type"] = "field", [field] = new JsonArray(items.Select(x => (JsonNode?)x).ToArray()), ["outboundTag"] = tag });
    }

    private static JsonObject AddTunInbound(JsonObject root)
    {
        var inbounds = root["inbounds"] as JsonArray ?? new JsonArray();
        root["inbounds"] = inbounds;
        for (var i = inbounds.Count - 1; i >= 0; i--)
        {
            if (inbounds[i] is JsonObject inbound && inbound["protocol"]?.GetValue<string>() == "tun")
                inbounds.RemoveAt(i);
        }
        inbounds.Insert(0, new JsonObject {
            ["tag"] = "lampa-tun-in", ["protocol"] = "tun",
            ["settings"] = new JsonObject {
                ["name"] = "Lampa", ["desc"] = "Lampa VPN", ["mtu"] = 1400,
                ["gateway"] = new JsonArray("10.89.0.1/30", "fd89::1/126"),
                ["userLevel"] = 0,
                ["autoSystemRoutingTable"] = new JsonArray("0.0.0.0/0", "::/0"),
                ["autoOutboundsInterface"] = "auto"
            },
            ["sniffing"] = new JsonObject {
                ["enabled"] = true,
                ["routeOnly"] = true,
                ["destOverride"] = new JsonArray("http", "tls", "quic")
            }
        });
        return root;
    }

    private static object BuildVmess(string link)
    {
        var raw = link["vmess://".Length..];
        raw += new string('=', (4 - raw.Length % 4) % 4);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(raw.Replace('-', '+').Replace('_', '/'))));
        var r = doc.RootElement;
        var host = Get(r, "host"); var path = Get(r, "path"); var net = Get(r, "net", "tcp"); var tls = Get(r, "tls");
        return new {
            tag = "proxy", protocol = "vmess",
            settings = new { vnext = new[] { new { address = Get(r, "add"), port = int.Parse(Get(r, "port")), users = new[] { new { id = Get(r, "id"), alterId = int.TryParse(Get(r, "aid"), out var aid) ? aid : 0, security = Get(r, "scy", "auto") } } } } },
            streamSettings = Stream(net, tls, host, path, Get(r, "sni", host), Get(r, "type"))
        };
    }

    private static object BuildUriProfile(string link)
    {
        var uri = new Uri(link); var query = ParseQuery(uri.Query);
        var protocol = uri.Scheme.ToLowerInvariant();
        var user = Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]);
        object settings = protocol switch {
            "vless" => new { vnext = new[] { new { address = uri.Host, port = uri.Port, users = new[] { new { id = user, encryption = Get(query, "encryption", "none"), flow = Get(query, "flow") } } } } },
            "trojan" => new { servers = new[] { new { address = uri.Host, port = uri.Port, password = user } } },
            _ => throw new NotSupportedException($"Протокол {protocol} пока не поддерживается core-конфигуратором")
        };
        return new { tag = "proxy", protocol, settings, streamSettings = Stream(Get(query, "type", "tcp"), Get(query, "security"), Get(query, "host"), Get(query, "path"), Get(query, "sni", uri.Host), Get(query, "headerType"), Get(query, "pbk"), Get(query, "sid"), Get(query, "fp")) };
    }

    private static object Stream(string network, string security, string host, string path, string sni, string headerType, string pbk = "", string sid = "", string fp = "") => new {
        network, security,
        tlsSettings = security == "tls" ? new { serverName = sni, fingerprint = string.IsNullOrEmpty(fp) ? "chrome" : fp, allowInsecure = false } : null,
        realitySettings = security == "reality" ? new { serverName = sni, publicKey = pbk, shortId = sid, fingerprint = string.IsNullOrEmpty(fp) ? "chrome" : fp } : null,
        wsSettings = network == "ws" ? new { path = string.IsNullOrEmpty(path) ? "/" : path, headers = new Dictionary<string,string> { ["Host"] = host } } : null,
        grpcSettings = network == "grpc" ? new { serviceName = path } : null,
        tcpSettings = network == "tcp" && headerType == "http" ? new { header = new { type = "http" } } : null
    };

    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => x.Length > 1 ? Uri.UnescapeDataString(x[1]) : "", StringComparer.OrdinalIgnoreCase);
    private static string Get(Dictionary<string,string> map, string key, string fallback = "") => map.TryGetValue(key, out var value) ? value : fallback;
    private static string Get(JsonElement el, string key, string fallback = "") => el.TryGetProperty(key, out var value) ? value.ToString() : fallback;
}
