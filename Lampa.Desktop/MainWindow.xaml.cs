using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lampa.Desktop.Models;
using Lampa.Desktop.Services;
using Forms = System.Windows.Forms;

namespace Lampa.Desktop;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly SubscriptionService _subscriptions = new();
    private readonly AppUpdateService _appUpdates = new();
    private readonly ConnectionSupervisor _connection;
    private readonly Forms.NotifyIcon _tray;
    private readonly DispatcherTimer _connectionTimer;
    private readonly DispatcherTimer _updateTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private DateTimeOffset? _connectedSince;
    private bool _reallyClose;
    private bool _isConnected;
    private bool _settingsUiReady;
    private bool _powerClickBusy;
    private bool _updatePromptVisible;
    private string _deferredUpdateVersion = "";
    private ConnectionState _renderedState = ConnectionState.Disconnected;
    private List<string> _draftProxyDomains = [];
    private List<string> _draftDirectDomains = [];

    public MainWindow()
    {
        InitializeComponent();
        Icon = AppIconFactory.CreateWindowIcon();
        // Жёстко отключаем автоподъём туннеля при старте приложения.
        // Иначе при совпадении условий (сеть/таймеры/старое значение в settings)
        // ConnectionSupervisor может запустить EnsureConnectedAsync.
        _settings.DesiredConnected = false;
        _settings.AutoReconnect = true;
        // Пользовательский переключатель маршрутизации скрыт: приложение
        // всегда использует полный набор SRS-правил.
        _settings.UseFullBlockList = true;
        _settings.Save();
        _connection = new ConnectionSupervisor(_settings);
        _connection.StateChanged += (state, message) => Dispatcher.Invoke(() => RenderState(state, message));
        _connectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _connectionTimer.Tick += (_, _) => UpdateConnectionTimer();
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _updateTimer.Tick += async (_, _) => await RunBackgroundUpdatesAsync();

        HomeSubscriptionUrlBox.Text = _settings.SubscriptionUrl;
        CorePathBox.Text = _settings.CorePath;
        PortBox.Text = _settings.LocalHttpPort.ToString();
        AutoReconnectCheck.IsChecked = _settings.AutoReconnect;
        PauseOnSleepCheck.IsChecked = _settings.PauseVpnOnSleep;
        TunCheck.IsChecked = _settings.UseTun;
        StartupCheck.IsChecked = _settings.StartWithWindows;

        ReloadProfiles();
        RenderBypassApps();
        RenderModeToggle();
        RenderRoutingMode();
        RenderRoutePolicy();
        _draftProxyDomains = [.. _settings.CustomProxyDomains];
        _draftDirectDomains = [.. _settings.CustomDirectDomains];
        RefreshCustomRulesUI();
        SubscriptionIntervalSlider.Value = _settings.SubscriptionUpdateHours;
        GeoIntervalSlider.Value = _settings.GeoUpdateDays;
        AppUpdateIntervalSlider.Value = _settings.AppUpdateDays;
        UpdateIntervalLabels();
        _settingsUiReady = true;
        _updateTimer.Start();
        _appUpdates.ProgressChanged += ui => Dispatcher.BeginInvoke(async () =>
        {
            RenderUpdateBanner(ui);
            await ShowUpdatePromptIfNeededAsync(ui);
        });
        RenderUpdateBanner(_appUpdates.CurrentUi(_settings));

        _tray = new Forms.NotifyIcon { Text = "Lampa Desktop", Icon = AppIconFactory.CreateTrayIcon(AppIconFactory.StatusKind.Idle), Visible = true };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Подключить", null, async (_, _) => await _connection.ConnectAsync());
        menu.Items.Add("Отключить", null, async (_, _) => await _connection.DisconnectAsync());
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _tray.ContextMenuStrip = menu;

        Loaded += async (_, _) =>
        {
            StartupManager.SetEnabled(_settings.StartWithWindows);
            await Task.Delay(4000);
            await EnsureSubscriptionSchemaAsync();
            await RunBackgroundUpdatesAsync();
            await ShowUpdatePromptIfNeededAsync(_appUpdates.CurrentUi(_settings));
        };
    }

    private void AddCustomProxy_Click(object sender, RoutedEventArgs e) => AddCustomRule(isProxy: true);

    private void AddCustomDirect_Click(object sender, RoutedEventArgs e) => AddCustomRule(isProxy: false);

    private void CustomProxyHostBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddCustomRule(isProxy: true);
    }

    private void CustomDirectHostBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddCustomRule(isProxy: false);
    }

    private void AddCustomRule(bool isProxy)
    {
        var box = isProxy ? CustomProxyHostBox : CustomDirectHostBox;
        var host = NormalizeHost(box.Text);
        if (string.IsNullOrWhiteSpace(host)) return;

        var list = isProxy ? _draftProxyDomains : _draftDirectDomains;
        if (!list.Any(x => string.Equals(x, host, StringComparison.OrdinalIgnoreCase)))
            list.Add(host);

        box.Clear();
        RefreshCustomRulesUI();
    }

    private void RemoveProxyRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string host }) return;
        _draftProxyDomains = _draftProxyDomains
            .Where(x => !string.Equals(x, host, StringComparison.OrdinalIgnoreCase))
            .ToList();
        RefreshCustomRulesUI();
    }

    private void RemoveDirectRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string host }) return;
        _draftDirectDomains = _draftDirectDomains
            .Where(x => !string.Equals(x, host, StringComparison.OrdinalIgnoreCase))
            .ToList();
        RefreshCustomRulesUI();
    }

    private void ClearCustomProxy_Click(object sender, RoutedEventArgs e)
    {
        if (_draftProxyDomains.Count == 0) return;
        if (System.Windows.MessageBox.Show(this, "Очистить все правила через VPN?", "Lampa Desktop", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _draftProxyDomains.Clear();
        RefreshCustomRulesUI();
    }

    private void ClearCustomDirect_Click(object sender, RoutedEventArgs e)
    {
        if (_draftDirectDomains.Count == 0) return;
        if (System.Windows.MessageBox.Show(this, "Очистить все правила мимо VPN?", "Lampa Desktop", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _draftDirectDomains.Clear();
        RefreshCustomRulesUI();
    }

    private async void SaveCustomRules_Click(object sender, RoutedEventArgs e)
    {
        if (!CustomRulesAreDirty()) return;

        _settings.CustomProxyDomains = [.. _draftProxyDomains];
        _settings.CustomDirectDomains = [.. _draftDirectDomains];
        _settings.Save();
        RefreshCustomRulesUI();
        if (_isConnected) await RestartTunnelAsync();
    }

    private void RefreshCustomRulesUI()
    {
        CustomProxyCountText.Text = $"{_draftProxyDomains.Count}";
        CustomDirectCountText.Text = $"{_draftDirectDomains.Count}";

        CustomProxyRulesList.ItemsSource = null;
        CustomProxyRulesList.ItemsSource = _draftProxyDomains
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        CustomDirectRulesList.ItemsSource = null;
        CustomDirectRulesList.ItemsSource = _draftDirectDomains
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        CustomProxyEmptyText.Visibility = _draftProxyDomains.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CustomDirectEmptyText.Visibility = _draftDirectDomains.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearCustomProxyBtn.Visibility = _draftProxyDomains.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearCustomDirectBtn.Visibility = _draftDirectDomains.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var dirty = CustomRulesAreDirty();
        SaveCustomRulesBtn.IsEnabled = dirty;
        SaveCustomRulesBtn.Opacity = dirty ? 1 : 0.4;
    }

    private bool CustomRulesAreDirty() =>
        !SameDomainSet(_draftProxyDomains, _settings.CustomProxyDomains) ||
        !SameDomainSet(_draftDirectDomains, _settings.CustomDirectDomains);

    private static bool SameDomainSet(IReadOnlyCollection<string> left, IReadOnlyCollection<string> right) =>
        left.Count == right.Count &&
        left.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(right);

    private static string NormalizeHost(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var v = input.Trim();

        if (v.Contains("://", StringComparison.OrdinalIgnoreCase))
        {
            try { v = new Uri(v).Host; } catch { }
        }

        v = v.Split('/')[0].Split('?')[0].Split('#')[0];
        v = v.Trim().Trim('.');
        return v.ToLowerInvariant();
    }

    private async Task RestartTunnelAsync()
    {
        try
        {
            await _connection.DisconnectAsync(false);
            await _connection.ConnectAsync();
        }
        catch (Exception ex)
        {
            StatusHint.Text = $"Ошибка перезапуска: {ex.Message}";
        }
    }

    private async void PowerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_powerClickBusy) return;
        _powerClickBusy = true;
        try
        {
            if (_settings.DesiredConnected)
            {
                RenderState(ConnectionState.Disconnected, "Отключаем…");
                await Task.Yield();
                await _connection.DisconnectAsync();
                return;
            }

            RenderState(ConnectionState.Connecting, "Подключаемся…");
            await Task.Yield();
            await _connection.ConnectAsync();
        }
        finally
        {
            _powerClickBusy = false;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshSubscriptionAsync();

    private void ToggleImportPopup_Click(object sender, RoutedEventArgs e)
    {
        ManualImportPanel.Visibility = Visibility.Collapsed;
        ImportPopup.IsOpen = true;
    }

    private async void ImportFromClipboard_Click(object sender, RoutedEventArgs e)
    {
        ImportPopup.IsOpen = false;
        if (!System.Windows.Clipboard.ContainsText())
        {
            System.Windows.MessageBox.Show(this, "В буфере обмена нет ссылки.", "Lampa Desktop", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var url = System.Windows.Clipboard.GetText().Trim();
        if (url.Length == 0) return;
        HomeSubscriptionUrlBox.Text = url;
        _settings.SubscriptionUrl = SubscriptionUrlResolver.NormalizeStoredUrl(url);
        _settings.Save();
        await RefreshSubscriptionAsync();
    }

    private void ShowManualImport_Click(object sender, RoutedEventArgs e)
    {
        ManualImportPanel.Visibility = Visibility.Visible;
        HomeSubscriptionUrlBox.Text = _settings.SubscriptionUrl;
        HomeSubscriptionUrlBox.Focus();
    }

    private async void ImportManualUrl_Click(object sender, RoutedEventArgs e)
    {
        var input = HomeSubscriptionUrlBox.Text.Trim();
        if (input.Length == 0) return;
        ImportPopup.IsOpen = false;
        _settings.SubscriptionUrl = SubscriptionUrlResolver.NormalizeStoredUrl(input);
        _settings.Save();
        await RefreshSubscriptionAsync();
    }

    private void ToggleSubscriptionLink_Click(object sender, RoutedEventArgs e)
    {
        var show = SubscriptionLinkText.Visibility != Visibility.Visible;
        SubscriptionLinkText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ToggleLinkBtn.Content = show ? "Скрыть ссылку" : "Показать ссылку";
    }

    private async Task<bool> RefreshSubscriptionAsync(bool silent = false)
    {
        if (string.IsNullOrWhiteSpace(_settings.SubscriptionUrl))
        {
            if (!silent) StatusHint.Text = "Сначала импортируйте ссылку через +";
            return false;
        }

        try
        {
            if (!silent) SubscriptionMetaText.Text = "Обновление…";
            var result = await _subscriptions.DownloadAsync(_settings.SubscriptionUrl, CancellationToken.None);
            _settings.Profiles = result.Profiles;
            _settings.LastSubscriptionUpdate = DateTimeOffset.Now;
            _settings.SubscriptionUrl = SubscriptionUrlResolver.NormalizeStoredUrl(_settings.SubscriptionUrl);
            _settings.SubscriptionTitle = result.Metadata.Title;
            _settings.SubscriptionUpload = result.Metadata.Upload;
            _settings.SubscriptionDownload = result.Metadata.Download;
            _settings.SubscriptionTotal = result.Metadata.Total;
            _settings.SubscriptionExpire = result.Metadata.Expire;
            _settings.ProfileRouting = result.Metadata.ProfileRouting;
            _settings.GeoIpUrl = result.Metadata.GeoIpUrl;
            _settings.GeoSiteUrl = result.Metadata.GeoSiteUrl;
            if (_settings.SelectedProfile >= result.Profiles.Count) _settings.SelectedProfile = 0;
            _settings.Save();
            ReloadProfiles();
            if (!silent) StatusHint.Text = "Конфигурация обновлена";
            return true;
        }
        catch (Exception ex)
        {
            if (silent)
            {
                ReloadProfiles();
                return false;
            }
            SubscriptionMetaText.Text = "Ошибка обновления";
            StatusHint.Text = ex.Message;
            return false;
        }
    }

    private async Task EnsureSubscriptionSchemaAsync()
    {
        const int currentSchema = 1; // Lampa-Desktop-SB / sing-box outbound array.
        if (_settings.SubscriptionSchemaVersion >= currentSchema) return;
        if (string.IsNullOrWhiteSpace(_settings.SubscriptionUrl))
        {
            // A subscription imported later is immediately requested with the
            // current User-Agent, so there is nothing to migrate yet.
            _settings.SubscriptionSchemaVersion = currentSchema;
            _settings.Save();
            return;
        }

        // Ignore LastSubscriptionUpdate once after upgrading from the legacy
        // client. Retry on later starts until the new format succeeds.
        if (!await RefreshSubscriptionAsync(silent: true)) return;
        _settings.SubscriptionSchemaVersion = currentSchema;
        _settings.Save();
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.CorePath = CorePathBox.Text.Trim();
        if (int.TryParse(PortBox.Text, out var port) && port is > 1024 and < 65534)
            _settings.LocalHttpPort = port;
        _settings.AutoReconnect = AutoReconnectCheck.IsChecked == true;
        _settings.PauseVpnOnSleep = PauseOnSleepCheck.IsChecked == true;
        _settings.UseTun = TunCheck.IsChecked == true;
        _settings.StartWithWindows = StartupCheck.IsChecked == true;
        ReadIntervalSliders();
        _settings.Save();
        StartupManager.SetEnabled(_settings.StartWithWindows);
        RenderModeToggle();
        RenderRoutePolicy();
        StatusHint.Text = "Настройки сохранены";
    }

    private void PauseOnSleepCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingsUiReady) return;
        _settings.PauseVpnOnSleep = PauseOnSleepCheck.IsChecked == true;
        _settings.Save();
    }

    private void SubscriptionIntervalSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_settingsUiReady) return;
        ReadIntervalSliders();
        _settings.Save();
        UpdateIntervalLabels();
    }

    private void GeoIntervalSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_settingsUiReady) return;
        ReadIntervalSliders();
        _settings.Save();
        UpdateIntervalLabels();
    }

    private void AppUpdateIntervalSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_settingsUiReady) return;
        ReadIntervalSliders();
        _settings.Save();
        UpdateIntervalLabels();
    }

    private void ReadIntervalSliders()
    {
        _settings.SubscriptionUpdateHours = Math.Clamp((int)Math.Round(SubscriptionIntervalSlider.Value / 6.0) * 6, 6, 72);
        _settings.GeoUpdateDays = Math.Clamp((int)Math.Round(GeoIntervalSlider.Value), 1, 7);
        _settings.AppUpdateDays = Math.Clamp((int)Math.Round(AppUpdateIntervalSlider.Value), 3, 30);
    }

    private void UpdateIntervalLabels()
    {
        SubscriptionIntervalLabel.Text = $"Каждые {FormatHours((int)SubscriptionIntervalSlider.Value)}";
        GeoIntervalLabel.Text = $"Каждые {FormatDays((int)GeoIntervalSlider.Value)}";
        AppUpdateIntervalLabel.Text = $"Каждые {FormatDays((int)AppUpdateIntervalSlider.Value)}";
    }

    private async Task RunBackgroundUpdatesAsync()
    {
        try { await MaybeRefreshSubscriptionAsync(); } catch { }
        try { await _appUpdates.CheckAndContinueAsync(_settings, ignoreInterval: false, _lifetime.Token); } catch { }
    }

    private async Task MaybeRefreshSubscriptionAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.SubscriptionUrl)) return;
        var hours = Math.Clamp(_settings.SubscriptionUpdateHours, 6, 72);
        if (_settings.LastSubscriptionUpdate is { } last && DateTimeOffset.Now - last < TimeSpan.FromHours(hours))
            return;
        await RefreshSubscriptionAsync(silent: true);
    }

    private static string FormatHours(int hours)
    {
        hours = Math.Clamp(hours, 6, 72);
        if (hours % 24 == 0) return FormatDays(hours / 24);
        return $"{hours} {Plural(hours, "час", "часа", "часов")}";
    }

    private static string FormatDays(int days)
    {
        days = Math.Max(1, days);
        return $"{days} {Plural(days, "день", "дня", "дней")}";
    }

    private static string Plural(int n, string one, string few, string many)
    {
        var n100 = n % 100;
        var n10 = n % 10;
        if (n10 == 1 && n100 != 11) return one;
        if (n10 is >= 2 and <= 4 && n100 is < 12 or > 14) return few;
        return many;
    }

    private void ReloadProfiles()
    {
        var hasSubscription = _settings.Profiles.Count > 0 || !string.IsNullOrWhiteSpace(_settings.SubscriptionUrl);
        if (!hasSubscription)
        {
            SubscriptionNameText.Text = "Подписка не добавлена";
            SubscriptionMetaText.Text = "Добавьте ссылку кнопкой справа";
            SelectedProfileText.Text = "Конфигурация не выбрана";
            SelectedServerText.Text = "";
            SubscriptionLinkText.Text = "";
            ToggleLinkBtn.Visibility = Visibility.Collapsed;
            ProfilesList.ItemsSource = null;
            ProfilesList.Visibility = Visibility.Collapsed;
            SubscriptionCard.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1A18110B")!);
            SubscriptionCard.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#28FFB300")!);
            return;
        }

        var title = _settings.SubscriptionTitle.Length > 0 ? _settings.SubscriptionTitle : "Lampa Desktop";
        SubscriptionNameText.Text = title;
        SubscriptionMetaText.Text = BuildSubscriptionMeta();
        var server = _settings.Profiles.ElementAtOrDefault(_settings.SelectedProfile)?.Name ?? title;
        SelectedProfileText.Text = server;
        SelectedServerText.Text = server;
        SubscriptionLinkText.Text = _settings.SubscriptionUrl;
        ToggleLinkBtn.Visibility = string.IsNullOrWhiteSpace(_settings.SubscriptionUrl) ? Visibility.Collapsed : Visibility.Visible;
        ProfilesList.ItemsSource = null;
        ProfilesList.ItemsSource = _settings.Profiles;
        ProfilesList.SelectedIndex = Math.Clamp(_settings.SelectedProfile, 0, Math.Max(0, _settings.Profiles.Count - 1));
        ProfilesList.Visibility = _settings.Profiles.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        SubscriptionCard.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#33241408")!);
        SubscriptionCard.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#88FFB300")!);
    }

    private string BuildSubscriptionMeta()
    {
        var parts = new List<string>();
        if (_settings.SubscriptionTotal > 0)
            parts.Add($"{FormatBytes(_settings.SubscriptionUpload + _settings.SubscriptionDownload)} / {FormatBytes(_settings.SubscriptionTotal)}");
        else if (_settings.SubscriptionDownload > 0 || _settings.SubscriptionUpload > 0)
            parts.Add($"↑ {FormatBytes(_settings.SubscriptionUpload)} · ↓ {FormatBytes(_settings.SubscriptionDownload)}");
        if (_settings.SubscriptionExpire > 0)
        {
            var expire = DateTimeOffset.FromUnixTimeSeconds(_settings.SubscriptionExpire).LocalDateTime;
            parts.Add($"до {expire:dd.MM.yyyy}");
        }
        parts.Add($"{_settings.Profiles.Count} проф.");
        return string.Join(" · ", parts);
    }

    private string FormatUpdatedAt()
    {
        if (_settings.LastSubscriptionUpdate is null) return "сейчас";
        var delta = DateTimeOffset.Now - _settings.LastSubscriptionUpdate.Value;
        if (delta.TotalMinutes < 1) return "только что";
        if (delta.TotalHours < 1) return $"{(int)delta.TotalMinutes} мин назад";
        if (delta.TotalDays < 1) return $"{(int)delta.TotalHours} ч назад";
        return _settings.LastSubscriptionUpdate.Value.LocalDateTime.ToString("dd.MM HH:mm", CultureInfo.CurrentCulture);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var order = Math.Min((int)Math.Log(bytes, 1024), units.Length - 1);
        var value = bytes / Math.Pow(1024, order);
        return $"{value:0.#} {units[order]}";
    }

    private void ChooseBypassApps_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProcessPickerWindow(_settings.BypassApplications) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _settings.BypassApplications = dialog.SelectedPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Path.GetFileName)
            .ToList();
        _settings.Save();
        RenderBypassApps();
        StatusHint.Text = "Список приложений обновлён";
    }

    private void ClearBypassApps_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.BypassApplications.Count == 0) return;
        if (System.Windows.MessageBox.Show(this, "Очистить список приложений в обход VPN?", "Lampa Desktop", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _settings.BypassApplications.Clear();
        _settings.Save();
        RenderBypassApps();
    }

    private void RenderBypassApps()
    {
        var count = _settings.BypassApplications.Count;
        BypassSummaryText.Text = count == 0 ? "Все приложения через VPN" : $"{count} приложений в обход";
        BypassAppsText.Text = count == 0
            ? "Приложения не выбраны"
            : string.Join("\n", _settings.BypassApplications.Select(Path.GetFileName));
    }

    private void RenderState(ConnectionState state, string message)
    {
        var active = state == ConnectionState.Connected;
        _isConnected = active;
        StatusBadgeText.Text = state switch
        {
            ConnectionState.Connected => ":: ПОДКЛЮЧЕНО ::",
            ConnectionState.Connecting => ":: СОЕДИНЕНИЕ... ::",
            ConnectionState.Recovering => ":: ВОССТАНОВЛЕНИЕ... ::",
            ConnectionState.Paused => ":: ПАУЗА ::",
            ConnectionState.Error => ":: ОШИБКА ::",
            _ => ":: НЕ В СЕТИ ::"
        };
        var busy = state is ConnectionState.Connecting or ConnectionState.Recovering;
        var statusColor = state == ConnectionState.Error ? "#FF453A" : active ? "#42F58A" : busy ? "#FF9D1C" : "#9CCEF5";
        StatusBadgeText.Foreground = Brush(statusColor);

        StatusHint.Text = message;
        SetPowerBusy(busy);
        PowerButton.Background = Brush(active ? "#FF9D1C" : busy ? "#332414" : "#E0091320");
        PowerButton.BorderBrush = Brush(active ? "#FFD36A" : busy ? "#FF9D1C" : "#B058B8FF");
        PowerButton.Foreground = Brush(active ? "#07111D" : busy ? "#FF9D1C" : "#42F58A");
        PowerButton.ToolTip = active ? "Отключить" : "Подключить";
        if (active && _renderedState != ConnectionState.Connected)
            ((Storyboard)FindResource("PowerConnectedStoryboard")).Begin(this, true);
        _renderedState = state;
        _tray.Text = state switch
        {
            ConnectionState.Connected => "Lampa Desktop — подключено",
            ConnectionState.Paused => "Lampa Desktop — пауза на время сна",
            ConnectionState.Recovering => "Lampa Desktop — восстановление",
            _ => "Lampa Desktop — отключено"
        };
        _tray.Icon = AppIconFactory.CreateTrayIcon(state == ConnectionState.Error
            ? AppIconFactory.StatusKind.Error
            : active ? AppIconFactory.StatusKind.Connected : AppIconFactory.StatusKind.Idle);

        if (active)
        {
            _connectedSince ??= DateTimeOffset.Now;
            _connectionTimer.Start();
        }
        else
        {
            _connectedSince = null;
            _connectionTimer.Stop();
            ConnectionTimerText.Text = "00:00:00";
        }
    }

    private void SetPowerBusy(bool busy)
    {
        var storyboard = (Storyboard)FindResource("PowerPulseStoryboard");
        if (busy)
        {
            storyboard.Begin(this, true);
            return;
        }

        storyboard.Stop(this);
        PowerButton.Opacity = 1;
        PowerScale.ScaleX = 1;
        PowerScale.ScaleY = 1;
    }

    private static SolidColorBrush Brush(string color) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color)!);

    private void UpdateConnectionTimer()
    {
        if (_connectedSince is null) return;
        var elapsed = DateTimeOffset.Now - _connectedSince.Value;
        ConnectionTimerText.Text = $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
    }

    private void ProxyMode_Click(object sender, RoutedEventArgs e) => SetConnectionMode(useTun: false);
    private void TunMode_Click(object sender, RoutedEventArgs e) => SetConnectionMode(useTun: true);
    private void TunCheck_Changed(object sender, RoutedEventArgs e) => RenderModeToggle();

    private void SetConnectionMode(bool useTun)
    {
        _settings.UseTun = useTun;
        TunCheck.IsChecked = useTun;
        _settings.Save();
        RenderModeToggle();
        StatusHint.Text = useTun
            ? "Режим TUN: весь трафик через VPN"
            : $"Режим прокси: 127.0.0.1:{_settings.LocalHttpPort}";
    }

    private void RenderModeToggle()
    {
        var useTun = TunCheck.IsChecked == true;
        ProxyModeBtn.Tag = useTun ? null : "active";
        TunModeBtn.Tag = useTun ? "active" : null;
    }

    private void RenderRoutingMode()
    {
        FullRoutingBtn.Tag = _settings.UseFullBlockList ? "active" : null;
        FastRoutingBtn.Tag = _settings.UseFullBlockList ? null : "active";
        RenderRoutePolicy();
    }

    private void RenderRoutePolicy()
    {
        ExceptRuRouteBtn.IsEnabled = !_settings.UseFullBlockList;
        BlockedOnlyRouteBtn.IsEnabled = !_settings.UseFullBlockList;
        if (_settings.UseFullBlockList)
        {
            RoutePolicyHint.Text = "P0–P4: блокировки и геоограничения через VPN, остальное напрямую. P5+: режим белых списков.";
            return;
        }
        ExceptRuRouteBtn.Tag = _settings.RouteExceptRussia ? "active" : null;
        BlockedOnlyRouteBtn.Tag = _settings.RouteExceptRussia ? null : "active";
        RoutePolicyHint.Text = _settings.RouteExceptRussia
            ? "Иностранные сайты через VPN, российские IP и «только из РФ» — напрямую. Gmail, Claude, HubSpot обычно работают."
            : "В VPN только списки РКН (ru-blocked). Остальное напрямую — могут рваться Gmail, Anthropic, виджеты на сайтах.";
    }

    private async void ExceptRuRoute_Click(object sender, RoutedEventArgs e) => await SetRouteExceptRussiaAsync(true);

    private async void BlockedOnlyRoute_Click(object sender, RoutedEventArgs e) => await SetRouteExceptRussiaAsync(false);

    private async Task SetRouteExceptRussiaAsync(bool exceptRussia)
    {
        if (_settings.RouteExceptRussia == exceptRussia) return;
        _settings.RouteExceptRussia = exceptRussia;
        _settings.Save();
        RenderRoutePolicy();
        StatusHint.Text = exceptRussia ? "Режим: всё кроме РФ" : "Режим: только блокировки";
        if (_isConnected) await RestartTunnelAsync();
    }

    private void RoutingInfo_Click(object sender, RoutedEventArgs e) => RoutingInfoPopup.IsOpen = true;

    private async void FullRouting_Click(object sender, RoutedEventArgs e) => await SetBlockListModeAsync(true);

    private async void FastRouting_Click(object sender, RoutedEventArgs e) => await SetBlockListModeAsync(false);

    private async Task SetBlockListModeAsync(bool full)
    {
        if (_settings.UseFullBlockList == full) return;
        _settings.UseFullBlockList = full;
        _settings.Save();
        RenderRoutingMode();
        if (_isConnected) await RestartTunnelAsync();
    }

    private void ShowPage(string page)
    {
        HomePage.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        RoutingPage.Visibility = page == "routing" ? Visibility.Visible : Visibility.Collapsed;
        RulesPage.Visibility = page == "rules" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HomeNav_Click(object sender, RoutedEventArgs e) => ShowPage("home");
    private void RoutingNav_Click(object sender, RoutedEventArgs e) => ShowPage("routing");
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => ShowPage("settings");
    private void RulesNav_Click(object sender, RoutedEventArgs e) => ShowPage("rules");

    private async void ProfilesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProfilesList.SelectedIndex < 0 || ProfilesList.SelectedIndex == _settings.SelectedProfile) return;
        _settings.SelectedProfile = ProfilesList.SelectedIndex;
        _settings.Save();
        ReloadProfiles();
        StatusHint.Text = "Активный профиль изменён";
        if (_isConnected) await RestartTunnelAsync();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeRestore_Click(object sender, RoutedEventArgs e) => ToggleWindowState();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleWindowState() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_reallyClose) return;
        e.Cancel = true;
        Hide();
        _tray.ShowBalloonTip(1500, "Lampa Desktop", "Приложение продолжает работать в трее", Forms.ToolTipIcon.Info);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        RenderUpdateBanner(_appUpdates.CurrentUi(_settings));
        _ = _appUpdates.CheckAndContinueAsync(_settings, ignoreInterval: false, _lifetime.Token);
    }

    private void RenderUpdateBanner(AppUpdateUi ui)
    {
        if (ui.State == AppUpdateUiState.Hidden)
        {
            UpdateBanner.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateBanner.Visibility = Visibility.Visible;
        UpdateBannerText.Text = ui.Message;
        UpdateProgressBar.Visibility = ui.State == AppUpdateUiState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressBar.Value = ui.Percent;
        UpdateInstallBtn.Visibility = ui.CanInstall ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        await ShowUpdatePromptIfNeededAsync(_appUpdates.CurrentUi(_settings), force: true);
    }

    private async Task ShowUpdatePromptIfNeededAsync(AppUpdateUi ui, bool force = false)
    {
        if (ui.State != AppUpdateUiState.Ready || _updatePromptVisible) return;
        var version = _settings.PendingUpdateVersion;
        if (string.IsNullOrWhiteSpace(version)) return;
        if (!force && string.Equals(_deferredUpdateVersion, version, StringComparison.OrdinalIgnoreCase)) return;

        _updatePromptVisible = true;
        try
        {
            var prompt = new UpdatePromptWindow(version) { Owner = this };
            if (prompt.ShowDialog() == true)
                await InstallReadyUpdateAsync();
            else
                _deferredUpdateVersion = version;
        }
        finally
        {
            _updatePromptVisible = false;
        }
    }

    private async Task InstallReadyUpdateAsync()
    {
        var path = _appUpdates.ReadyInstallerPath(_settings);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            RenderUpdateBanner(_appUpdates.CurrentUi(_settings));
            return;
        }

        try
        {
            await _connection.DisconnectAsync(false);
            AppUpdateService.LaunchInstaller(path);
            ExitApplication();
        }
        catch (Exception ex)
        {
            StatusHint.Visibility = Visibility.Visible;
            StatusHint.Text = $"Не удалось запустить установщик: {ex.Message}";
        }
    }

    private async void ExitApplication()
    {
        _reallyClose = true;
        _lifetime.Cancel();
        _connectionTimer.Stop();
        _updateTimer.Stop();
        await _connection.DisconnectAsync(false);
        _connection.Dispose();
        _appUpdates.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    internal void ExitApplicationForUninstall()
    {
        // Execute in the interactive user's process so the correct HKCU Run
        // value is removed even when the uninstaller itself is elevated.
        StartupManager.SetEnabled(false);
        ExitApplication();
    }
}
