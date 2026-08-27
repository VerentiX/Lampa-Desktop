# Keeps the bundled custom sing-box-lx core and downloads Wintun.
$ErrorActionPreference = "Stop"
$core = Join-Path (Split-Path $PSScriptRoot -Parent) "Lampa.Desktop\core"
New-Item -ItemType Directory -Path $core -Force | Out-Null
$tmp = Join-Path $env:TEMP ("lampa-core-" + [guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Path $tmp | Out-Null

try {
    $singBox = Join-Path $core "sing-box.exe"
    if (-not (Test-Path $singBox)) {
        throw "Custom sing-box-lx.29 is missing: $singBox. Restore it from the project sources before packaging."
    }

    $wintun = Join-Path $tmp "wintun.zip"
    Write-Host "Downloading Wintun..."
    Invoke-WebRequest -Uri "https://www.wintun.net/builds/wintun-0.14.1.zip" -OutFile $wintun
    Expand-Archive $wintun -DestinationPath (Join-Path $tmp "wintun") -Force
    $dll = Get-ChildItem (Join-Path $tmp "wintun") -Recurse -Filter "wintun.dll" |
        Where-Object { $_.FullName -match '\\amd64\\' } |
        Select-Object -First 1
    if (-not $dll) { throw "wintun.dll (amd64) not found in archive" }
    Copy-Item $dll.FullName (Join-Path $core "wintun.dll") -Force

    Get-ChildItem $core | Select-Object Name, @{ N = "MB"; E = { [math]::Round($_.Length / 1MB, 2) } }
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
