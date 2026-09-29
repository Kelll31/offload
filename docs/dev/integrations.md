# Интеграции с IDE (Offload.Integrations)

Мы правим **чужие** конфиги пользователя. Любая порча = потерянные настройки IDE. Поэтому:

- Пути — только через `IntegrationEnvironment` / `ClientLocations`, никогда `Environment.GetFolderPath` напрямую
  (иначе песочница тестов не работает и `RealProfileGuard` роняет прогон).
- Запись — только через `ConfigFile.Edit`: бэкап перед записью (`AppPaths.BackupsDir`), атомарная замена, повтор при гонке
  (сравнение SHA-256 между чтением и записью, `ConfigBusyException`), `WriteOutcome.Unchanged` при отсутствии изменений.
- JSON/JSONC — точечные правки `JsoncEditor` с сохранением комментариев, порядка ключей, отступов и концов строк; результат
  сверяется с независимым разбором. Полная пересериализация — только для файлов без комментариев. TOML — `TomlPatcher`.
- Строго UTF-8: UTF-16 / NUL-байты → `ConfigReadException`, файл не трогаем. BOM не возвращаем.
- Наша запись распознаётся по `CommandPath.IsOffload`; перезаписываем только `OwnedKeys` (по умолчанию `type/command/args`),
  чужие ключи пользователя (`disabled`, `timeout`…) сохраняем; `DroppedKeys` — явно удаляемые.
- Регистрация и отмена — идемпотентны; отмена удаляет только нашу запись.
- Новая IDE — запись в `KnownIntegrations`, пути в `ClientLocations`, тест полного цикла в `Sandbox`.
- Claude Code-дополнения (`Claude/ClaudeExtrasImpl.cs`, `ClaudeTexts.cs`) пишут в `~/.claude/` пользователя: скил `offload`,
  правило `rules/offload.md`, агент `offload-runner`, разрешения в `settings.json` — с маркером `x-offload: managed`.
  Файлы без маркера (созданные пользователем) не перезаписываем.
- Автоподключение Claude (`AutoConnect/`: `AutoConnectPolicy` — чистое решение, `AutoConnectEngine` — прогон; в приложении его гоняет
  `Offload.App/Services/ClaudeAutoConnect` раз в 15 минут): пишет **только** через `RegisterAsync`, только при статусе `NotRegistered`,
  не трогает `DeclinedIntegrations` и уже отслеживаемые IDE; после записи — `IntegrationVerifier` (запуск команды из записи, ретраи
  2/5/10 с, причина сбоя — `FailureKind` с текстом «что делать» в `FailureText`). Откат записи при неудачной проверке не делается.
  Отказ пользователя: `cfg.Decline(id)` / `cfg.Undecline(id)`; сведения о подключении и итог проверки — `cfg.IntegrationStates`.
  Логика автоподключения проверяется в `Offload.Integrations.Tests` (без WinForms): `AutoConnectTests`, `IntegrationVerifierTests`.
- В IDE пишется путь установленной копии (`McpServerSpec.ForInstalledOrCurrent`, в приложении — `InstallInfo.McpSpec()`); все места
  регистрации используют его, а не `ForCurrentExecutable`.
- Тесты: каждый — в своём `Sandbox`; живые проверки с настоящим `claude.exe` — только `OFFLOAD_LIVE_CLI=1`.
