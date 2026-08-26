# Xray core

Place Windows x64 `xray.exe`, `wintun.dll`, `geoip.dat` and `geosite.dat` in this folder.

From the repo root:

```powershell
.\scripts\fetch-core.ps1
```

Compat databases (`geoip-compat.dat`, `geosite-compat.dat`) are committed. Full geo databases are downloaded at runtime if missing or stale.
