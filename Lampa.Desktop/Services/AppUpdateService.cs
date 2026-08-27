using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using Lampa.Desktop.Models;

namespace Lampa.Desktop.Services;

public enum AppUpdateUiState { Hidden, Downloading, Ready }

public sealed record AppUpdateUi(AppUpdateUiState State, string Message, double Percent, bool CanInstall);

/// <summary>
/// Checks GitHub Releases (with the legacy site API as a fallback), downloads
/// with HTTP Range resume, and keeps a thin UI state for the main window.
/// Expected site payload (prepare this before shipping the installer):
/// {
///   "ok": true, "tag": "v1.0.1", "name": "Lampa 1.0.1",
///   "downloadUrl": "/download/windows",
///   "windows": { "name": "LampaSetup.exe", "url": "/download/windows", "size": 0, "arch": "x64", "sha256": "" },
///   "assets": [{ "name": "LampaSetup.exe", "url": "/download/windows", "size": 0, "arch": "x64", "platform": "windows" }]
/// }
/// APK-only responses are ignored.
/// </summary>
public sealed class AppUpdateService : IDisposable
{
    private readonly HttpClient _checkHttp = CreateHttp(TimeSpan.FromSeconds(12));
    private readonly HttpClient _downloadHttp = CreateHttp(Timeout.InfiniteTimeSpan);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private DateTimeOffset _lastUi = DateTimeOffset.MinValue;
    private Task? _downloadTask;

    public event Action<AppUpdateUi>? ProgressChanged;

    public static string CurrentVersion
    {
        get
        {
            var asm = typeof(AppUpdateService).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info)) return info.Split('+')[0].Trim().TrimStart('v', 'V');
            return asm.GetName().Version?.ToString(3) ?? "1.0.0";
        }
    }

    public static string UpdatesDirectory => Path.Combine(AppSettings.DataDirectory, "updates");

    public async Task CheckAndContinueAsync(AppSettings settings, bool ignoreInterval, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var ct = linked.Token;
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            if (TryRestoreReady(settings) is { } ready)
            {
                Report(new AppUpdateUi(AppUpdateUiState.Ready, $"Версия {ready.Version} готова", 100, true));
                return;
            }

            if (HasPendingDownload(settings))
            {
                StartDownload(settings);
                return;
            }

            var days = Math.Clamp(settings.AppUpdateDays, 3, 30);
            if (!ignoreInterval && settings.LastAppUpdateCheck is { } last &&
                DateTimeOffset.Now - last < TimeSpan.FromDays(days))
                return;

            var release = await FindWindowsReleaseAsync(ct);
            settings.LastAppUpdateCheck = DateTimeOffset.Now;
            settings.Save();
            if (release is null) return;

            settings.PendingUpdateVersion = release.Version;
            settings.PendingUpdateUrl = release.Url;
            settings.PendingUpdateSize = release.Size;
            settings.PendingUpdateSha256 = release.Sha256;
            settings.PendingUpdateStatus = "downloading";
            settings.Save();
            StartDownload(settings);
        }
        catch
        {
            // Stay quiet: next timer tick retries.
        }
        finally
        {
            _gate.Release();
        }
    }

    public AppUpdateUi CurrentUi(AppSettings settings)
    {
        if (TryRestoreReady(settings) is { } ready)
            return new AppUpdateUi(AppUpdateUiState.Ready, $"Версия {ready.Version} готова", 100, true);
        if (HasPendingDownload(settings))
        {
            var percent = ProgressPercent(settings);
            return new AppUpdateUi(AppUpdateUiState.Downloading, $"Скачиваем обновление {percent:0}%", percent, false);
        }
        return new AppUpdateUi(AppUpdateUiState.Hidden, "", 0, false);
    }

    public string? ReadyInstallerPath(AppSettings settings) => TryRestoreReady(settings)?.Path;

    public static void LaunchInstaller(string path)
    {
        try { File.Delete(path + ":Zone.Identifier"); } catch { /* optional MOTW strip */ }
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            Arguments = "/SILENT /CLOSEAPPLICATIONS /NORESTART",
            UseShellExecute = true,
        });
    }

    public void Dispose()
    {
        try { _lifetime.Cancel(); } catch { }
        _lifetime.Dispose();
        _gate.Dispose();
    }

    private void StartDownload(AppSettings settings)
    {
        if (_downloadTask is { IsCompleted: false }) return;
        var version = settings.PendingUpdateVersion;
        var url = settings.PendingUpdateUrl;
        var size = settings.PendingUpdateSize;
        var sha = settings.PendingUpdateSha256;
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(url)) return;
        _downloadTask = Task.Run(() => DownloadLoopAsync(settings, version, url, size, sha, _lifetime.Token));
    }

    private async Task DownloadLoopAsync(AppSettings settings, string version, string url, long expectedSize, string sha256, CancellationToken token)
    {
        var dest = InstallerPath(version);
        var part = dest + ".part";
        Directory.CreateDirectory(UpdatesDirectory);
        var delay = TimeSpan.FromSeconds(2);
        Report(new AppUpdateUi(AppUpdateUiState.Downloading, "Скачиваем обновление…", ProgressPercent(part, expectedSize), false));

        while (!token.IsCancellationRequested)
        {
            try
            {
                var existing = File.Exists(part) ? new FileInfo(part).Length : 0L;
                if (File.Exists(dest) && IsComplete(dest, expectedSize, sha256))
                {
                    MarkReady(settings, version);
                    Report(new AppUpdateUi(AppUpdateUiState.Ready, $"Версия {version} готова", 100, true));
                    return;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, url) { Version = HttpVersion.Version11 };
                ApplyDesktopHeaders(request);
                if (existing > 0)
                    request.Headers.Range = new RangeHeaderValue(existing, null);

                using var response = await _downloadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    if (existing > 0 && (expectedSize <= 0 || existing >= expectedSize))
                    {
                        FinishPart(part, dest, expectedSize, sha256);
                        MarkReady(settings, version);
                        Report(new AppUpdateUi(AppUpdateUiState.Ready, $"Версия {version} готова", 100, true));
                        return;
                    }
                    try { File.Delete(part); } catch { }
                    existing = 0;
                    continue;
                }

                if (response.StatusCode != HttpStatusCode.PartialContent && existing > 0)
                {
                    try { File.Delete(part); } catch { }
                    existing = 0;
                }

                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");

                var total = response.Content.Headers.ContentRange?.Length
                    ?? (response.Content.Headers.ContentLength is { } len ? existing + len : expectedSize);
                if (total > 0 && expectedSize <= 0) expectedSize = total;

                var mode = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent ? FileMode.Append : FileMode.Create;
                await using (var fs = new FileStream(part, mode, FileAccess.Write, FileShare.Read, 64 * 1024, true))
                await using (var input = await response.Content.ReadAsStreamAsync(token))
                {
                    var buffer = new byte[64 * 1024];
                    var written = existing;
                    int read;
                    while ((read = await input.ReadAsync(buffer, token)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, read), token);
                        written += read;
                        MaybeReport(written, total > 0 ? total : expectedSize);
                    }
                    await fs.FlushAsync(token);
                }

                var have = File.Exists(part) ? new FileInfo(part).Length : 0L;
                if (expectedSize > 0 && have < expectedSize)
                {
                    delay = TimeSpan.FromSeconds(2);
                    continue;
                }

                FinishPart(part, dest, expectedSize, sha256);
                MarkReady(settings, version);
                Report(new AppUpdateUi(AppUpdateUiState.Ready, $"Версия {version} готова", 100, true));
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                try { await Task.Delay(delay, token); } catch (OperationCanceledException) { return; }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
        }
    }

    private async Task<WindowsRelease?> FindWindowsReleaseAsync(CancellationToken token)
    {
        foreach (var api in AppEndpoints.AppLatestCheckUrls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, api);
                ApplyDesktopHeaders(request);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await _checkHttp.SendAsync(request, token);
                if (!response.IsSuccessStatusCode) continue;
                var json = await response.Content.ReadAsStringAsync(token);
                var release = api.Equals(AppEndpoints.GitHubLatestReleaseApi, StringComparison.OrdinalIgnoreCase)
                    ? ParseGitHubRelease(json)
                    : ParseSiteRelease(json);
                if (release is not null) return release;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch { /* next URL */ }
        }
        return null;
    }

    internal static WindowsRelease? ParseGitHubRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True) return null;

        var version = ReadString(root, "tag_name", "name").Trim().TrimStart('v', 'V');
        if (version.Length == 0 || CompareVersions(version, CurrentVersion) <= 0) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        WindowsAsset? selected = null;
        foreach (var item in assets.EnumerateArray())
        {
            var asset = ReadAsset(item);
            if (asset is null || !LooksLikeWindowsName(asset.Name)) continue;
            if (asset.Name.Equals("LampaSetup.exe", StringComparison.OrdinalIgnoreCase))
            {
                selected = asset;
                break;
            }
            if (asset.Name.Contains("setup", StringComparison.OrdinalIgnoreCase)) selected ??= asset;
        }

        return selected is null
            ? null
            : new WindowsRelease(version, selected.Url, selected.Size, selected.Sha256);
    }

    internal static WindowsRelease? ParseSiteRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False) return null;

        var tag = ReadString(root, "tag", "tag_name", "version");
        var version = tag.Trim().TrimStart('v', 'V');
        if (version.Length == 0 || CompareVersions(version, CurrentVersion) <= 0) return null;

        var asset = PickWindowsAsset(root);
        if (asset is null) return null;
        var url = ResolveUrl(asset.Url);
        if (url.Length == 0) return null;
        return new WindowsRelease(version, url, asset.Size, asset.Sha256);
    }

    private static WindowsAsset? PickWindowsAsset(JsonElement root)
    {
        foreach (var key in new[] { "windows", "desktop", "setup", "exe" })
        {
            if (root.TryGetProperty(key, out var node) && node.ValueKind == JsonValueKind.Object)
            {
                var asset = ReadAsset(node);
                if (asset is not null && IsWindowsAsset(asset)) return asset;
            }
        }

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            WindowsAsset? fallback = null;
            foreach (var item in assets.EnumerateArray())
            {
                var asset = ReadAsset(item);
                if (asset is null || !IsWindowsAsset(asset)) continue;
                if (asset.Name.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
                    asset.Arch.Contains("x64", StringComparison.OrdinalIgnoreCase))
                    return asset;
                fallback ??= asset;
            }
            if (fallback is not null) return fallback;
        }

        var downloadUrl = ReadString(root, "downloadUrl", "download_url");
        if (LooksLikeWindowsUrl(downloadUrl) || LooksLikeWindowsName(downloadUrl))
            return new WindowsAsset(Path.GetFileName(downloadUrl), downloadUrl, 0, "", "x64", "windows");
        return null;
    }

    private static WindowsAsset? ReadAsset(JsonElement node)
    {
        var name = ReadString(node, "name");
        // GitHub assets contain both an authenticated API URL (`url`) and the
        // public installer URL (`browser_download_url`). Prefer the latter.
        var url = ReadString(node, "browser_download_url", "url", "downloadUrl");
        if (url.Length == 0) return null;
        var size = 0L;
        if (node.TryGetProperty("size", out var sizeNode) && sizeNode.TryGetInt64(out var parsed)) size = parsed;
        var sha = ReadString(node, "digest", "sha256", "sha", "hash");
        var arch = ReadString(node, "arch");
        var platform = ReadString(node, "platform", "os");
        return new WindowsAsset(name, url, size, sha, arch, platform);
    }

    private static bool IsWindowsAsset(WindowsAsset asset)
    {
        if (LooksLikeAndroid(asset.Name) || LooksLikeAndroid(asset.Url) || LooksLikeAndroid(asset.Arch) || LooksLikeAndroid(asset.Platform))
            return false;
        return LooksLikeWindowsName(asset.Name)
            || LooksLikeWindowsUrl(asset.Url)
            || IsWindowsPlatform(asset.Platform)
            || IsWindowsArch(asset.Arch);
    }

    private static bool LooksLikeAndroid(string value) =>
        value.Contains(".apk", StringComparison.OrdinalIgnoreCase)
        || value.Contains("android", StringComparison.OrdinalIgnoreCase)
        || value.Contains("arm64", StringComparison.OrdinalIgnoreCase)
        || value.Contains("armeabi", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeWindowsName(string value) =>
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        || value.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
        || value.Contains("LampaSetup", StringComparison.OrdinalIgnoreCase)
        || value.Contains("setup.exe", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeWindowsUrl(string value) =>
        value.Contains("/download/windows", StringComparison.OrdinalIgnoreCase)
        || value.Contains("/download/desktop", StringComparison.OrdinalIgnoreCase)
        || value.Contains("platform=windows", StringComparison.OrdinalIgnoreCase)
        || value.Contains("arch=x64", StringComparison.OrdinalIgnoreCase);

    private static bool IsWindowsPlatform(string value) =>
        value.Equals("windows", StringComparison.OrdinalIgnoreCase)
        || value.Equals("win", StringComparison.OrdinalIgnoreCase)
        || value.Equals("win32", StringComparison.OrdinalIgnoreCase)
        || value.Equals("desktop", StringComparison.OrdinalIgnoreCase);

    private static bool IsWindowsArch(string value) =>
        value.Equals("x64", StringComparison.OrdinalIgnoreCase)
        || value.Equals("win-x64", StringComparison.OrdinalIgnoreCase)
        || value.Equals("amd64", StringComparison.OrdinalIgnoreCase);

    private static string ResolveUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;
        if (url.StartsWith('/')) return AppEndpoints.SiteUrl + url;
        return AppEndpoints.SiteUrl + "/" + url.TrimStart('/');
    }

    internal static int CompareVersions(string version1, string version2)
    {
        static int[] Parts(string value) =>
            value.Split('.', '-', '+')
                .Select(part => int.TryParse(new string(part.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0)
                .DefaultIfEmpty(0)
                .ToArray();

        var v1 = Parts(version1);
        var v2 = Parts(version2);
        for (var i = 0; i < Math.Max(v1.Length, v2.Length); i++)
        {
            var a = i < v1.Length ? v1[i] : 0;
            var b = i < v2.Length ? v2[i] : 0;
            if (a != b) return a.CompareTo(b);
        }
        return 0;
    }

    private ReadyUpdate? TryRestoreReady(AppSettings settings)
    {
        var version = settings.PendingUpdateVersion;
        if (string.IsNullOrWhiteSpace(version) || CompareVersions(version, CurrentVersion) <= 0)
        {
            if (!string.IsNullOrWhiteSpace(version) && CompareVersions(version, CurrentVersion) <= 0)
                ClearPending(settings);
            return null;
        }
        var path = InstallerPath(version);
        if (!File.Exists(path) || !IsComplete(path, settings.PendingUpdateSize, settings.PendingUpdateSha256))
            return null;
        if (!string.Equals(settings.PendingUpdateStatus, "ready", StringComparison.OrdinalIgnoreCase))
        {
            settings.PendingUpdateStatus = "ready";
            settings.Save();
        }
        return new ReadyUpdate(version, path);
    }

    private static bool HasPendingDownload(AppSettings settings)
    {
        var version = settings.PendingUpdateVersion;
        var url = settings.PendingUpdateUrl;
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(url)) return false;
        if (CompareVersions(version, CurrentVersion) <= 0) return false;
        return !string.Equals(settings.PendingUpdateStatus, "ready", StringComparison.OrdinalIgnoreCase);
    }

    private static void ClearPending(AppSettings settings)
    {
        settings.PendingUpdateVersion = "";
        settings.PendingUpdateUrl = "";
        settings.PendingUpdateSize = 0;
        settings.PendingUpdateSha256 = "";
        settings.PendingUpdateStatus = "";
        settings.Save();
    }

    private static void MarkReady(AppSettings settings, string version)
    {
        settings.PendingUpdateVersion = version;
        settings.PendingUpdateStatus = "ready";
        settings.Save();
        CleanupOldInstallers(version);
    }

    private static void CleanupOldInstallers(string keepVersion)
    {
        try
        {
            var keep = Path.GetFileName(InstallerPath(keepVersion));
            foreach (var file in Directory.EnumerateFiles(UpdatesDirectory))
            {
                var name = Path.GetFileName(file);
                if (!name.Equals(keep, StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals(keep + ".part", StringComparison.OrdinalIgnoreCase))
                    File.Delete(file);
            }
        }
        catch { }
    }

    private static void FinishPart(string part, string dest, long expectedSize, string sha256)
    {
        if (!File.Exists(part)) throw new IOException("Файл обновления не найден");
        if (!IsComplete(part, expectedSize, sha256))
        {
            try { File.Delete(part); } catch { }
            throw new IOException("Файл обновления повреждён");
        }
        File.Move(part, dest, true);
        try { File.Delete(dest + ":Zone.Identifier"); } catch { }
    }

    private static bool IsComplete(string path, long expectedSize, string sha256)
    {
        if (!File.Exists(path)) return false;
        var length = new FileInfo(path).Length;
        if (length < 1_000_000) return false;
        if (expectedSize > 0 && length != expectedSize) return false;
        if (string.IsNullOrWhiteSpace(sha256)) return true;
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        var hash = Convert.ToHexString(sha.ComputeHash(fs));
        return hash.Equals(sha256.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase).Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string InstallerPath(string version)
    {
        var safe = string.Concat(version.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_'));
        return Path.Combine(UpdatesDirectory, $"LampaSetup-{safe}.exe");
    }

    private static double ProgressPercent(AppSettings settings)
    {
        var part = InstallerPath(settings.PendingUpdateVersion) + ".part";
        return ProgressPercent(part, settings.PendingUpdateSize);
    }

    private static double ProgressPercent(string part, long expectedSize)
    {
        if (expectedSize <= 0 || !File.Exists(part)) return 0;
        return Math.Clamp(new FileInfo(part).Length * 100.0 / expectedSize, 0, 99);
    }

    private void MaybeReport(long written, long total)
    {
        if (DateTimeOffset.Now - _lastUi < TimeSpan.FromMilliseconds(250)) return;
        _lastUi = DateTimeOffset.Now;
        var percent = total > 0 ? Math.Clamp(written * 100.0 / total, 0, 99) : 0;
        Report(new AppUpdateUi(AppUpdateUiState.Downloading, $"Скачиваем обновление {percent:0}%", percent, false));
    }

    private void Report(AppUpdateUi ui)
    {
        try { ProgressChanged?.Invoke(ui); } catch { }
    }

    private static void ApplyDesktopHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd($"Lampa-Desktop/{CurrentVersion}");
        request.Headers.TryAddWithoutValidation("X-Lampa-Client", "desktop");
        request.Headers.TryAddWithoutValidation("X-Lampa-Platform", "windows");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    }

    private static string ReadString(JsonElement node, params string[] names)
    {
        foreach (var name in names)
        {
            if (node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";
        }
        return "";
    }

    private static HttpClient CreateHttp(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(12),
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    internal sealed record WindowsRelease(string Version, string Url, long Size, string Sha256);
    private sealed record WindowsAsset(string Name, string Url, long Size, string Sha256, string Arch, string Platform);
    private sealed record ReadyUpdate(string Version, string Path);
}
