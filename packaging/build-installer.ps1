$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "Lampa.Desktop\Lampa.Desktop.csproj"
$publishDir = Join-Path $PSScriptRoot "publish"
$distDir = Join-Path $root "dist"
$iss = Join-Path $PSScriptRoot "Lampa.iss"
$fetchCore = Join-Path $root "scripts\fetch-core.ps1"
if (-not (Test-Path (Join-Path $root "Lampa.Desktop\core\xray.exe"))) {
  & $fetchCore
}

Get-Process Lampa, xray -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir, $distDir -Force | Out-Null

dotnet publish $project -c Release -r win-x64 --self-contained true `
  -p:PublishDir="$publishDir\" `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None `
  -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$iscc = @(
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
  "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
  throw "Inno Setup 6 not found. Install JRSoftware.InnoSetup and re-run."
}

& $iscc $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

Get-ChildItem $distDir -Filter "LampaSetup*.exe" | Select-Object FullName, Length
