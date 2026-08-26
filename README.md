# Lampa Desktop

Windows WPF-клиент Lampa VPN: подписка → сервер → одна кнопка подключения.

Мобильное приложение: [VerentiX/Lampa](https://github.com/VerentiX/Lampa).

## Возможности

- та же subscription URL, что на Android, включая полный JSON-автоконфиг `/auto/`
- User-Agent `Lampa-Desktop/1.0`, профили FULL / Whitelist
- TUN по умолчанию (нужны права администратора)
- раздельное туннелирование по Windows-приложениям
- локальные HTTP/SOCKS как запасной режим
- трей, автозапуск, watchdog `xray.exe`
- пауза VPN во сне (Modern Standby) без холодного перезапуска ядра
- проверка обновлений с `https://hattabych.ru/api/app/latest?platform=windows`

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

Xray-core, Wintun и базы маршрутизации качаются скриптом и при необходимости докачиваются при запуске. Пользователю установщика отдельно ничего ставить не нужно (.NET 8 внутри self-contained publish).
