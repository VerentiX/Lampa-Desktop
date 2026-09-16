using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Lampa.Desktop.Models;

namespace Lampa.Desktop.Services;

public enum AppLogLevel
{
    None = 0,
    Error = 1,
    Warn = 2,
    Info = 3,
    Debug = 4
}

public sealed class LogEntry
{
    public required DateTimeOffset Time { get; init; }
    public required AppLogLevel Level { get; init; }
    public required string Source { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// Bounded, batched log writer. The core stdout thread never blocks: overflow
/// drops the oldest queued line. Disk flushes are coalesced so idle laptops
/// are not kept awake by per-line WriteThrough I/O.
/// </summary>
public sealed class LogStore : IDisposable
{
    public const int UiCap = 1200;
    private const int MaxFileBytes = 20 * 1024 * 1024;
    private const int MaxFolderBytes = 150 * 1024 * 1024;
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;]*m", RegexOptions.Compiled);
    private static readonly Regex CoreStamp = new(
        @"^(?:\+\d{4}\s+)?(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})\s+(TRACE|DEBUG|INFO|WARN|WARNING|ERROR|FATAL|PANIC)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static LogStore Instance { get; } = new();

    private readonly Channel<LogEntry> _queue = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(4096)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest
    });
    private readonly ConcurrentQueue<LogEntry> _ui = new();
    private readonly object _fileGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private StreamWriter? _coreWriter;
    private StreamWriter? _appWriter;
    private StreamWriter? _accessWriter;
    private DateTime _writerDay = DateTime.MinValue.Date;
    private int _uiCount;
    private int _listeners;
    private AppLogLevel _level = AppLogLevel.Warn;
    private int _retentionHours = 72;
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    public event Action<LogEntry>? Appended;
    public string DirectoryPath => Path.Combine(AppSettings.DataDirectory, "logs");

    private LogStore()
    {
        Directory.CreateDirectory(DirectoryPath);
        _ = PumpAsync();
    }

    public void ApplySettings(AppSettings settings)
    {
        _level = ParseLevel(settings.LogLevel);
        _retentionHours = settings.LogRetentionHours is 1 or 6 or 24 or 72 or 168 or 720
            ? settings.LogRetentionHours
            : 72;
    }

    public static AppLogLevel ParseLevel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "none" => AppLogLevel.None,
        "warn" or "warning" => AppLogLevel.Warn,
        "info" => AppLogLevel.Info,
        "debug" or "trace" => AppLogLevel.Debug,
        _ => AppLogLevel.Error
    };

    public static string ToSingBoxLevel(string? value) => ParseLevel(value) switch
    {
        AppLogLevel.None => "panic",
        AppLogLevel.Warn => "warn",
        AppLogLevel.Info => "info",
        AppLogLevel.Debug => "debug",
        _ => "error"
    };

    public static string RetentionLabel(int hours) => hours switch
    {
        1 => "1 час",
        6 => "6 часов",
        24 => "1 день",
        72 => "3 дня",
        720 => "30 дней",
        _ => "7 дней"
    };

    public void WriteCore(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || _level == AppLogLevel.None) return;
        var clean = Ansi.Replace(raw, "").Trim();
        if (clean.Length == 0) return;
        var level = AppLogLevel.Info;
        var time = DateTimeOffset.Now;
        var message = clean;
        var match = CoreStamp.Match(clean);
        if (match.Success)
        {
            if (DateTimeOffset.TryParse(match.Groups[1].Value, out var parsed)) time = parsed;
            level = ParseLevel(match.Groups[2].Value);
            message = clean[match.Length..].Trim();
        }
        else if (clean.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                 clean.Contains("FATAL", StringComparison.OrdinalIgnoreCase))
            level = AppLogLevel.Error;
        else if (clean.Contains("WARN", StringComparison.OrdinalIgnoreCase))
            level = AppLogLevel.Warn;

        if (level > _level) return;
        Enqueue(new LogEntry { Time = time, Level = level, Source = "core", Message = message });
    }

    public void WriteApp(AppLogLevel level, string message)
    {
        if (_level == AppLogLevel.None || level > _level || string.IsNullOrWhiteSpace(message)) return;
        Enqueue(new LogEntry { Time = DateTimeOffset.Now, Level = level, Source = "app", Message = message.Trim() });
    }

    public void WriteAccess(string message)
    {
        if (_level == AppLogLevel.None || string.IsNullOrWhiteSpace(message)) return;
        Enqueue(new LogEntry { Time = DateTimeOffset.Now, Level = AppLogLevel.Info, Source = "access", Message = message.Trim() });
    }

    public IReadOnlyList<LogEntry> Snapshot() => _ui.ToArray();

    public void AddListener() => Interlocked.Increment(ref _listeners);
    public void RemoveListener() => Interlocked.Decrement(ref _listeners);

    public string ExportZip()
    {
        Flush();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var zipPath = Path.Combine(Path.GetTempPath(), $"lampa-logs-{stamp}.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        Directory.CreateDirectory(DirectoryPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.log"))
        {
            try { zip.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.SmallestSize); }
            catch { }
        }
        return zipPath;
    }

    public void Flush()
    {
        lock (_fileGate)
        {
            try { _coreWriter?.Flush(); } catch { }
            try { _appWriter?.Flush(); } catch { }
            try { _accessWriter?.Flush(); } catch { }
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _queue.Writer.TryComplete();
        Flush();
        lock (_fileGate)
        {
            _coreWriter?.Dispose();
            _appWriter?.Dispose();
            _accessWriter?.Dispose();
            _coreWriter = _appWriter = _accessWriter = null;
        }
        _lifetime.Dispose();
    }

    private void Enqueue(LogEntry entry) => _queue.Writer.TryWrite(entry);

    private async Task PumpAsync()
    {
        var pending = new List<LogEntry>(64);
        var reader = _queue.Reader;
        var lastFlush = Environment.TickCount64;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                pending.Clear();
                if (!await reader.WaitToReadAsync(_lifetime.Token).ConfigureAwait(false)) break;
                while (reader.TryRead(out var item)) pending.Add(item);
                foreach (var entry in pending) Accept(entry);
                WriteBatch(pending);
                if (Environment.TickCount64 - lastFlush >= 750)
                {
                    Flush();
                    lastFlush = Environment.TickCount64;
                    MaybePrune();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        Flush();
    }

    private void Accept(LogEntry entry)
    {
        _ui.Enqueue(entry);
        if (Interlocked.Increment(ref _uiCount) > UiCap && _ui.TryDequeue(out _))
            Interlocked.Decrement(ref _uiCount);
        if (Volatile.Read(ref _listeners) > 0)
        {
            try { Appended?.Invoke(entry); } catch { }
        }
    }

    private void WriteBatch(List<LogEntry> batch)
    {
        if (batch.Count == 0) return;
        lock (_fileGate)
        {
            EnsureWriters();
            foreach (var entry in batch)
            {
                var line = $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} {entry.Level.ToString().ToUpperInvariant(),-5} {entry.Message}";
                try
                {
                    var writer = entry.Source switch
                    {
                        "access" => _accessWriter,
                        "app" => _appWriter,
                        _ => _coreWriter
                    };
                    writer?.WriteLine(line);
                }
                catch { }
            }
        }
    }

    private void EnsureWriters()
    {
        var today = DateTime.Now.Date;
        if (_coreWriter is not null && _writerDay == today) return;
        _coreWriter?.Dispose();
        _appWriter?.Dispose();
        _accessWriter?.Dispose();
        Directory.CreateDirectory(DirectoryPath);
        _coreWriter = OpenWriter("core");
        _appWriter = OpenWriter("app");
        _accessWriter = OpenWriter("access");
        _writerDay = today;
    }

    private StreamWriter OpenWriter(string prefix)
    {
        var path = Path.Combine(DirectoryPath, $"{prefix}-{DateTime.Now:yyyy-MM-dd}.log");
        if (File.Exists(path) && new FileInfo(path).Length >= MaxFileBytes)
            path = Path.Combine(DirectoryPath, $"{prefix}-{DateTime.Now:yyyy-MM-dd}-{DateTime.Now:HHmmss}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
        return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
    }

    private void MaybePrune()
    {
        if (DateTimeOffset.Now - _lastPrune < TimeSpan.FromMinutes(30)) return;
        _lastPrune = DateTimeOffset.Now;
        try
        {
            var cutoff = DateTime.Now.AddHours(-Math.Max(1, _retentionHours));
            var files = Directory.Exists(DirectoryPath)
                ? Directory.GetFiles(DirectoryPath, "*.log")
                : [];
            foreach (var file in files)
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
                }
                catch { }
            }
            files = Directory.Exists(DirectoryPath) ? Directory.GetFiles(DirectoryPath, "*.log") : [];
            var total = files.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            if (total <= MaxFolderBytes) return;
            foreach (var file in files.OrderBy(File.GetLastWriteTime))
            {
                try
                {
                    var size = new FileInfo(file).Length;
                    File.Delete(file);
                    total -= size;
                    if (total <= MaxFolderBytes * 3 / 4) break;
                }
                catch { }
            }
        }
        catch { }
    }
}

internal static class ByteText
{
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.##} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.##} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };

    public static string Speed(long bytesPerSec) => bytesPerSec <= 0 ? "0 B/s" : Size(bytesPerSec) + "/s";
}
