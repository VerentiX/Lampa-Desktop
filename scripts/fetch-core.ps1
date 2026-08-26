# Downloads Xray, Wintun and runetfreedom geo databases into Lampa.Desktop/core.
$ErrorActionPreference = "Stop"
$core = Join-Path (Split-Path $PSScriptRoot -Parent) "Lampa.Desktop\core"
New-Item -ItemType Directory -Path $core -Force | Out-Null
$tmp = Join-Path $env:TEMP ("lampa-core-" + [guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Path $tmp | Out-Null

try {
    $zip = Join-Path $tmp "xray.zip"
    Write-Host "Downloading Xray-windows-64..."
    Invoke-WebRequest -Uri "https://github.com/XTLS/Xray-core/releases/latest/download/Xray-windows-64.zip" -OutFile $zip
    Expand-Archive $zip -DestinationPath (Join-Path $tmp "xray") -Force
    Copy-Item (Join-Path $tmp "xray\xray.exe") (Join-Path $core "xray.exe") -Force

    $wintun = Join-Path $tmp "wintun.zip"
    Write-Host "Downloading Wintun..."
    Invoke-WebRequest -Uri "https://www.wintun.net/builds/wintun-0.14.1.zip" -OutFile $wintun
    Expand-Archive $wintun -DestinationPath (Join-Path $tmp "wintun") -Force
    $dll = Get-ChildItem (Join-Path $tmp "wintun") -Recurse -Filter "wintun.dll" |
        Where-Object { $_.FullName -match '\\amd64\\' } |
        Select-Object -First 1
    if (-not $dll) { throw "wintun.dll (amd64) not found in archive" }
    Copy-Item $dll.FullName (Join-Path $core "wintun.dll") -Force

    Write-Host "Downloading geoip/geosite..."
    Invoke-WebRequest -Uri "https://raw.githubusercontent.com/runetfreedom/russia-v2ray-rules-dat/release/geoip.dat" -OutFile (Join-Path $core "geoip.dat")
    Invoke-WebRequest -Uri "https://raw.githubusercontent.com/runetfreedom/russia-v2ray-rules-dat/release/geosite.dat" -OutFile (Join-Path $core "geosite.dat")

    Get-ChildItem $core | Select-Object Name, @{ N = "MB"; E = { [math]::Round($_.Length / 1MB, 2) } }
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
