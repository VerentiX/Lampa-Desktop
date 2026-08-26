using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.IO;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Http;
using Lampa.Desktop.Models;

namespace Lampa.Desktop.Services;

public enum ConnectionState { Disconnected, Connecting, Connected, Recovering, Paused, Error }

public sealed class ConnectionSupervisor : IDisposable
{
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Threading.Timer _watchdog;
    private readonly SleepPowerMonitor _sleep;
    private Process? _core;
    private DateTimeOffset _coreStartedAt = DateTimeOffset.MinValue;
    private volatile bool _suspended;
    private bool _disposed;
    private int _failedHealthChecks;
    private int _resumeGeneration;
    private DateTimeOffset _connectedAt = DateTimeOffset.MinValue;
    private bool _coreFrozen;
    private string _readyConfigFingerprint = "";
    public ConnectionState State { get; private set; }
    public event Action<ConnectionState, string>? StateChanged;

    public ConnectionSupervisor(AppSettings settings)
    {
        _settings = settings;
        _sleep = new SleepPowerMonitor();
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
            if (_coreFrozen) ThawCore();
            if (_core is { HasExited: false } && await IsPortOpenAsync()) {
                if (_settings.UseTun) SystemProxy.Disable(); else SystemProxy.Enable(_settings.LocalHttpPort);
                SetState(ConnectionState.Connected, _settings.UseTun ? "Весь трафик защищён через TUN" : "Соединение защищено"); return;
            }
            SetState(recovering ? ConnectionState.Recovering : ConnectionState.Connecting, recovering ? "Восстанавливаем соединение…" : "Подключаемся…");
            await Task.Yield();
            StopCore(); SystemProxy.Disable();
            var profile = _settings.Profiles.ElementAtOrDefault(_settings.SelectedProfile) ?? throw new InvalidOperationException("Добавьте подписку и выберите сервер");
            var corePath = Path.GetFullPath(Path.IsPathRooted(_settings.CorePath) ? _settings.CorePath : Path.Combine(AppContext.BaseDirectory, _settings.CorePath));
            if (!File.Exists(corePath)) throw new FileNotFoundException("Компонент подключения отсутствует. Переустановите Lampa VPN.", corePath);
            Directory.CreateDirectory(AppSettings.DataDirectory);
            var assetDirectory = Path.Combine(AppSettings.DataDirectory, "assets");
            EnsureAssetFiles(assetDirectory);
            var configPath = Path.Combine(AppSettings.DataDirectory, "config.json");
            var fingerprint = ConfigFingerprint(profile);
            var reuseConfig = fingerprint == _readyConfigFingerprint && File.Exists(configPath);
            if (!reuseConfig)
            {
                var routing = RoutingBundle.RefreshFromBundled(RoutingBundle.Resolve(_settings.ProfileRouting));
                var configJson = await Task.Run(() => CoreConfigBuilder.Build(profile, _settings.LocalHttpPort,
                    _settings.UseTun, routing, _settings.BypassApplications, _settings.ActivePriority,
                    _settings.CustomProxyDomains, _settings.CustomDirectDomains, _settings.UseFullBlockList)).ConfigureAwait(false);
                await File.WriteAllTextAsync(configPath, configJson).ConfigureAwait(false);
                _readyConfigFingerprint = fingerprint;
            }
            var startInfo = new ProcessStartInfo(corePath, $"run -c \"{configPath}\"") {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(corePath)!,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            startInfo.Environment["XRAY_LOCATION_ASSET"] = assetDirectory;
            _core = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _core.OutputDataReceived += OnCoreLog; _core.ErrorDataReceived += OnCoreLog;
            _core.Start(); _core.BeginOutputReadLine(); _core.BeginErrorReadLine();
            // Xray иногда поднимает inbound-порт чуть позже TUN/driver-инициализации.
            // Даем более щедрое время, чтобы не уходить в перезапуск-цикл.
            _coreStartedAt = DateTimeOffset.Now;
            for (var i = 0; i < 60 && !await IsPortOpenAsync(); i++) { if (_core?.HasExited != false) break; await Task.Delay(250); }
            if (!await IsPortOpenAsync()) throw new InvalidOperationException("VPN core не смог запуститься");
            if (_settings.UseTun) SystemProxy.Disable(); else SystemProxy.Enable(_settings.LocalHttpPort);
            _connectedAt = DateTimeOffset.Now;
            _failedHealthChecks = 0;
            SetState(ConnectionState.Connected, recovering ? "TUN-соединение восстановлено" : "Весь трафик защищён через TUN");
        }
        catch (Exception ex) { StopCore(); SystemProxy.Disable(); SetState(ConnectionState.Error, ex.Message); }
        finally { _gate.Release(); }
    }

    private static void EnsureAssetFiles(string assetDirectory)
    {
        Directory.CreateDirectory(assetDirectory);
        GeoAssetService.EnsureCompatAssets(assetDirectory);
        GeoAssetService.CopyBundledPrimary(assetDirectory);

        if (!GeoAssetService.IsPrimaryGeoSiteValid(Path.Combine(assetDirectory, "geosite.dat")) ||
            !GeoAssetService.IsPrimaryGeoIpValid(Path.Combine(assetDirectory, "geoip.dat")))
            throw new InvalidOperationException("Не найдены geo-базы маршрутизации. Переустановите Lampa VPN.");
        if (!File.Exists(Path.Combine(assetDirectory, "geosite-compat.dat")) || !File.Exists(Path.Combine(assetDirectory, "geoip-compat.dat")))
            throw new InvalidOperationException("Не найдены compat-базы маршрутизации. Переустановите Lampa VPN.");
    }

    private void OnCoreLog(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data)) return;
        try
        {
            var logPath = Path.Combine(AppSettings.DataDirectory, "core.log");
            File.AppendAllText(logPath, e.Data + Environment.NewLine);
        }
        catch { }

        var match = Regex.Match(e.Data, @"\[(?:auto-proxy-in|chain-in-s\d+)\s*->\s*route-p0*(\d+)", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var priority)) return;
        _settings.ActivePriority = priority; _settings.Save();
    }

    private async Task WatchdogAsync()
    {
        if (!_settings.DesiredConnected || !_settings.AutoReconnect || _suspended || _coreFrozen) return;
        // Грейс-период после запуска core, чтобы watchdog не рестартил Xray
        // пока он ещё "прогревается" (инициализация tun/обмен с сетью).
        if (_coreStartedAt > DateTimeOffset.MinValue &&
            DateTimeOffset.Now - _coreStartedAt < TimeSpan.FromSeconds(15)) return;
        if (_connectedAt > DateTimeOffset.MinValue && DateTimeOffset.Now - _connectedAt < TimeSpan.FromSeconds(45)) return;
        var processDead = _core is null || _core.HasExited || !await IsPortOpenAsync();
        var tunnelDead = !processDead && !await IsTunnelHealthyAsync();
        _failedHealthChecks = tunnelDead ? _failedHealthChecks + 1 : 0;
        if (processDead || _failedHealthChecks >= 5) {
            _failedHealthChecks = 0;
            await DisconnectAsync(false); await EnsureConnectedAsync(true);
        }
    }

    private void OnSleepRequested(bool classicSuspend)
    {
        if (!classicSuspend && !_settings.PauseVpnOnSleep) return;
        _ = PauseForSleepAsync();
    }

    private async Task PauseForSleepAsync()
    {
        Interlocked.Increment(ref _resumeGeneration);
        _suspended = true;
        await _gate.WaitAsync();
        try
        {
            if (!FreezeCore()) StopCore();
            SystemProxy.Disable();
            if (_settings.DesiredConnected && !_disposed)
                SetState(ConnectionState.Paused, "VPN усыплён, правила остаются в памяти");
        }
        finally { _gate.Release(); }
    }

    private void OnWakeRequested()
    {
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
                ThawCore();
                _suspended = false;
                _coreStartedAt = DateTimeOffset.Now;
                _connectedAt = DateTimeOffset.Now;
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

        if (fromSleep && _core is { HasExited: false })
        {
            for (var i = 0; i < 20; i++)
            {
                if (_suspended || _disposed || generation != _resumeGeneration) return;
                if (await IsPortOpenAsync())
                {
                    if (_settings.UseTun) SystemProxy.Disable(); else SystemProxy.Enable(_settings.LocalHttpPort);
                    _coreStartedAt = DateTimeOffset.Now;
                    _connectedAt = DateTimeOffset.Now;
                    _failedHealthChecks = 0;
                    SetState(ConnectionState.Connected, "VPN проснулся");
                    return;
                }
                await Task.Delay(150);
            }
        }
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

    private string ConfigFingerprint(ProxyProfile profile) =>
        string.Join('|',
            _settings.SelectedProfile,
            profile.ConfigJson.Length,
            profile.Link,
            _settings.UseTun,
            _settings.LocalHttpPort,
            _settings.ActivePriority,
            _settings.UseFullBlockList,
            string.Join(',', _settings.BypassApplications),
            string.Join(',', _settings.CustomProxyDomains),
            string.Join(',', _settings.CustomDirectDomains));

    private bool FreezeCore()
    {
        if (_core is null || _core.HasExited) return false;
        if (_coreFrozen) return true;
        if (!ProcessSuspender.Suspend(_core.Id)) return false;
        _coreFrozen = true;
        return true;
    }

    private void ThawCore()
    {
        if (!_coreFrozen) return;
        try
        {
            if (_core is { HasExited: false })
                ProcessSuspender.Resume(_core.Id);
        }
        catch { }
        _coreFrozen = false;
    }

    private void StopCore()
    {
        ThawCore();
        try { if (_core is { HasExited: false }) { _core.Kill(true); _core.WaitForExit(2000); } } catch { }
        _core?.Dispose(); _core = null;
        _coreStartedAt = DateTimeOffset.MinValue;
        _connectedAt = DateTimeOffset.MinValue;
    }
    private void SetState(ConnectionState state, string message) { State = state; StateChanged?.Invoke(state, message); }
    public void Dispose()
    {
        _disposed = true;
        _sleep.SleepRequested -= OnSleepRequested;
        _sleep.WakeRequested -= OnWakeRequested;
        _sleep.Dispose();
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _watchdog.Dispose();
        StopCore();
        _gate.Dispose();
    }
}
