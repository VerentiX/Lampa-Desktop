using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Lampa.Desktop.Models;

namespace Lampa.Desktop.Services;

public sealed class ConnectionRow
{
    public string Id { get; init; } = "";
    public string Host { get; init; } = "";
    public string Chain { get; init; } = "";
    public string Network { get; init; } = "";
    public string Process { get; init; } = "";
    public string DownText { get; init; } = "";
    public string UpText { get; init; } = "";
    public string DownSpeedText { get; init; } = "";
    public string UpSpeedText { get; init; } = "";
    public long Download { get; init; }
    public long Upload { get; init; }
}

public sealed class ClashConnectionsSnapshot
{
    public long DownloadTotal { get; init; }
    public long UploadTotal { get; init; }
    public IReadOnlyList<ConnectionRow> Rows { get; init; } = [];
}

/// <summary>
/// Clash-API access log. Live polling runs only while a viewer is open.
/// History is sampled (new connection ids only) on a slow timer so TUN/YouTube
/// cannot keep the disk or CPU busy.
/// </summary>
public sealed class ClashConnectionMonitor : IDisposable
{
    private const string ApiRoot = "http://127.0.0.1:19090";
    private readonly AppSettings _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMilliseconds(800) };
    private readonly Dictionary<string, (long Up, long Down, long Stamp)> _speeds = [];
    private readonly Dictionary<string, long> _loggedAccess = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;
    private int _liveViewers;
    private bool _running;
    private bool _disposed;

    public event Action<ClashConnectionsSnapshot>? Updated;

    public ClashConnectionMonitor(AppSettings settings)
    {
        _settings = settings;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "lampa");
        _timer = new System.Threading.Timer(_ => _ = PollAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _running = true;
            _loggedAccess.Clear();
            _speeds.Clear();
            ArmTimer();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _loggedAccess.Clear();
            _speeds.Clear();
        }
    }

    public void AddLiveViewer()
    {
        Interlocked.Increment(ref _liveViewers);
        lock (_gate) ArmTimer();
    }

    public void RemoveLiveViewer()
    {
        Interlocked.Decrement(ref _liveViewers);
        lock (_gate) ArmTimer();
    }

    public void RefreshMode()
    {
        lock (_gate) ArmTimer();
    }

    public async Task CloseAllAsync()
    {
        try { using var response = await _http.DeleteAsync($"{ApiRoot}/connections"); }
        catch { }
    }

    public async Task CloseAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        try { using var response = await _http.DeleteAsync($"{ApiRoot}/connections/{Uri.EscapeDataString(id)}"); }
        catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;
        }
        _timer.Dispose();
        _http.Dispose();
    }

    private void ArmTimer()
    {
        if (_disposed || !_running)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        var live = Volatile.Read(ref _liveViewers) > 0;
        var persist = _settings.AccessLogEnabled && LogStore.ParseLevel(_settings.LogLevel) != AppLogLevel.None;
        if (!live && !persist)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        var interval = live ? 1000 : 5000;
        _timer.Change(interval, interval);
    }

    private async Task PollAsync()
    {
        if (!_running || _disposed) return;
        try
        {
            using var response = await _http.GetAsync($"{ApiRoot}/connections");
            if (!response.IsSuccessStatusCode) return;
            var json = await response.Content.ReadAsStringAsync();
            var snapshot = Parse(json, persist: _settings.AccessLogEnabled && LogStore.ParseLevel(_settings.LogLevel) != AppLogLevel.None);
            Updated?.Invoke(snapshot);
        }
        catch { }
    }

    private ClashConnectionsSnapshot Parse(string json, bool persist)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var downTotal = root.TryGetProperty("downloadTotal", out var dt) ? dt.GetInt64() : 0;
        var upTotal = root.TryGetProperty("uploadTotal", out var ut) ? ut.GetInt64() : 0;
        var rows = new List<ConnectionRow>();
        var now = Environment.TickCount64;
        var liveIds = new HashSet<string>(StringComparer.Ordinal);

        if (root.TryGetProperty("connections", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idEl) ? idEl.ToString() : "";
                if (string.IsNullOrEmpty(id)) continue;
                liveIds.Add(id);
                var upload = item.TryGetProperty("upload", out var upEl) ? upEl.GetInt64() : 0;
                var download = item.TryGetProperty("download", out var downEl) ? downEl.GetInt64() : 0;
                var host = "";
                var port = "";
                var network = "";
                var process = "";
                if (item.TryGetProperty("metadata", out var meta))
                {
                    host = meta.TryGetProperty("host", out var h) ? h.GetString() ?? "" : "";
                    port = meta.TryGetProperty("destinationPort", out var p) ? p.ToString() : "";
                    network = meta.TryGetProperty("network", out var n) ? n.GetString() ?? "" : "";
                    process = meta.TryGetProperty("processPath", out var pr) ? Path.GetFileName(pr.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(host) && meta.TryGetProperty("destinationIP", out var ip))
                        host = ip.ToString();
                }
                var chains = new List<string>();
                if (item.TryGetProperty("chains", out var chainEl) && chainEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var hop in chainEl.EnumerateArray())
                    {
                        var tag = hop.GetString();
                        if (!string.IsNullOrWhiteSpace(tag)) chains.Add(tag);
                    }
                }
                chains.Reverse();
                var chain = chains.Count == 0 ? "—" : string.Join(" / ", chains);
                var displayHost = string.IsNullOrWhiteSpace(port) || host.Contains(':') ? host : $"{host}:{port}";
                long upSpeed = 0, downSpeed = 0;
                lock (_gate)
                {
                    if (_speeds.TryGetValue(id, out var prev) && now > prev.Stamp)
                    {
                        var dtMs = Math.Max(1, now - prev.Stamp);
                        upSpeed = Math.Max(0, (upload - prev.Up) * 1000 / dtMs);
                        downSpeed = Math.Max(0, (download - prev.Down) * 1000 / dtMs);
                    }
                    _speeds[id] = (upload, download, now);
                    if (persist)
                    {
                        var accessKey = $"{network} {displayHost} {chain}";
                        if (!_loggedAccess.TryGetValue(accessKey, out var last) || now - last >= 60_000)
                        {
                            _loggedAccess[accessKey] = now;
                            LogStore.Instance.WriteAccess(accessKey);
                        }
                        if (_loggedAccess.Count > 4000)
                        {
                            foreach (var stale in _loggedAccess.Where(kv => now - kv.Value >= 60_000).Select(kv => kv.Key).ToList())
                                _loggedAccess.Remove(stale);
                            if (_loggedAccess.Count > 4000) _loggedAccess.Clear();
                        }
                    }
                }

                rows.Add(new ConnectionRow
                {
                    Id = id,
                    Host = string.IsNullOrWhiteSpace(displayHost) ? "—" : displayHost,
                    Chain = chain,
                    Network = network,
                    Process = process,
                    Download = download,
                    Upload = upload,
                    DownText = ByteText.Size(download),
                    UpText = ByteText.Size(upload),
                    DownSpeedText = ByteText.Speed(downSpeed),
                    UpSpeedText = ByteText.Speed(upSpeed)
                });
            }
        }

        lock (_gate)
        {
            foreach (var id in _speeds.Keys.Where(id => !liveIds.Contains(id)).ToList())
                _speeds.Remove(id);
        }

        rows.Sort((a, b) => b.Download.CompareTo(a.Download));
        return new ClashConnectionsSnapshot
        {
            DownloadTotal = downTotal,
            UploadTotal = upTotal,
            Rows = rows
        };
    }
}
