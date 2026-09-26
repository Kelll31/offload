# Сборка, установщик, CI

- Общие свойства — `Directory.Build.props` (net10.0-windows, Nullable, ImplicitUsings, `Version`, ru-RU). Тестовые — `tests/Directory.Build.props`
  (xunit.v3, `OutputType=Exe`). Новый проект — добавить в `Offload.slnx` и при необходимости `InternalsVisibleTo` в csproj.
- Публикация: `scripts/build.ps1` → single-file, self-contained, ReadyToRun, compressed, `win-x64` → `publish\Offload.exe` (~60 МБ).
  Установщик — Inno Setup (`installer/Offload.iss`, версия передаётся `/DAppVersion`) → `dist\Offload-Setup-<версия>.exe`.
- **Одна установка на компьютер.** `AppGuid` в `installer/Offload.iss` = `AppInfo.InstallerAppId` (тест `InstallerTests`) — не менять.
  Установщик только «для себя» (`PrivilegesRequired=lowest`, без `PrivilegesRequiredOverridesAllowed`); в `[Code]` находит прежнюю
  установку (HKCU и HKLM) и предлагает обновить / переустановить / удалить; обновление — строго в ту же папку.
  Программа при запуске не из папки установки (`InstallGuard`) предлагает открыть / обновить / удалить установленную копию,
  а такая «чужая» копия (`InstallInfo.Foreign`) не переписывает пути в IDE и автозапуск.
- Ветки установщика — на тестовой записи: `ISCC /DAppGuid=<тестовый GUID> /FOffload-Setup-test`.
- Скрипты PowerShell — совместимы с Windows PowerShell 5.1, сообщения по-русски, `$ErrorActionPreference = "Stop"`, проверка `$LASTEXITCODE`.
- CI (`.github/workflows/build.yml`, windows-latest): тесты → Inno Setup → публикация → артефакты; на тег `v*` — SHA256SUMS и релиз
  с телом из `.github/release-notes.md`. Релиз — тег `v<Version>` после обновления `Directory.Build.props` и `CHANGELOG.md`.
- Зависимости NuGet добавлять осознанно: exe самодостаточный, каждая библиотека увеличивает размер и поверхность атаки.
