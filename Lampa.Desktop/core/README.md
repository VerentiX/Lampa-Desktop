# sing-box-lx core

The application ships the custom Windows x64 `sing-box.exe` (`1.14.0-lx.29`) and `wintun.dll` from this folder.

From the repo root:

```powershell
.\scripts\fetch-core.ps1
```

Routing databases are remote binary SRS rule-sets. sing-box caches and refreshes them through the configured HTTP clients.
