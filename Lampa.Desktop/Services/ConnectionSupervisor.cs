using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.IO;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using Lampa.Desktop.Models;

namespace Lampa.Desktop.Services;

public enum ConnectionState { Disconnected, Connecting, Connected, Recovering, Paused, Error }

public sealed class ConnectionSupervisor : IDisposable
{
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Threading.Timer _watchdog;
    private readonly SleepPowerMonitor _sleep;
    private readonly ClashConnectionMonitor _connections;
    private Process? _core;
    private DateTimeOffset _coreStartedAt = DateTimeOffset.MinValue;
    private volatile bool _suspended;
    private bool _disposed;
    private int _failedHealthChecks;
    private int _resumeGeneration;
    private DateTimeOffset _connectedAt = DateTimeOffset.MinValue;
    private string _readyConfigFingerprint = "";
    private string _priorityCandidate = "";
    private int _priorityConfirmations;
    private DateTimeOffset _lastStartAttempt = DateTimeOffset.MinValue;
    private int _consecutiveStartFails;
    private readonly List<string> _recentCoreErrors = [];
    public ConnectionState State { get; private set; }
    public string ActiveRouteName { get; private set; } = "";
    private string ClashApiRoot => $"http://127.0.0.1:{(_settings.ClashApiPort is > 1024 and < 65534 ? _settings.ClashApiPort : 19090)}";
    public ClashConnectionMonitor Connections => _connections;
    public event Action<ConnectionState, string>? StateChanged;

    public ConnectionSupervisor(AppSettings settings)
    {
        _settings = settings;
        LogStore.Instance.ApplySettings(settings);
        _sleep = new SleepPowerMonitor();
        _connections = new ClashConnectionMonitor(settings);
        _sleep.SleepRequested += OnSleepRequested;
        _sleep.WakeRequested += OnWakeRequested;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _watchdog = new System.Threading.Timer(_ => _ = WatchdogAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }

    public Task ConnectAsync()
    {
        _suspended = false;
        _settings.DesiredConnected = true;
        _settings.Save();
        return EnsureConnectedAsync(false);
    }

    public async Task ReloadAsync()
    {
        _suspended = false;
        _settings.DesiredConnected = true;
        _settings.Save();
        _readyConfigFingerprint = "";
        await _gate.WaitAsync();
        try
        {
            SetState(ConnectionState.Recovering, "Обновляем маршруты…");
            StopCore();
            SystemProxy.Disable();
        }
        finally { _gate.Release(); }
        if (_settings.UseTun) await Task.Delay(1200);
        await EnsureConnectedAsync(true);
    }

    public async Task DisconnectAsync(bool userInitiated = true)
    {
        if (userInitiated)
        {
            _settings.DesiredConnected = false;
            _settings.Save();
            _suspended = false;
        }
        await _gate.WaitAsync();
        try { StopCore(); SystemProxy.Disable(); SetState(ConnectionState.Disconnected, "Отключено"); }
        finally { _gate.Release(); }
    }

    private async Task EnsureConnectedAsync(bool recovering)
    {
        if (!_settings.DesiredConnected || _suspended || _disposed) return;
        await _gate.WaitAsync();
        try
        {
            if (!_settings.DesiredConnected || _suspended || _disposed) return;
            if (_core is { HasExited: false } && await IsPortOpenAsync()) {
                if (_settings.UseTun) SystemProxy.Disable(); else SystemProxy.Enable(_settings.LocalHttpPort);
                SetState(ConnectionState.Connected, _settings.UseTun ? "Весь трафик защищён через TUN" : "Соединение защищено"); return;
            }
            SetState(recovering ? ConnectionState.Recovering : ConnectionState.Connecting, recovering ? "Восстанавливаем соединение…" : "Подключаемся…");
            WriteSupervisorLog($"start-core recovering={recovering}", AppLogLevel.Info);
            _lastStartAttempt = DateTimeOffset.Now;
            await Task.Yield();
            StopCore(); SystemProxy.Disable();
            if (_settings.UseTun) await Task.Delay(800);
            var profile = _settings.Profiles.ElementAtOrDefault(_settings.SelectedProfile) ?? throw new InvalidOperationException("Добавьте подписку и выберите сервер");
            var corePath = Path.GetFullPath(Path.IsPathRooted(_settings.CorePath) ? _settings.CorePath : Path.Combine(AppContext.BaseDirectory, _settings.CorePath));
            if (!File.Exists(corePath)) throw new FileNotFoundException("Компонент подключения отсутствует. Переустановите Lampa VPN.", corePath);
            StopStaleCoreProcesses(corePath);
            await Task.Delay(400);
            EnsureLocalPorts();
            Directory.CreateDirectory(AppSettings.DataDirectory);
            var configPath = Path.Combine(AppSettings.DataDirectory, "config.json");
            await WriteCoreConfigAsync(profile, configPath);
            ClearCoreErrors();
            if (!string.IsNullOrWhiteSpace(_settings.LastProxyOutbound))
                ActiveRouteName = _settings.LastProxyOutbound;
            await LaunchCoreAsync(corePath, configPath);
            if (!await IsPortOpenAsync() && IsLocalBindFailure())
            {
                WriteSupervisorLog($"start-failed bind {LastCoreError()}", AppLogLevel.Warn);
                StopCore();
                await Task.Delay(400);
                EnsureLocalPorts(skipCurrent: true);
                await WriteCoreConfigAsync(profile, configPath);
                ClearCoreErrors();
                await LaunchCoreAsync(corePath, configPath);
            }
            else if (!await IsPortOpenAsync())
            {
                WriteSupervisorLog($"start-failed {LastCoreError()}", AppLogLevel.Error);
                StopCore();
                if (_settings.UseTun) await Task.Delay(2000);
                await LaunchCoreAsync(corePath, configPath);
            }
            if (!await IsPortOpenAsync())
                throw new InvalidOperationException(StartFailureMessage());
            if (_settings.UseTun) SystemProxy.Disable(); else SystemProxy.Enable(_settings.LocalHttpPort);
            _connectedAt = DateTimeOffset.Now;
            _failedHealthChecks = 0;
            _consecutiveStartFails = 0;
            _connections.Start();
            SetState(ConnectionState.Connected, recovering ? "TUN-соединение восстановлено" : "Весь трафик защищён через TUN");
            _ = ProbeUrlTestAsync();
        }
        catch (Exception ex)
        {
            _consecutiveStartFails++;
            WriteSupervisorLog($"start-core failed: {ex.Message}", AppLogLevel.Error);
            StopCore(); SystemProxy.Disable(); SetState(ConnectionState.Error, ex.Message);
        }
        finally { _gate.Release(); }
    }

    private async Task WriteCoreConfigAsync(ProxyProfile profile, string configPath)
    {
        var fingerprint = ConfigFingerprint(profile);
        var reuseConfig = fingerprint == _readyConfigFingerprint && File.Exists(configPath);
        if (reuseConfig) return;
        var routing = RoutingBundle.RefreshFromBundled(RoutingBundle.Resolve(_settings.ProfileRouting));
        var configJson = await Task.Run(() => SingBoxConfigBuilder.Build(profile, _settings.LocalHttpPort,
            _settings.UseTun, routing, _settings.BypassApplications, _settings.ActivePriority,
            _settings.CustomProxyDomains, _settings.CustomDirectDomains, _settings.UseFullBlockList,
            _settings.RouteExceptRussia, _settings.GeoUpdateDays, _settings.WhitelistMode,
            _settings.LogLevel, _settings.LastProxyOutbound,
            _settings.ClashApiPort)).ConfigureAwait(false);
        await File.WriteAllTextAsync(configPath, configJson).ConfigureAwait(false);
        _readyConfigFingerprint = fingerprint;
    }

    private void EnsureLocalPorts(bool skipCurrent = false)
    {
        var mixedPreferred = _settings.LocalHttpPort is > 1024 and < 65534 ? _settings.LocalHttpPort : 10809;
        var clashPreferred = _settings.ClashApiPort is > 1024 and < 65534 ? _settings.ClashApiPort : 19090;
        int[] skip = skipCurrent ? [mixedPreferred, clashPreferred] : [];
        var mixed = LocalPortGuard.Pick(mixedPreferred, skip);
        var clash = LocalPortGuard.Pick(clashPreferred, [.. skip, mixed]);
        if (mixed == _settings.LocalHttpPort && clash == clashPreferred) return;
        WriteSupervisorLog($"local-ports mixed={mixed} clash={clash} previous={_settings.LocalHttpPort}/{clashPreferred}", AppLogLevel.Warn);
        _settings.LocalHttpPort = mixed;
        _settings.ClashApiPort = clash;
        _settings.Save();
        _readyConfigFingerprint = "";
    }

    private bool IsLocalBindFailure()
    {
        var text = LastCoreError();
        return text.Contains("forbidden by its access permissions", StringComparison.OrdinalIgnoreCase)
            || text.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase)
            || text.Contains("bind", StringComparison.OrdinalIgnoreCase) && text.Contains("listen", StringComparison.OrdinalIgnoreCase);
    }

    private async Task LaunchCoreAsync(string corePath, string configPath)
    {
        var startInfo = new ProcessStartInfo(corePath, $"run -c \"{configPath}\"") {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(corePath)!,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        _core = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _core.OutputDataReceived += OnCoreLog; _core.ErrorDataReceived += OnCoreLog;
        _core.Start(); _core.BeginOutputReadLine(); _core.BeginErrorReadLine();
        _coreStartedAt = DateTimeOffset.Now;
        for (var i = 0; i < 80 && !await IsPortOpenAsync(); i++)
        {
            if (_core?.HasExited != false) break;
            await Task.Delay(250);
        }
    }

    private void OnCoreLog(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data)) return;
        LogStore.Instance.WriteCore(e.Data);
        NoteCoreError(e.Data);

        var match = Regex.Match(e.Data, @"\[(?:auto-proxy-in|chain-in-s\d+)\s*->\s*route-p0*(\d+)", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var priority)) return;
        _settings.ActivePriority = priority; _settings.Save();
    }

    private async Task WatchdogAsync()
    {
        if (!_settings.DesiredConnected || !_settings.AutoReconnect || _suspended) return;
        if (State is ConnectionState.Connecting or ConnectionState.Recovering) return;
        if (_gate.CurrentCount == 0) return;
        if (_lastStartAttempt > DateTimeOffset.MinValue)
        {
            var wait = State == ConnectionState.Error
                ? Math.Min(120, 20 * Math.Max(1, _consecutiveStartFails))
                : 25;
            if (DateTimeOffset.Now - _lastStartAttempt < TimeSpan.FromSeconds(wait)) return;
        }
        if (_connectedAt > DateTimeOffset.MinValue && DateTimeOffset.Now - _connectedAt < TimeSpan.FromSeconds(45)) return;
        if (await RefreshActivePriorityAsync())
        {
            WriteSupervisorLog($"watchdog-restart reason=priority-change route={ActiveRouteName} p={_settings.ActivePriority}", AppLogLevel.Warn);
            await DisconnectAsync(false);
            await EnsureConnectedAsync(true);
            return;
        }
        var processDead = _core is null || _core.HasExited || !await IsPortOpenAsync();
        var tunnelDead = !processDead && !await IsTunnelHealthyAsync();
        _failedHealthChecks = tunnelDead ? _failedHealthChecks + 1 : 0;
        if (processDead || _failedHealthChecks >= 5) {
            WriteSupervisorLog($"watchdog-restart reason={(processDead ? "process-or-port-dead" : "health-fail-x5")} failedHealth={_failedHealthChecks} pid={_core?.Id}",
                State == ConnectionState.Error ? AppLogLevel.Warn : AppLogLevel.Error);
            _failedHealthChecks = 0;
            await DisconnectAsync(false); await EnsureConnectedAsync(true);
        }
    }

    private async Task<bool> RefreshActivePriorityAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "lampa");
            var current = "proxy";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var depth = 0; depth < 16 && seen.Add(current); depth++)
            {
                var match = Regex.Match(current, @"(?:^|[^a-z0-9])p(\d+)(?=[^0-9]|$)", RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var priority))
                {
                    ActiveRouteName = current;
                    if (!string.Equals(_priorityCandidate, current, StringComparison.Ordinal))
                    {
                        _priorityCandidate = current;
                        _priorityConfirmations = 1;
                        return false;
                    }
                    if (++_priorityConfirmations < 2 || priority == _settings.ActivePriority)
                    {
                        RememberOutbound(current);
                        return false;
                    }
                    _settings.ActivePriority = priority;
                    RememberOutbound(current);
                    _settings.Save();
                    _readyConfigFingerprint = "";
                    return true;
                }

                var url = $"{ClashApiRoot}/proxies/{Uri.EscapeDataString(current)}";
                var json = JsonNode.Parse(await client.GetStringAsync(url)) as JsonObject;
                var next = json?["now"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(next) || string.Equals(next, current, StringComparison.Ordinal)) return false;
                current = next;
            }
        }
        catch { }
        return false;
    }

    private void OnSleepRequested(bool classicSuspend)
    {
        WriteSupervisorLog($"sleep-requested classic={classicSuspend} pauseOnSleep={_settings.PauseVpnOnSleep}", AppLogLevel.Info);
        if (!classicSuspend && !_settings.PauseVpnOnSleep) return;
        _ = PauseForSleepAsync(classicSuspend);
    }

    private async Task PauseForSleepAsync(bool classicSuspend)
    {
        Interlocked.Increment(ref _resumeGeneration);
        _suspended = true;
        await _gate.WaitAsync();
        try
        {
            // Kill the core instead of NtSuspendProcess: a frozen TUN keeps
            // WinTun/urltest runnable and blocks Modern Standby (S0ix).
            WriteSupervisorLog($"pause-for-sleep classic={classicSuspend} pid={_core?.Id}", AppLogLevel.Warn);
            StopCore();
            SystemProxy.Disable();
            if (_settings.DesiredConnected && !_disposed)
                SetState(ConnectionState.Paused, "VPN остановлен на время сна");
        }
        finally { _gate.Release(); }
    }

    private void OnWakeRequested()
    {
        WriteSupervisorLog($"wake-requested suspended={_suspended}", AppLogLevel.Info);
        if (!_suspended) return;
        _ = ResumeAsync(fromSleep: true);
    }

    private async Task ResumeAsync(bool fromSleep = false)
    {
        var generation = Interlocked.Increment(ref _resumeGeneration);
        if (!_settings.DesiredConnected || _disposed) return;
        if (!fromSleep && _suspended) return;
        SetState(ConnectionState.Recovering, fromSleep ? "Просыпаем VPN…" : "Ожидаем сеть…");
        if (fromSleep)
        {
            await _gate.WaitAsync();
            try
            {
                _suspended = false;
                _failedHealthChecks = 0;
            }
            finally { _gate.Release(); }
        }

        var waitSeconds = fromSleep ? 8 : 20;
        for (var i = 0; i < waitSeconds && !NetworkInterface.GetIsNetworkAvailable(); i++)
        {
            if (_suspended || _disposed || generation != _resumeGeneration) return;
            await Task.Delay(1000);
        }
        if (_suspended || _disposed || generation != _resumeGeneration) return;
        await Task.Delay(fromSleep ? 400 : 1500);
        if (_suspended || _disposed || generation != _resumeGeneration) return;
        await EnsureConnectedAsync(true);
    }

    private void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable || !_settings.DesiredConnected || _suspended) return;
        _ = ResumeAsync();
    }

    private async Task<bool> IsPortOpenAsync()
    {
        try { using var tcp = new TcpClient(); await tcp.ConnectAsync("127.0.0.1", _settings.LocalHttpPort).WaitAsync(TimeSpan.FromMilliseconds(500)); return true; }
        catch { return false; }
    }

    private async Task<bool> IsTunnelHealthyAsync()
    {
        try {
            // Оставляем health-check ровно как было:
            // gstatic + cloudflare generate_204 (destination прилетает из логики подписки).
            var healthUrls = new[] { "https://www.gstatic.com/generate_204" };

            using var handler = new SocketsHttpHandler {
                Proxy = new WebProxy($"http://127.0.0.1:{_settings.LocalHttpPort}"), UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(3), PooledConnectionLifetime = TimeSpan.Zero
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
            foreach (var url in healthUrls) {
                try {
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    if ((int)response.StatusCode is >= 200 and < 500) return true;
                } catch { }
            }
            return false;
        } catch { return false; }
    }

    private async Task ProbeUrlTestAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "lampa");
            var url = "https://www.gstatic.com/generate_204";
            using var delay = await client.GetAsync(
                $"{ClashApiRoot}/proxies/proxy/delay?timeout=5000&url={Uri.EscapeDataString(url)}");
            var json = JsonNode.Parse(await client.GetStringAsync($"{ClashApiRoot}/proxies/proxy")) as JsonObject;
            RememberOutbound(json?["now"]?.GetValue<string>());
        }
        catch (Exception ex)
        {
            WriteSupervisorLog($"urltest-probe failed: {ex.Message}", AppLogLevel.Info);
        }
    }

    private void RememberOutbound(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        if (tag is "proxy" or "direct" or "block") return;
        ActiveRouteName = tag;
        if (string.Equals(_settings.LastProxyOutbound, tag, StringComparison.OrdinalIgnoreCase)) return;
        _settings.LastProxyOutbound = tag;
        _settings.Save();
        WriteSupervisorLog($"remember-node {tag}", AppLogLevel.Info);
    }

    private void NoteCoreError(string line)
    {
        if (line.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) < 0 &&
            line.IndexOf("FATAL", StringComparison.OrdinalIgnoreCase) < 0 &&
            line.IndexOf("panic", StringComparison.OrdinalIgnoreCase) < 0)
            return;
        lock (_recentCoreErrors)
        {
            _recentCoreErrors.Add(line);
            if (_recentCoreErrors.Count > 16) _recentCoreErrors.RemoveAt(0);
        }
    }

    private void ClearCoreErrors()
    {
        lock (_recentCoreErrors) _recentCoreErrors.Clear();
    }

    private string LastCoreError()
    {
        lock (_recentCoreErrors) return _recentCoreErrors.Count == 0 ? "" : _recentCoreErrors[^1];
    }

    private string StartFailureMessage()
    {
        var detail = LastCoreError();
        if (string.IsNullOrWhiteSpace(detail)) return "VPN core не смог запуститься";
        var cut = detail.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase);
        if (cut >= 0) detail = detail[cut..];
        if (detail.Length > 180) detail = detail[..180];
        return $"VPN core не смог запуститься: {detail.Trim()}";
    }

    private string ConfigFingerprint(ProxyProfile profile) =>
        string.Join('|',
            _settings.SelectedProfile,
            profile.ConfigJson.Length,
            profile.Link,
            _settings.UseTun,
            _settings.LocalHttpPort,
            _settings.ActivePriority,
            _settings.UseFullBlockList,
            _settings.RouteExceptRussia,
            _settings.WhitelistMode,
            _settings.GeoUpdateDays,
            _settings.LogLevel,
            _settings.ClashApiPort,
            string.Join(',', _settings.BypassApplications),
            string.Join(',', _settings.CustomProxyDomains),
            string.Join(',', _settings.CustomDirectDomains));

    private void StopCore()
    {
        _connections.Stop();
        try { if (_core is { HasExited: false }) { _core.Kill(true); _core.WaitForExit(2000); } } catch { }
        _core?.Dispose(); _core = null;
        _coreStartedAt = DateTimeOffset.MinValue;
        _connectedAt = DateTimeOffset.MinValue;
    }

    private void StopStaleCoreProcesses(string corePath)
    {
        var expectedPath = Path.GetFullPath(corePath);
        var processName = Path.GetFileNameWithoutExtension(expectedPath);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (_core is not null && process.Id == _core.Id) continue;
                    var runningPath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(runningPath) ||
                        !Path.GetFullPath(runningPath).Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    process.Kill(true);
                    process.WaitForExit(3000);
                }
                catch
                {
                    // It may exit between enumeration and inspection. Never
                    // terminate a sing-box process from another application.
                }
            }
        }
    }
    private void SetState(ConnectionState state, string message) { State = state; StateChanged?.Invoke(state, message); }

    private static void WriteSupervisorLog(string message, AppLogLevel level = AppLogLevel.Info) =>
        LogStore.Instance.WriteApp(level, message);

    public void NotifyLogSettingsChanged()
    {
        LogStore.Instance.ApplySettings(_settings);
        _connections.RefreshMode();
    }
    public void Dispose()
    {
        _disposed = true;
        _sleep.SleepRequested -= OnSleepRequested;
        _sleep.WakeRequested -= OnWakeRequested;
        _sleep.Dispose();
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _watchdog.Dispose();
        _connections.Dispose();
        StopCore();
        _gate.Dispose();
    }
}
