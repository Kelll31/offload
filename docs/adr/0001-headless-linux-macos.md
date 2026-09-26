# ADR 0001. Headless-режим для Linux и macOS

- **Статус:** предложено (исследование), решение не принято.
- **Дата:** 2026-09-25.
- **Связано:** [ROADMAP.md](../../ROADMAP.md), раздел 9.4.

## Контекст

Offload — приложение для Windows: трей на WinForms, установщик Inno Setup, определение железа через реестр и WinAPI.
При этом большая часть ценности — MCP-сервер (`Offload.exe --mcp`) и управление llama-server — от Windows почти не зависит.
Пользователи Claude Code на Linux и macOS (и в WSL без Windows-хоста) сейчас не могут использовать Offload.

## Что уже переносимо

Без изменений работают на любой ОС (чистый .NET, без WinAPI):

- `Offload.Llama`: `LlamaServerArgs`, `LlamaClient`, `ChatStream`, разбор релизов (`GitHubReleases`, `AssetSelector`);
- `Offload.Models`: `ModelCatalog`, `FitCalculator`, `ServerFit`, `HfClient`, `GgufReader`, `RemoteCatalog`, `Ed25519`;
- `Offload.Core`: `HttpDownloader`, `DownloadPolicy`, `ConfigStore`, `Log`, `UsageLog`, локализация;
- `Offload.Mcp`: почти все инструменты, `PathGuard` (кроме Windows-специфичных проверок: UNC, ADS, имена устройств,
  `GetFinalPathNameByHandle`), `CodeIndex` (SQLite есть для всех ОС), Roslyn.

## Что завязано на Windows

| Компонент | Зависимость | Замена на Linux/macOS |
|---|---|---|
| `Offload.App` (трей, окна, мастер) | WinForms | не переносится; headless-режим без UI |
| `HardwareDetector` | реестр, `GlobalMemoryStatusEx`, PDH | `nvidia-smi`, `rocm-smi`, `/proc/meminfo`, `sysctl hw.memsize`, `system_profiler` |
| `VcRuntimeCheck` | VC++ Redistributable | не нужен |
| `JobObject` | Job Objects | группы процессов (`setsid` + `kill(-pgid)`), `prctl(PR_SET_PDEATHSIG)` на Linux |
| IPC трея | именованный канал `CurrentUserOnly` | Unix domain socket в `$XDG_RUNTIME_DIR` с правами 0600 |
| `PathGuard` | UNC, ADS, `CON/NUL`, reparse points | `realpath`, `lstat`, запрет `/proc`, `/sys`, `/dev` |
| `VerifyCommand` | `cmd.exe /d /s /c` | `execve` с разобранными аргументами без оболочки |
| Сборки llama.cpp | `win-*.zip`, `llama-server.exe` | `ubuntu-{x64,vulkan,cuda,rocm,sycl}`, `macos-arm64` (`.tar.gz`) |
| Интеграции IDE | пути `%APPDATA%` | `~/.config/…`, `~/Library/Application Support/…` (форматы те же) |
| DPAPI (токен HF) | `CryptProtectData` | libsecret / Keychain или файл с правами 0600 |

## Варианты

1. **Headless только MCP + CLI-установщик.** Новый проект `Offload.Cli` (`net10.0`): `offload setup` (выбор сборки и модели
   в терминале), `offload server start|stop|status` (демон под systemd user unit / launchd agent вместо трея),
   `offload --mcp`, `offload integrations add|remove`. Библиотеки переводятся на `net10.0` (без `-windows`), Windows-код —
   за интерфейсами (`IHardwareProbe`, `IProcessGroup`, `IIpcTransport`, `ISecretStore`).
2. **Полный порт UI на Avalonia.** Один интерфейс для трёх ОС. Большой объём работы (около 11 тыс. строк UI), риск регрессий
   в Windows-версии, сложнее одиночный exe.
3. **Не делать.** Рекомендовать Linux-пользователям подключаться к Offload на Windows-машине по `--mcp-http` (он уже есть).

## Предлагаемое решение

Вариант 1, поэтапно:

1. Вынести Windows-зависимости за интерфейсы, не меняя поведения на Windows (тесты — прежние).
2. Перевести `Offload.Core/Llama/Models/Mcp/Integrations/OpenCode` на `net10.0`, оставив `Offload.App` на `net10.0-windows`.
3. `Offload.Cli` с командами выше и сборкой `linux-x64`, `linux-arm64`, `osx-arm64` в CI (single-file, без трея).
4. Linux-специфичные защиты `PathGuard` + отдельный набор `SecurityRegressionTests` на Linux-раннере.

Прежде чем начинать, стоит оценить спрос (issues, опрос) — вариант 3 уже покрывает сценарий «Linux-клиент, Windows-GPU».

## Последствия

- Плюс: MCP-инструменты и локальная модель на всех ОС; CI на трёх ОС ловит случайные зависимости от Windows.
- Минус: второй способ установки и управления сервером; тесты защиты нужно дублировать для POSIX-путей.
