<#
.SYNOPSIS
    Сборка Offload: тесты, публикация самодостаточного Offload.exe и (опционально) установщика.

.EXAMPLE
    .\scripts\build.ps1                 # тесты + publish\Offload.exe
    .\scripts\build.ps1 -Installer      # + установщик dist\Offload-Setup-<версия>.exe (нужен Inno Setup 6)
    .\scripts\build.ps1 -SkipTests
    .\scripts\build.ps1 -Installer -Sign   # + подпись exe и установщика (сертификат — см. .NOTES)

.NOTES
    Подпись (-Sign) берёт сертификат из переменных окружения:
      OFFLOAD_SIGN_PFX_BASE64 + OFFLOAD_SIGN_PFX_PASSWORD — PFX в base64 (секреты CI; на время сборки импортируется в хранилище), или
      OFFLOAD_SIGN_THUMBPRINT — отпечаток сертификата в хранилище текущего пользователя (например, на токене).
    OFFLOAD_SIGN_TIMESTAMP — сервер меток времени (по умолчанию http://timestamp.digicert.com).
#>
param(
    [string]$Configuration = "Release",
    [switch]$Installer,
    [switch]$SkipTests,
    [switch]$Sign
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

# Подпись: сертификат из PFX временно импортируется в хранилище текущего пользователя, signtool и Inno Setup получают
# только отпечаток — пароль не попадает ни в командные строки, ни во временные файлы. Удаление — в finally.
$signtool = $null
$signArgs = $null
$importedCert = $null

function Initialize-Signing {
    $script:signtool = @(
        (Get-Command signtool -ErrorAction SilentlyContinue).Source,
        (Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName)
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $script:signtool) { throw "Не найден signtool.exe (Windows SDK)" }
    $timestamp = if ($env:OFFLOAD_SIGN_TIMESTAMP) { $env:OFFLOAD_SIGN_TIMESTAMP } else { "http://timestamp.digicert.com" }

    $thumbprint = $env:OFFLOAD_SIGN_THUMBPRINT
    if ($env:OFFLOAD_SIGN_PFX_BASE64) {
        $flags = [Security.Cryptography.X509Certificates.X509KeyStorageFlags]"PersistKeySet,UserKeySet"
        $cert = New-Object Security.Cryptography.X509Certificates.X509Certificate2(
            [Convert]::FromBase64String($env:OFFLOAD_SIGN_PFX_BASE64), $env:OFFLOAD_SIGN_PFX_PASSWORD, $flags)
        $store = New-Object Security.Cryptography.X509Certificates.X509Store("My", "CurrentUser")
        $store.Open("ReadWrite")
        try { $store.Add($cert) } finally { $store.Close() }
        $script:importedCert = $cert
        $thumbprint = $cert.Thumbprint
    }
    if (-not $thumbprint) {
        throw "Для -Sign задайте OFFLOAD_SIGN_PFX_BASE64 и OFFLOAD_SIGN_PFX_PASSWORD или OFFLOAD_SIGN_THUMBPRINT"
    }
    $script:signArgs = @("sign", "/fd", "SHA256", "/tr", $timestamp, "/td", "SHA256", "/s", "My", "/sha1", $thumbprint)
}

function Remove-SigningCert {
    if (-not $script:importedCert) { return }
    try {
        $store = New-Object Security.Cryptography.X509Certificates.X509Store("My", "CurrentUser")
        $store.Open("ReadWrite")
        try { $store.Remove($script:importedCert) } finally { $store.Close() }
        # Удаление из хранилища не удаляет закрытый ключ (PersistKeySet) — удаляем контейнер ключа явно.
        $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($script:importedCert)
        if ($rsa -is [Security.Cryptography.RSACng]) { $rsa.Key.Delete() }
        elseif ($rsa -is [Security.Cryptography.RSACryptoServiceProvider]) { $rsa.PersistKeyInCsp = $false; $rsa.Clear() }
    } catch {
        Write-Warning "Не удалось удалить временный сертификат подписи из хранилища: $_"
    }
    $script:importedCert = $null
}

function Invoke-Sign([string]$file) {
    & $signtool @signArgs $file
    if ($LASTEXITCODE -ne 0) { throw "Не удалось подписать $file" }
}

try {
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
    if ($Sign) {
        Initialize-Signing
        Write-Host "== Подпись" -ForegroundColor Cyan
        Invoke-Sign $exe
    }
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "Готово: $exe ($size МБ)" -ForegroundColor Green

    if ($Installer) {
        Write-Host "== Установщик" -ForegroundColor Cyan
        # Версия закреплена в installer\Offload.iss (#define InnoSetupVersion): другой компилятор остановит сборку с подсказкой.
        # $env:ISCC — явный путь (CI ставит закреплённую версию); затем Inno Setup 7, и только потом — что есть в PATH.
        $iscc = @(
            $env:ISCC,
            "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
            (Get-Command iscc -ErrorAction SilentlyContinue).Source
        ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
        if (-not $iscc) { throw "Не найден Inno Setup (ISCC.exe). Установите: winget install JRSoftware.InnoSetup.7 --version 7.1.0" }
        $version = (Get-Item $exe).VersionInfo.ProductVersion.Split('+')[0]
        $isccArgs = @("/DAppVersion=$version", "/DPublishDir=$publishDir")
        if ($signArgs) {
            # Inno Setup подписывает установщик и деинсталлятор: $q — кавычка, $f — путь к файлу. Секретов в команде нет.
            $isccArgs += "/Soffloadsign=`$q$signtool`$q $($signArgs -join ' ') `$f"
            $isccArgs += "/DSignInstaller"
        }
        & $iscc @isccArgs installer\Offload.iss
        if ($LASTEXITCODE -ne 0) { throw "Сборка установщика не удалась" }
        Write-Host "Установщик: $(Join-Path $root 'dist')" -ForegroundColor Green
    }
}
finally {
    Remove-SigningCert
}
