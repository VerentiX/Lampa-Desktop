using System.Text.Json.Nodes;
using Lampa.Desktop.Services;

if (args.Length is < 1 or > 2) throw new ArgumentException("Pass a subscription URL and optionally a core path.");
var result = await new SubscriptionService().DownloadAsync(args[0], CancellationToken.None);
if (result.Profiles.Count == 0) throw new InvalidOperationException("No profiles parsed.");
foreach (var profile in result.Profiles)
{
    var root = JsonNode.Parse(profile.ConfigJson)?.AsObject();
    var outboundCount = root?["outbounds"]?.AsArray().Count ?? 0;
    Console.WriteLine($"PROFILE={profile.Name}; OUTBOUNDS={outboundCount}; TITLE={result.Metadata.Title}");
    if (args.Length > 1)
    {
        var configPath = Path.Combine(Path.GetTempPath(), "lampa-smoke-config.json");
        await File.WriteAllTextAsync(configPath, CoreConfigBuilder.Build(profile, 10809, true, result.Metadata.ProfileRouting, [@"C:\Program Files\Direct App\app.exe"], 5));
        var built = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!.AsObject();
        var rules = built["routing"]!["rules"]!.AsArray();
        if (!rules.Any(x => x?["process"] is not null) || !rules.Any(x => x?["ip"]?.ToJsonString().Contains("geoip:whitelist") == true)
            || !rules.Any(x => x?["domain"]?.ToJsonString().Contains("domain:gosuslugi.ru") == true)
            || !rules.Any(x => x?["domain"]?.ToJsonString().Contains("domain:2ip.ru") == true))
            throw new InvalidOperationException("Desktop routing rules were not applied.");
        var startInfo = new System.Diagnostics.ProcessStartInfo(args[1], $"run -test -c \"{configPath}\"") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(args[1]))! };
        startInfo.Environment["XRAY_LOCATION_ASSET"] = Path.Combine(Lampa.Desktop.Models.AppSettings.DataDirectory, "assets");
        using var process = System.Diagnostics.Process.Start(startInfo);
        await process!.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Core rejected config (exit {process.ExitCode}).");
        Console.WriteLine("CORE_CONFIG=OK");
    }
}
