using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.IO;
using Lampa.Desktop.Models;

namespace Lampa.Desktop.Services;

/// <summary>Builds a native sing-box-lx desktop configuration from the worker's outbound array.</summary>
public static class SingBoxConfigBuilder
{
    private const string ProxyTag = "proxy";

    public static string Build(ProxyProfile profile, int httpPort, bool useTun = true,
        string profileRouting = "", IReadOnlyCollection<string>? bypassApplications = null, int activePriority = 0,
        IReadOnlyCollection<string>? customProxyDomains = null, IReadOnlyCollection<string>? customDirectDomains = null,
        bool useFullBlockList = true, bool routeExceptRussia = false, int ruleSetUpdateDays = 3,
        bool whitelistMode = false, string logLevel = "warn",
        string lastProxyOutbound = "", int clashApiPort = 19090)
    {
        var effectivePriority = whitelistMode ? activePriority : Math.Min(activePriority, 4);
        var outbounds = ReadOutbounds(profile);
        NormalizeOutbounds(outbounds);
        TuneUrlTest(outbounds);
        if (!whitelistMode) RemoveWhitelistPriorityOutbounds(outbounds);
        EnsureSystemOutbounds(outbounds);
        PreferLastOutbound(outbounds, lastProxyOutbound);

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = LogStore.ToSingBoxLevel(logLevel), ["timestamp"] = true },
            ["dns"] = BuildDns(useFullBlockList, effectivePriority, customProxyDomains ?? []),
            ["inbounds"] = BuildInbounds(httpPort, useTun),
            ["outbounds"] = outbounds,
            ["http_clients"] = new JsonArray
            {
                new JsonObject { ["tag"] = "rules-direct" },
                new JsonObject { ["tag"] = "rules-proxy", ["detour"] = ProxyTag }
            },
            ["route"] = BuildRoute(bypassApplications ?? [], effectivePriority,
                customProxyDomains ?? [], customDirectDomains ?? [], useFullBlockList, routeExceptRussia,
                Math.Clamp(ruleSetUpdateDays, 1, 7)),
            ["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{(clashApiPort is > 1024 and < 65534 ? clashApiPort : 19090)}",
                    ["secret"] = "lampa"
                },
                ["cache_file"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["path"] = Path.Combine(AppSettings.DataDirectory, "sing-box-cache.db"),
                    ["store_fakeip"] = false
                }
            }
        };
        return JsonText.Write(root);
    }

    private static void RemoveWhitelistPriorityOutbounds(JsonArray outbounds)
    {
        static bool IsP5OrHigher(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return false;
            var match = System.Text.RegularExpressions.Regex.Match(tag,
                @"(?:^|[^a-z0-9])p0*(\d+)(?=[^0-9]|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value, out var priority) && priority >= 5;
        }

        foreach (var outbound in outbounds.OfType<JsonObject>())
        {
            if (outbound["outbounds"] is not JsonArray members) continue;
            for (var i = members.Count - 1; i >= 0; i--)
            {
                if (members[i] is JsonValue value && value.TryGetValue<string>(out var tag) && IsP5OrHigher(tag))
                    members.RemoveAt(i);
            }
        }

        for (var i = outbounds.Count - 1; i >= 0; i--)
        {
            if (outbounds[i] is JsonObject outbound && IsP5OrHigher(outbound["tag"]?.GetValue<string>()))
                outbounds.RemoveAt(i);
        }

        // Remove nodes which were used only by the disabled P5+ branches.
        var byTag = outbounds.OfType<JsonObject>()
            .Where(x => !string.IsNullOrWhiteSpace(x["tag"]?.GetValue<string>()))
            .ToDictionary(x => x["tag"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ProxyTag, "direct", "block" };
        var queue = new Queue<string>(reachable);
        while (queue.TryDequeue(out var tag))
        {
            if (!byTag.TryGetValue(tag, out var outbound)) continue;
            if (outbound["outbounds"] is JsonArray members)
            {
                foreach (var member in members.OfType<JsonValue>())
                    if (member.TryGetValue<string>(out var child) && reachable.Add(child)) queue.Enqueue(child);
            }
            if (outbound["detour"] is JsonValue detour && detour.TryGetValue<string>(out var parent) && reachable.Add(parent))
                queue.Enqueue(parent);
        }

        for (var i = outbounds.Count - 1; i >= 0; i--)
        {
            var tag = outbounds[i]?["tag"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(tag) && !reachable.Contains(tag)) outbounds.RemoveAt(i);
        }
    }

    private static JsonArray ReadOutbounds(ProxyProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.ConfigJson))
            throw new InvalidOperationException("В подписке нет sing-box конфигурации. Обновите подписку Lampa-Desktop-SB.");
        var parsed = JsonNode.Parse(profile.ConfigJson);
        if (parsed is JsonArray array) return array.DeepClone().AsArray();
        if (parsed is JsonObject root && root["outbounds"] is JsonArray embedded)
            return embedded.DeepClone().AsArray();
        throw new InvalidOperationException("Воркер вернул неподдерживаемый формат: ожидается JSON-массив sing-box outbounds.");
    }

    private static void NormalizeOutbounds(JsonArray outbounds)
    {
        foreach (var outbound in outbounds.OfType<JsonObject>())
        {
            if (outbound["tls"] is JsonObject tls && tls["alpn"] is JsonValue alpnValue &&
                alpnValue.TryGetValue<string>(out var alpn) && !string.IsNullOrWhiteSpace(alpn))
                tls["alpn"] = new JsonArray(alpn);

            if (outbound["transport"] is not JsonObject transport) continue;
            MoveAlias(transport, "session_id_table", "session_table");
            MoveAlias(transport, "session_id_length", "session_length");
            MoveAlias(transport, "sessionIDTable", "session_table");
            MoveAlias(transport, "sessionIDLength", "session_length");
        }

        var leaves = outbounds.OfType<JsonObject>()
            .Where(x => !IsGroup(x))
            .Select(x => x["tag"]?.GetValue<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>().ToArray();
        if (!outbounds.OfType<JsonObject>().Any(x => x["tag"]?.GetValue<string>() == ProxyTag))
        {
            var suppliedGroup = outbounds.OfType<JsonObject>().FirstOrDefault(IsGroup);
            if (suppliedGroup is not null)
                suppliedGroup["tag"] = ProxyTag;
            else
            {
                outbounds.Add(new JsonObject
                {
                    ["type"] = "urltest", ["tag"] = ProxyTag,
                    ["outbounds"] = new JsonArray(leaves.Select(x => (JsonNode?)x).ToArray()),
                    ["url"] = "https://www.gstatic.com/generate_204",
                    ["tolerance"] = 50,
                    ["interrupt_exist_connections"] = false
                });
            }
        }
    }

    private static void TuneUrlTest(JsonArray outbounds)
    {
        foreach (var outbound in outbounds.OfType<JsonObject>())
        {
            if (outbound["type"]?.GetValue<string>() != "urltest") continue;
            outbound["interval"] = "15m";
            outbound["active_check_interval"] = "30s";
            outbound["active_check_failures"] = 2;
            outbound["interrupt_exist_connections"] = false;
            if (outbound["url"] is null)
                outbound["url"] = "https://www.gstatic.com/generate_204";
        }
    }

    private static void PreferLastOutbound(JsonArray outbounds, string last)
    {
        if (string.IsNullOrWhiteSpace(last)) return;
        foreach (var group in outbounds.OfType<JsonObject>().Where(IsGroup))
        {
            if (group["outbounds"] is not JsonArray members) continue;
            var tags = members.OfType<JsonValue>()
                .Select(value => value.TryGetValue<string>(out var tag) ? tag : "")
                .Where(tag => tag.Length > 0)
                .ToList();
            if (!tags.Exists(tag => string.Equals(tag, last, StringComparison.OrdinalIgnoreCase)))
                continue;

            members.Clear();
            members.Add(last);
            foreach (var tag in tags)
            {
                if (!string.Equals(tag, last, StringComparison.OrdinalIgnoreCase))
                    members.Add(tag);
            }
            if (group["type"]?.GetValue<string>() == "selector")
                group["default"] = last;
        }
    }

    private static bool IsGroup(JsonObject outbound)
    {
        var type = outbound["type"]?.GetValue<string>();
        return type is "urltest" or "selector";
    }

    private static void MoveAlias(JsonObject obj, string source, string target)
    {
        if (obj[target] is null && obj[source] is not null) obj[target] = obj[source]!.DeepClone();
        obj.Remove(source);
    }

    private static void EnsureSystemOutbounds(JsonArray outbounds)
    {
        EnsureOutbound(outbounds, "direct", "direct");
        EnsureOutbound(outbounds, "block", "block");
    }

    private static void EnsureOutbound(JsonArray outbounds, string tag, string type)
    {
        if (outbounds.OfType<JsonObject>().Any(x => x["tag"]?.GetValue<string>() == tag)) return;
        outbounds.Add(new JsonObject { ["type"] = type, ["tag"] = tag });
    }

    private static JsonObject BuildDns(bool useFullBlockList, int activePriority,
        IReadOnlyCollection<string> customProxyDomains)
    {
        var selectiveFullRouting = useFullBlockList && activePriority < 5;
        var servers = new JsonArray
        {
            // Bootstrap and remote rule-set hostnames must use the Windows
            // resolver.  A fixed 1.1.1.1:53 dependency breaks startup on
            // networks that block third-party UDP DNS.
            new JsonObject { ["type"] = "local", ["tag"] = "dns-bootstrap" },
            new JsonObject
            {
                ["type"] = "https", ["tag"] = "dns-tunnel", ["server"] = "1.1.1.1", ["server_port"] = 443,
                ["path"] = "/dns-query", ["detour"] = ProxyTag
            }
        };
        if (selectiveFullRouting)
            servers.Add(new JsonObject
            {
                ["type"] = "https", ["tag"] = "dns-direct", ["server"] = "77.88.8.8", ["server_port"] = 443,
                // No explicit detour: sing-box lx.29 rejects detouring a DNS
                // transport to an otherwise empty direct outbound at runtime.
                ["path"] = "/dns-query"
            });

        var dns = new JsonObject
        {
            ["servers"] = servers,
            ["final"] = selectiveFullRouting ? "dns-direct" : "dns-tunnel",
        // The Windows TUN adapter must not advertise IPv6 unless the selected
        // outbound can actually carry it. Chromium otherwise prefers AAAA,
        // completes the synthetic TUN connect and then loses the TLS handshake
        // with ERR_CONNECTION_CLOSED on IPv4-only upstream routes.
            ["strategy"] = "ipv4_only"
        };
        if (selectiveFullRouting)
        {
            var dnsRules = new JsonArray();
            var custom = NormalizeHostList(customProxyDomains);
            if (custom.Length > 0)
                dnsRules.Add(new JsonObject
                {
                    ["domain_suffix"] = new JsonArray(custom.Select(x => (JsonNode?)x).ToArray()),
                    ["server"] = "dns-tunnel"
                });
            dnsRules.Add(new JsonObject { ["rule_set"] = FullProxyDomainRuleSets(), ["server"] = "dns-tunnel" });
            dns["rules"] = dnsRules;
        }
        return dns;
    }

    private static JsonArray BuildInbounds(int httpPort, bool useTun)
    {
        var result = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "mixed", ["tag"] = "mixed-in", ["listen"] = "127.0.0.1",
                ["listen_port"] = httpPort
            }
        };
        if (useTun)
        {
            // Mixed must bind first. If Windows reserved the local port, fail
            // before the TUN adapter is created.
            result.Add(new JsonObject
            {
                ["type"] = "tun", ["tag"] = "tun-in", ["interface_name"] = "Lampa",
                ["address"] = new JsonArray("172.19.0.1/30"),
                ["mtu"] = 1400, ["auto_route"] = true, ["strict_route"] = true,
                ["stack"] = "mixed"
            });
        }
        return result;
    }

    private static JsonObject BuildRoute(IReadOnlyCollection<string> bypassApplications, int activePriority,
        IReadOnlyCollection<string> customProxyDomains, IReadOnlyCollection<string> customDirectDomains,
        bool useFullBlockList, bool routeExceptRussia, int ruleSetUpdateDays)
    {
        var p5 = activePriority >= 5;
        var selectiveFullRouting = useFullBlockList && !p5;
        var rules = new JsonArray
        {
            new JsonObject { ["action"] = "sniff", ["timeout"] = "100ms" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" }
        };
        rules.Add(new JsonObject { ["network"] = "udp", ["port"] = 443, ["action"] = "reject" });
        rules.Add(new JsonObject { ["rule_set"] = "ads-all", ["outbound"] = "block" });
        AddDomainRule(rules, customProxyDomains, ProxyTag);
        AddDomainRule(rules, customDirectDomains, "direct");
        if (bypassApplications.Count > 0)
            rules.Add(new JsonObject
            {
                ["process_path"] = new JsonArray(bypassApplications.Select(x => (JsonNode?)x).ToArray()),
                ["outbound"] = "direct"
            });

        var ruleSets = new JsonArray();
        AddRemoteRuleSet(ruleSets, "ads-all",
            "https://raw.githubusercontent.com/runetfreedom/russia-v2ray-rules-dat/release/sing-box/rule-set-geosite/geosite-category-ads-all.srs",
            ruleSetUpdateDays, true);
        AddRemoteRuleSet(ruleSets, "roscom-whitelist", "https://cdn.jsdelivr.net/gh/hydraponique/roscomvpn-geosite@release/sing-box/whitelist.srs", ruleSetUpdateDays);
        AddRemoteRuleSet(ruleSets, "roscom-category-ru", "https://cdn.jsdelivr.net/gh/hydraponique/roscomvpn-geosite@release/sing-box/category-ru.srs", ruleSetUpdateDays);
        AddRemoteRuleSet(ruleSets, "roscom-private", "https://cdn.jsdelivr.net/gh/hydraponique/roscomvpn-geosite@release/sing-box/private.srs", ruleSetUpdateDays);
        AddRemoteRuleSet(ruleSets, "roscom-ip-direct", "https://cdn.jsdelivr.net/gh/hydraponique/roscomvpn-geoip@release/sing-box/direct.srs", ruleSetUpdateDays);
        AddInlineRuleSet(ruleSets, "lampa-sber", ["sber.ru", "sberbank.ru", "sberbank.com", "sbrf.ru", "sbercloud.ru", "sberdevices.ru", "sbermobile.ru", "sberspasibo.ru"]);
        AddInlineRuleSet(ruleSets, "lampa-tbank", ["tbank.ru", "tinkoff.ru", "tinkoff.com", "tcsbank.ru", "tinkoffjournal.ru", "tinkoffmobile.com", "t-j.ru", "t-static.ru", "t-tech.ru"]);

        // First pass keeps AsIs-like behaviour: domain and already-known IP
        // rules can match without forcing a DNS lookup.
        var profileRuleStart = rules.Count;
        if (p5)
        {
            rules.Add(new JsonObject { ["rule_set"] = new JsonArray("lampa-sber", "lampa-tbank"), ["outbound"] = ProxyTag });
            rules.Add(new JsonObject { ["rule_set"] = "roscom-whitelist", ["outbound"] = "direct" });
        }
        else if (selectiveFullRouting)
        {
            AddRemoteRuleSet(ruleSets, "refilter-domains", "https://github.com/1andrevich/Re-filter-lists/releases/latest/download/ruleset-domain-refilter_domains.srs", ruleSetUpdateDays, true);
            AddRemoteRuleSet(ruleSets, "refilter-ips", "https://github.com/1andrevich/Re-filter-lists/releases/latest/download/ruleset-ip-refilter_ipsum.srs", ruleSetUpdateDays, true);
            AddRemoteRuleSet(ruleSets, "roscom-geoblock-ru", "https://cdn.jsdelivr.net/gh/hydraponique/roscomvpn-geosite@release/sing-box/category-geoblock-ru.srs", ruleSetUpdateDays, true);
            AddRemoteRuleSet(ruleSets, "roscom-youtube", "https://cdn.jsdelivr.net/gh/hydraponique/roscomvpn-geosite@release/sing-box/youtube.srs", ruleSetUpdateDays, true);
            AddRemoteRuleSet(ruleSets, "roscom-telegram", "https://cdn.jsdelivr.net/gh/hydraponique/roscomvpn-geosite@release/sing-box/telegram.srs", ruleSetUpdateDays, true);
            AddRemoteRuleSet(ruleSets, "roscom-github", "https://cdn.jsdelivr.net/gh/hydraponique/roscomvpn-geosite@release/sing-box/github.srs", ruleSetUpdateDays, true);
            AddRemoteRuleSet(ruleSets, "roscom-google-play", "https://cdn.jsdelivr.net/gh/hydraponique/roscomvpn-geosite@release/sing-box/google-play.srs", ruleSetUpdateDays, true);
            rules.Add(new JsonObject { ["rule_set"] = "roscom-private", ["outbound"] = "direct" });
            rules.Add(new JsonObject
            {
                ["rule_set"] = new JsonArray("refilter-domains", "refilter-ips", "roscom-geoblock-ru",
                    "roscom-youtube", "roscom-telegram", "roscom-github", "roscom-google-play"),
                ["outbound"] = ProxyTag
            });
        }
        else
        {
            rules.Add(new JsonObject
            {
                ["rule_set"] = new JsonArray("roscom-private", "roscom-category-ru", "roscom-whitelist", "roscom-ip-direct"),
                ["outbound"] = "direct"
            });
        }

        // Native sing-box has no global Xray `IPIfNonMatch` switch.  Reproduce
        // it explicitly: only traffic that did not match the first pass is
        // resolved, then the same profile rules are retried so IP entries in
        // mixed SRS sets can select the intended outbound.  Raw-IP traffic and
        // domains matched on the first pass pay no extra DNS lookup.
        var retryRules = rules.Skip(profileRuleStart)
            .Select(rule => rule?.DeepClone())
            .ToArray();
        rules.Add(new JsonObject { ["action"] = "resolve", ["strategy"] = "ipv4_only" });
        foreach (var retryRule in retryRules)
            rules.Add(retryRule);

        return new JsonObject
        {
            ["rules"] = rules, ["rule_set"] = ruleSets,
            ["final"] = p5 || (!selectiveFullRouting && routeExceptRussia) ? ProxyTag : "direct",
            ["auto_detect_interface"] = true, ["default_domain_resolver"] = "dns-bootstrap",
            ["default_http_client"] = "rules-direct"
        };
    }

    private static void AddDomainRule(JsonArray rules, IEnumerable<string> domains, string outbound)
    {
        var values = NormalizeHostList(domains);
        if (values.Length == 0) return;
        rules.Add(new JsonObject { ["domain_suffix"] = new JsonArray(values.Select(x => (JsonNode?)x).ToArray()), ["outbound"] = outbound });
    }

    private static string[] NormalizeHostList(IEnumerable<string> domains) =>
        domains.Select(NormalizeDomain).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static string NormalizeDomain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var host = value.Trim();
        if (host.Contains("://", StringComparison.OrdinalIgnoreCase))
        {
            try { host = new Uri(host).Host; } catch { }
        }
        host = host.Split('/')[0].Split('?')[0].Split('#')[0].Trim().Trim('.');
        if (!host.StartsWith('[') && host.Count(c => c == ':') == 1)
        {
            var colon = host.LastIndexOf(':');
            if (colon > 0 && host[(colon + 1)..].All(char.IsDigit))
                host = host[..colon];
        }
        host = host.Trim('.').ToLowerInvariant();
        try { if (host.Length > 0) host = new IdnMapping().GetAscii(host); } catch { return ""; }
        return IsUsableHost(host) ? host : "";
    }

    private static bool IsUsableHost(string host)
    {
        if (host.Length is < 1 or > 253) return false;
        if (IPAddress.TryParse(host, out _)) return true;
        if (!host.Contains('.')) return false;
        foreach (var label in host.Split('.'))
        {
            if (label.Length is 0 or > 63) return false;
            if (label.StartsWith('-') || label.EndsWith('-')) return false;
            if (!label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
        }
        return true;
    }

    private static JsonArray FullProxyDomainRuleSets() => new(
        "refilter-domains", "roscom-geoblock-ru", "roscom-youtube", "roscom-telegram", "roscom-github", "roscom-google-play");

    private static void AddRemoteRuleSet(JsonArray target, string tag, string url, int updateDays, bool throughProxy = false) => target.Add(new JsonObject
    {
        ["type"] = "remote", ["tag"] = tag, ["format"] = "binary", ["url"] = url,
        ["update_interval"] = $"{updateDays}d", ["http_client"] = throughProxy ? "rules-proxy" : "rules-direct"
    });

    private static void AddInlineRuleSet(JsonArray target, string tag, string[] domains) => target.Add(new JsonObject
    {
        ["type"] = "inline", ["tag"] = tag,
        ["rules"] = new JsonArray(new JsonObject { ["domain_suffix"] = new JsonArray(domains.Select(x => (JsonNode?)x).ToArray()) })
    });
}
