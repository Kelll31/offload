<#
.SYNOPSIS
    Сборка Offload: тесты, публикация самодостаточного Offload.exe и (опционально) установщика.

.EXAMPLE
    .\scripts\build.ps1                 # тесты + publish\Offload.exe
    .\scripts\build.ps1 -Installer      # + установщик dist\Offload-Setup-<версия>.exe (нужен Inno Setup 6)
    .\scripts\build.ps1 -SkipTests
#>
param(
    [string]$Configuration = "Release",
    [switch]$Installer,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    $local = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
    if (Test-Path $local) { $dotnet = $local } else { throw "Не найден .NET SDK 10. Установите: https://dotnet.microsoft.com/download/dotnet/10.0" }
}
Write-Host "dotnet: $dotnet" -ForegroundColor DarkGray

if (-not $SkipTests) {
    Write-Host "== Тесты" -ForegroundColor Cyan
    & $dotnet test --solution Offload.slnx -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Тесты не прошли" }
}

Write-Host "== Публикация Offload.exe" -ForegroundColor Cyan
$publishDir = Join-Path $root "publish"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
& $dotnet publish src/Offload.App/Offload.App.csproj -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=embedded -p:PublishReadyToRun=true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "Публикация не удалась" }

$exe = Join-Path $publishDir "Offload.exe"
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "Готово: $exe ($size МБ)" -ForegroundColor Green

if ($Installer) {
    Write-Host "== Установщик" -ForegroundColor Cyan
    $iscc = @(
        (Get-Command iscc -ErrorAction SilentlyContinue).Source,
        "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) { throw "Не найден Inno Setup (ISCC.exe). Установите: winget install JRSoftware.InnoSetup" }
    $version = (Get-Item $exe).VersionInfo.ProductVersion.Split('+')[0]
    & $iscc "/DAppVersion=$version" "/DPublishDir=$publishDir" installer\Offload.iss
    if ($LASTEXITCODE -ne 0) { throw "Сборка установщика не удалась" }
    Write-Host "Установщик: $(Join-Path $root 'dist')" -ForegroundColor Green
}
