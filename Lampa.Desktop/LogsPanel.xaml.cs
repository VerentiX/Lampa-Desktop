using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Lampa.Desktop.Models;
using Lampa.Desktop.Services;
using Brush = System.Windows.Media.Brush;

namespace Lampa.Desktop;

public partial class LogsPanel : System.Windows.Controls.UserControl
{
    public sealed class JournalRow
    {
        public string TimeText { get; init; } = "";
        public string LevelText { get; init; } = "";
        public string Message { get; init; } = "";
        public Brush LevelBrush { get; init; } = System.Windows.Media.Brushes.White;
        public Brush MessageBrush { get; init; } = System.Windows.Media.Brushes.White;
        public AppLogLevel Level { get; init; }
        public string Source { get; init; } = "";
        public string Haystack { get; init; } = "";
    }

    private static readonly SolidColorBrush ErrorBrush = MakeBrush("#FF6B6B");
    private static readonly SolidColorBrush WarnBrush = MakeBrush("#FFB300");
    private static readonly SolidColorBrush InfoBrush = MakeBrush("#58B8FF");
    private static readonly SolidColorBrush DebugBrush = MakeBrush("#80FFFFFF");
    private static readonly SolidColorBrush AccessBrush = MakeBrush("#42F58A");
    private static readonly SolidColorBrush MuteBrush = MakeBrush("#CBE8FF");

    private readonly ObservableCollection<JournalRow> _journal = [];
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly DispatcherTimer _uiTimer;
    private AppSettings? _settings;
    private ClashConnectionMonitor? _connections;
    private bool _accessTab;
    private bool _liveAttached;
    private bool _active;

    public LogsPanel()
    {
        InitializeComponent();
        JournalList.ItemsSource = _journal;
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTimer.Tick += (_, _) => DrainPending();
    }

    public void Activate(AppSettings settings, ClashConnectionMonitor connections)
    {
        _settings = settings;
        _connections = connections;
        if (_active) { RebuildJournal(); return; }
        _active = true;
        RebuildJournal();
        LogStore.Instance.AddListener();
        LogStore.Instance.Appended += OnLog;
        _uiTimer.Start();
        if (_accessTab) AttachLive();
    }

    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        _uiTimer.Stop();
        LogStore.Instance.Appended -= OnLog;
        LogStore.Instance.RemoveListener();
        DetachLive();
    }

    private void OnLog(LogEntry entry) => _pending.Enqueue(entry);

    private void DrainPending()
    {
        if (!_active) return;
        var added = false;
        while (_pending.TryDequeue(out var entry))
        {
            var row = ToRow(entry);
            if (!Matches(row)) continue;
            _journal.Add(row);
            added = true;
            while (_journal.Count > LogStore.UiCap) _journal.RemoveAt(0);
        }
        if (!_accessTab)
            FooterText.Text = $"{_journal.Count} строк";
        if (added && FollowTailCheck.IsChecked == true && JournalList.Items.Count > 0)
            JournalList.ScrollIntoView(JournalList.Items[^1]);
    }

    private void JournalTab_Click(object sender, RoutedEventArgs e)
    {
        _accessTab = false;
        JournalTab.Tag = "active";
        AccessTab.Tag = null;
        JournalPane.Visibility = Visibility.Visible;
        AccessPane.Visibility = Visibility.Collapsed;
        CloseAllBtn.Visibility = Visibility.Collapsed;
        DetachLive();
        FooterText.Text = $"{_journal.Count} строк";
    }

    private void AccessTab_Click(object sender, RoutedEventArgs e)
    {
        _accessTab = true;
        JournalTab.Tag = null;
        AccessTab.Tag = "active";
        JournalPane.Visibility = Visibility.Collapsed;
        AccessPane.Visibility = Visibility.Visible;
        CloseAllBtn.Visibility = Visibility.Visible;
        if (_active) AttachLive();
        FooterText.Text = "Соединения · живой список через Clash API";
    }

    private void AttachLive()
    {
        if (_liveAttached || _connections is null) return;
        _liveAttached = true;
        _connections.Updated += OnConnections;
        _connections.AddLiveViewer();
    }

    private void DetachLive()
    {
        if (!_liveAttached || _connections is null) return;
        _liveAttached = false;
        _connections.Updated -= OnConnections;
        _connections.RemoveLiveViewer();
    }

    private void OnConnections(ClashConnectionsSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_active || !_accessTab) return;
            var filter = FilterBox.Text.Trim();
            IEnumerable<ConnectionRow> rows = snapshot.Rows;
            if (!string.IsNullOrEmpty(filter))
                rows = rows.Where(x => x.Host.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                       x.Chain.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                       x.Process.Contains(filter, StringComparison.OrdinalIgnoreCase));
            var list = rows.ToList();
            ConnectionsList.ItemsSource = list;
            FooterText.Text = $"активных {list.Count} · ↓ {ByteText.Size(snapshot.DownloadTotal)} · ↑ {ByteText.Size(snapshot.UploadTotal)}";
        });
    }

    private async void CloseAll_Click(object sender, RoutedEventArgs e)
    {
        if (_connections is not null) await _connections.CloseAllAsync();
    }

    private async void CloseConnection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string id } && _connections is not null)
            await _connections.CloseAsync(id);
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_active) return;
        if (!_accessTab) RebuildJournal();
    }

    private void RebuildJournal()
    {
        _journal.Clear();
        foreach (var entry in LogStore.Instance.Snapshot())
        {
            var row = ToRow(entry);
            if (Matches(row)) _journal.Add(row);
        }
        FooterText.Text = $"{_journal.Count} строк";
        if (FollowTailCheck.IsChecked == true && _journal.Count > 0)
            JournalList.ScrollIntoView(_journal[^1]);
    }

    private bool Matches(JournalRow row)
    {
        var text = FilterBox.Text.Trim();
        return string.IsNullOrEmpty(text) || row.Haystack.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private static JournalRow ToRow(LogEntry entry)
    {
        var (levelText, levelBrush, messageBrush) = entry.Source == "access"
            ? ("ACC", AccessBrush, AccessBrush)
            : entry.Level switch
            {
                AppLogLevel.Error => ("ERR", ErrorBrush, ErrorBrush),
                AppLogLevel.Warn => ("WARN", WarnBrush, MuteBrush),
                AppLogLevel.Debug => ("DBG", DebugBrush, DebugBrush),
                _ => (entry.Source == "app" ? "APP" : "INFO", InfoBrush, MuteBrush)
            };
        return new JournalRow
        {
            TimeText = entry.Time.ToString("HH:mm:ss"),
            LevelText = levelText,
            Message = entry.Message,
            LevelBrush = levelBrush,
            MessageBrush = messageBrush,
            Level = entry.Level,
            Source = entry.Source,
            Haystack = $"{entry.Source} {entry.Message}"
        };
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Экспорт журналов Lampa",
                Filter = "ZIP-архив (*.zip)|*.zip",
                FileName = $"lampa-logs-{DateTime.Now:yyyyMMdd-HHmm}.zip"
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            var zip = LogStore.Instance.ExportZip();
            File.Copy(zip, dialog.FileName, true);
            try { File.Delete(zip); } catch { }
            FooterText.Text = "Архив сохранён";
        }
        catch (Exception ex)
        {
            FooterText.Text = ex.Message;
        }
    }

    private static SolidColorBrush MakeBrush(string hex)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private void UserControl_Unloaded(object sender, RoutedEventArgs e) => Deactivate();
}
