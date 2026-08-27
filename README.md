# Lampa Desktop

Windows WPF-клиент Lampa VPN: подписка → сервер → одна кнопка подключения.

Мобильное приложение: [VerentiX/Lampa](https://github.com/VerentiX/Lampa).

## Возможности

- та же subscription URL, что на Android, включая полный JSON-автоконфиг `/auto/`
- User-Agent `Lampa-Desktop-SB`, sing-box JSON outbound subscriptions
- TUN по умолчанию (нужны права администратора)
- Для P0–P4 всегда используется режим «Полная»: через VPN идут Re:filter, геоблокированные для РФ сервисы, YouTube, Telegram, GitHub и Google Play; остальной трафик идёт напрямую. Для P5+ действует режим белых списков.
- удалённые бинарные SRS-списки обновляются и кешируются самим sing-box без перезапуска VPN
- раздельное туннелирование по Windows-приложениям
- локальные HTTP/SOCKS как запасной режим
- трей, автозапуск, watchdog `sing-box.exe` и автоматическая маршрутизация по активному P-приоритету
- пауза VPN во сне (Modern Standby) без холодного перезапуска ядра
- проверка обновлений с `https://hattabych.ru/api/app/latest?platform=windows`
- одноразовая миграция старой подписки на User-Agent `Lampa-Desktop-SB`
- защита от параллельного запуска и остановка оставшегося процесса ядра Lampa перед новым стартом

## Сборка

Нужны [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) и Windows 10/11 x64.

```powershell
.\scripts\fetch-core.ps1
dotnet build .\Lampa.Desktop.slnx -c Release
```

Установщик (Inno Setup 6):

```powershell
.\packaging\build-installer.ps1
```

Готовый файл: `dist\LampaSetup.exe`. Тот же `AppId`, что у предыдущих сборок — ставится поверх старой версии.

Кастомное ядро sing-box-lx `1.14.0-lx.29` хранится в проекте; Wintun при необходимости загружается скриптом. Старые `geoip.dat`/`geosite.dat` приложению не нужны. Пользователю установщика отдельно ничего ставить не нужно (.NET 8 внутри self-contained publish).
