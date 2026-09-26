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
- Тесты: каждый — в своём `Sandbox`; живые проверки с настоящим `claude.exe` — только `OFFLOAD_LIVE_CLI=1`.
