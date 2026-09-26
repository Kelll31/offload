<#
.SYNOPSIS
    Подпись удалённого каталога моделей (catalog\catalog.json) ключом Ed25519 мейнтейнера через OpenSSL 3.

.DESCRIPTION
    Offload скачивает catalog.json и catalog.json.sig (base64 подписи Ed25519 точных байтов файла) и проверяет подпись
    открытым ключом, зашитым в сборку (RemoteCatalog.PublicKeyBase64 в src\Offload.Models\RemoteCatalog.cs).
    Закрытый ключ хранится только у мейнтейнера — никогда не кладите его в репозиторий.

    1. Однократно — создать пару ключей (эквивалент: openssl genpkey -algorithm ed25519 -aes-256-cbc -out offload-catalog.pem):
         .\scripts\sign-catalog.ps1 -GenerateKey -Key D:\keys\offload-catalog.pem
       Закрытый ключ шифруется (AES-256-CBC) паролем, который скрипт спросит дважды; при подписи пароль спрашивается
       снова и передаётся OpenSSL через переменную окружения дочернего процесса (не в командной строке).
       -NoEncryption создаёт незашифрованный ключ — только для ключа на зашифрованном/аппаратном носителе.
       Скрипт выведет открытый ключ — 32 байта в base64. Вставьте его в RemoteCatalog.PublicKeyBase64.
       Вручную открытый ключ получается так: openssl pkey -in offload-catalog.pem -pubout -outform DER -out pub.der —
       это 44 байта (SubjectPublicKeyInfo), последние 32 и есть ключ; его base64 и вставляется в код.
    2. Каждый выпуск каталога — увеличить "version" в catalog\catalog.json (ГГГГММДДNN) и подписать:
         .\scripts\sign-catalog.ps1 -Key D:\keys\offload-catalog.pem
       Рядом появится catalog\catalog.json.sig; закоммитьте оба файла вместе (подпись покрывает файл байт в байт —
       после подписи файл не править, в том числе концы строк: см. catalog\.gitattributes).
    3. Открытый ключ по закрытому:  .\scripts\sign-catalog.ps1 -PublicKey -Key D:\keys\offload-catalog.pem

    Нужен OpenSSL 3.0+ (ключ -rawin): есть в Git для Windows (C:\Program Files\Git\usr\bin\openssl.exe), путь можно
    указать параметром -OpenSsl.

.EXAMPLE
    .\scripts\sign-catalog.ps1 -Key D:\keys\offload-catalog.pem -Catalog catalog\catalog.json
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Key,
    [string]$Catalog,
    [string]$Out,
    [switch]$GenerateKey,
    [switch]$PublicKey,
    [switch]$NoEncryption,
    [string]$OpenSsl
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $Catalog) { $Catalog = Join-Path $root "catalog\catalog.json" }

function Find-OpenSsl {
    if ($OpenSsl) {
        if (-not (Test-Path $OpenSsl)) { throw "OpenSSL не найден: $OpenSsl" }
        return (Resolve-Path $OpenSsl).Path
    }
    $cmd = Get-Command openssl -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($candidate in @(
            (Join-Path $env:ProgramFiles "Git\usr\bin\openssl.exe"),
            (Join-Path $env:ProgramFiles "Git\mingw64\bin\openssl.exe"),
            (Join-Path $env:ProgramFiles "OpenSSL-Win64\bin\openssl.exe"))) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    throw "Не найден OpenSSL 3. Установите Git для Windows или OpenSSL и повторите (или укажите путь: -OpenSsl <путь к openssl.exe>)."
}

$ossl = Find-OpenSsl

function Invoke-OpenSsl {
    param([string[]]$Arguments)
    & $ossl @Arguments
    if ($LASTEXITCODE -ne 0) { throw "openssl $($Arguments[0]) завершился с кодом $LASTEXITCODE." }
}

$versionText = (& $ossl version)
if ($LASTEXITCODE -ne 0) { throw "Не удалось запустить OpenSSL: $ossl" }
if ($versionText -notmatch '^OpenSSL\s+([3-9]|\d{2,})\.') {
    throw "Нужен OpenSSL 3.0 или новее (ключ -rawin для Ed25519), найден: $versionText ($ossl)."
}
Write-Host "openssl: $ossl ($versionText)" -ForegroundColor DarkGray

# Пароль закрытого ключа передаётся OpenSSL через переменную окружения (не виден в командной строке процесса).
$passEnv = "OFFLOAD_CATALOG_KEY_PASS"
$script:passIn = @()

function Read-Passphrase {
    param([string]$Prompt, [switch]$Confirm)
    $plain = [Net.NetworkCredential]::new("", (Read-Host -AsSecureString $Prompt)).Password
    if ($Confirm) {
        if ($plain.Length -lt 12) { throw "Пароль ключа слишком короткий: нужно не меньше 12 символов." }
        $again = [Net.NetworkCredential]::new("", (Read-Host -AsSecureString "Повторите пароль")).Password
        if ($again -cne $plain) { throw "Пароли не совпадают." }
    }
    elseif ($plain.Length -eq 0) {
        throw "Пароль ключа не введён."
    }
    return $plain
}

# Открытый ключ Ed25519 в «сыром» виде (32 байта, base64) — именно его ждёт RemoteCatalog.PublicKeyBase64.
function Get-RawPublicKey {
    param([string]$KeyPath)
    $der = [IO.Path]::GetTempFileName()
    try {
        Invoke-OpenSsl (@("pkey", "-in", $KeyPath) + $script:passIn + @("-pubout", "-outform", "DER", "-out", $der))
        $bytes = [IO.File]::ReadAllBytes($der)
        # SubjectPublicKeyInfo Ed25519: 30 2a 30 05 06 03 2b 65 70 03 21 00 + 32 байта ключа.
        $prefix = "302a300506032b6570032100"
        $actual = -join ($bytes[0..11] | ForEach-Object { $_.ToString("x2") })
        if ($bytes.Length -ne 44 -or $actual -ne $prefix) { throw "Ключ $KeyPath — не Ed25519." }
        return [Convert]::ToBase64String($bytes, 12, 32)
    }
    finally {
        Remove-Item $der -Force -ErrorAction SilentlyContinue
    }
}

try {
if ($GenerateKey) {
    if (Test-Path $Key) { throw "Файл $Key уже существует — не перезаписываю ключ. Укажите другой путь." }
    $dir = Split-Path -Parent ([IO.Path]::GetFullPath($Key))
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    if ($NoEncryption) {
        Write-Warning "Закрытый ключ будет записан БЕЗ шифрования (-NoEncryption): любой, кто прочитает файл, сможет подписать каталог, который примут все копии Offload. Храните его только на зашифрованном или аппаратном носителе."
        Invoke-OpenSsl @("genpkey", "-algorithm", "ed25519", "-out", $Key)
    }
    else {
        Set-Item "Env:$passEnv" (Read-Passphrase "Пароль для закрытого ключа (не меньше 12 символов)" -Confirm)
        $script:passIn = @("-passin", "env:$passEnv")
        Invoke-OpenSsl @("genpkey", "-algorithm", "ed25519", "-aes-256-cbc", "-pass", "env:$passEnv", "-out", $Key)
    }
    $pub = Get-RawPublicKey $Key
    Write-Host ""
    Write-Host "Закрытый ключ: $Key — храните вне репозитория (менеджер паролей, аппаратный носитель)." -ForegroundColor Yellow
    if (-not $NoEncryption) { Write-Host "Ключ зашифрован паролем: без пароля подписать каталог нельзя — сохраните пароль отдельно от ключа." -ForegroundColor Yellow }
    Write-Host "Открытый ключ (вставьте в RemoteCatalog.PublicKeyBase64, src\Offload.Models\RemoteCatalog.cs):" -ForegroundColor Green
    Write-Output $pub
    return
}

if (-not (Test-Path $Key)) { throw "Закрытый ключ не найден: $Key (создать: -GenerateKey)." }

# Зашифрованный ключ (PKCS#8 «ENCRYPTED PRIVATE KEY») — пароль спрашивается один раз на все вызовы OpenSSL.
if ((Get-Content -LiteralPath $Key -Raw) -match 'ENCRYPTED') {
    Set-Item "Env:$passEnv" (Read-Passphrase "Пароль закрытого ключа $Key")
    $script:passIn = @("-passin", "env:$passEnv")
}
else {
    Write-Warning "Закрытый ключ $Key не зашифрован. Рекомендуется зашифровать: openssl pkey -in <ключ> -aes-256-cbc -out <новый ключ>."
}

if ($PublicKey) {
    Write-Output (Get-RawPublicKey $Key)
    return
}

if (-not (Test-Path $Catalog)) { throw "Каталог не найден: $Catalog" }
$Catalog = (Resolve-Path $Catalog).Path
if (-not $Out) { $Out = "$Catalog.sig" }

$text = [IO.File]::ReadAllText($Catalog, [Text.Encoding]::UTF8)
if ($text -match '"version"\s*:\s*(\d+)') {
    Write-Host "Версия каталога: $($Matches[1])"
}
else {
    throw "В каталоге нет поля ""version"": Offload применяет только каталог с версией выше встроенной."
}

$sigBin = [IO.Path]::GetTempFileName()
$pubPem = [IO.Path]::GetTempFileName()
try {
    Invoke-OpenSsl (@("pkeyutl", "-sign", "-rawin", "-inkey", $Key) + $script:passIn + @("-in", $Catalog, "-out", $sigBin))
    $sig = [IO.File]::ReadAllBytes($sigBin)
    if ($sig.Length -ne 64) { throw "Подпись Ed25519 должна занимать 64 байта, получено $($sig.Length) — ключ не Ed25519?" }

    # Самопроверка: подпись сверяется открытым ключом тем же способом, что и в Offload (точные байты файла).
    Invoke-OpenSsl (@("pkey", "-in", $Key) + $script:passIn + @("-pubout", "-out", $pubPem))
    Invoke-OpenSsl @("pkeyutl", "-verify", "-rawin", "-pubin", "-inkey", $pubPem, "-in", $Catalog, "-sigfile", $sigBin) | Out-Null

    $b64 = [Convert]::ToBase64String($sig)
    [IO.File]::WriteAllText($Out, $b64, (New-Object Text.ASCIIEncoding))
    Write-Host "Подпись записана: $Out" -ForegroundColor Green
    Write-Host "Открытый ключ: $(Get-RawPublicKey $Key)" -ForegroundColor DarkGray
    Write-Output $b64
}
finally {
    Remove-Item $sigBin, $pubPem -Force -ErrorAction SilentlyContinue
}
}
finally {
    # Пароль не остаётся в окружении сеанса PowerShell после работы скрипта.
    Remove-Item "Env:$passEnv" -ErrorAction SilentlyContinue
}
