using System.Text.Json.Nodes;
using Lampa.Desktop.Services;

if (args.Length is < 1 or > 2) throw new ArgumentException("Pass a subscription URL and optionally a core path.");
var result = await new SubscriptionService().DownloadAsync(args[0], CancellationToken.None);
if (result.Profiles.Count == 0) throw new InvalidOperationException("No profiles parsed.");
foreach (var profile in result.Profiles)
{
    var source = JsonNode.Parse(profile.ConfigJson);
    var outboundCount = source is JsonArray array ? array.Count : source?["outbounds"]?.AsArray().Count ?? 0;
    Console.WriteLine($"PROFILE={profile.Name}; OUTBOUNDS={outboundCount}; TITLE={result.Metadata.Title}");
    if (args.Length > 1)
    {
        var configPath = Path.Combine(Path.GetTempPath(), "lampa-smoke-config.json");
        var fullP0 = JsonNode.Parse(SingBoxConfigBuilder.Build(profile, 10809, true, result.Metadata.ProfileRouting,
            [@"C:\Program Files\Direct App\app.exe"], 0, useFullBlockList: true, routeExceptRussia: true))!.AsObject();
        var fullRules = fullP0["route"]!["rules"]!.AsArray();
        var bootstrapDns = fullP0["dns"]!["servers"]!.AsArray()
            .OfType<JsonObject>().Single(x => x["tag"]?.GetValue<string>() == "dns-bootstrap");
        if (bootstrapDns["type"]?.GetValue<string>() != "local" || bootstrapDns["server"] is not null)
            throw new Exception("Bootstrap DNS must use the Windows system resolver");
        if (!fullRules.Any(x => x?["network"]?.GetValue<string>() == "udp" &&
                                x?["port"]?.GetValue<int>() == 443 &&
                                x?["action"]?.GetValue<string>() == "reject"))
            throw new Exception("Desktop config must reject QUIC so browsers immediately fall back to TCP");
        if (fullP0["route"]?["final"]?.GetValue<string>() != "direct" ||
            fullP0["dns"]?["final"]?.GetValue<string>() != "dns-direct" ||
            !fullRules.Any(x => x?["rule_set"]?.ToJsonString().Contains("refilter-domains") == true))
            throw new InvalidOperationException("Full P0 routing was not applied.");
        var resolveIndex = fullRules.Select((rule, index) => (rule, index))
            .FirstOrDefault(x => x.rule?["action"]?.GetValue<string>() == "resolve").index;
        if (resolveIndex <= 0 || !fullRules.Skip(resolveIndex + 1)
                .Any(x => x?["rule_set"]?.ToJsonString().Contains("refilter-ips") == true))
            throw new InvalidOperationException("IPIfNonMatch retry pass was not generated.");

        var fastP0 = JsonNode.Parse(SingBoxConfigBuilder.Build(profile, 10809, true, result.Metadata.ProfileRouting,
            [], 0, useFullBlockList: false, routeExceptRussia: true))!.AsObject();
        if (fastP0["route"]?["final"]?.GetValue<string>() != "proxy" ||
            fastP0["dns"]?["final"]?.GetValue<string>() != "dns-tunnel" ||
            fastP0["route"]!["rules"]!.AsArray().Any(x => x?["rule_set"]?.ToJsonString().Contains("refilter-domains") == true))
            throw new InvalidOperationException("Fast P0 routing changed unexpectedly.");

        var p5 = JsonNode.Parse(SingBoxConfigBuilder.Build(profile, 10809, true, result.Metadata.ProfileRouting,
            [], 5, useFullBlockList: true, routeExceptRussia: true, whitelistMode: true))!.AsObject();
        if (!p5["route"]!["rules"]!.AsArray().Any(x => x?["rule_set"]?.ToJsonString().Contains("lampa-sber") == true) ||
            p5["dns"]?["final"]?.GetValue<string>() != "dns-tunnel")
            throw new InvalidOperationException("P5 routing changed unexpectedly.");

        var p0Only = JsonNode.Parse(SingBoxConfigBuilder.Build(profile, 10809, true, result.Metadata.ProfileRouting,
            [], 5, useFullBlockList: true, routeExceptRussia: true, whitelistMode: false))!.AsObject();
        if (p0Only["outbounds"]!.AsArray().OfType<JsonObject>()
            .Any(x => System.Text.RegularExpressions.Regex.IsMatch(x["tag"]?.GetValue<string>() ?? "",
                @"(?:^|[^a-z0-9])p0*(?:[5-9]|\d{2,})(?=[^0-9]|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new InvalidOperationException("Whitelist mode off still contains P5+ outbounds.");
        if (p0Only["route"]?["final"]?.GetValue<string>() != "direct")
            throw new InvalidOperationException("Whitelist mode off retained P5 routing policy.");

        foreach (var (name, config) in new[] { ("full-p0", fullP0), ("fast-p0", fastP0), ("p5", p5) })
        {
            await File.WriteAllTextAsync(configPath, config.ToJsonString());
            var startInfo = new System.Diagnostics.ProcessStartInfo(args[1], $"check -c \"{configPath}\"") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(args[1]))! };
            using var process = System.Diagnostics.Process.Start(startInfo);
            await process!.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException($"Core rejected {name} config (exit {process.ExitCode}).");
        }

        // `sing-box check` does not start DNS transports, so also perform a
        // short real start of Full P0 on isolated ports. This catches runtime
        // errors such as an invalid DNS detour.
        var runtime = JsonNode.Parse(SingBoxConfigBuilder.Build(profile, 11089, false, result.Metadata.ProfileRouting,
            [], 0, useFullBlockList: true, routeExceptRussia: true))!.AsObject();
        runtime["experimental"]!["clash_api"]!["external_controller"] = "127.0.0.1:19190";
        runtime["experimental"]!["cache_file"]!["path"] = Path.Combine(Path.GetTempPath(), "lampa-smoke-cache.db");
        await File.WriteAllTextAsync(configPath, runtime.ToJsonString());
        var runInfo = new System.Diagnostics.ProcessStartInfo(args[1], $"run -c \"{configPath}\"")
        {
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(args[1]))!,
            RedirectStandardError = true
        };
        using (var runtimeProcess = System.Diagnostics.Process.Start(runInfo)!)
        {
            await Task.Delay(4000);
            if (runtimeProcess.HasExited)
                throw new InvalidOperationException($"Core runtime start failed: {await runtimeProcess.StandardError.ReadToEndAsync()}");
            runtimeProcess.Kill(true);
            await runtimeProcess.WaitForExitAsync();
        }
        Console.WriteLine("CORE_RUNTIME=OK");
        Console.WriteLine("CORE_CONFIG=OK");
    }
}
