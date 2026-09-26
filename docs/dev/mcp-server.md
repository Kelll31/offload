# MCP-сервер (Offload.Mcp)

- **stdout — только JSON-RPC.** Никаких `Console.Write*`; диагностика — `Log.*` (файл `logs/mcp-*.log`) или stderr. Хук `check-cs.mjs` ловит это.
- Раскладка: `OffloadTools.cs` — только объявления (атрибуты, `_meta`, description-константы); реализация — `Tools/<Имя>Tool.cs`
  (`internal static class`, `Task<string> RunAsync(ToolContext ctx)`); общая инфраструктура — `Infrastructure/*`.
- Каждый инструмент вызывается через `ToolRunner.RunAsync(имя, state, context, Impl.RunAsync, ct)` — он даёт прогресс, запись
  в `usage.jsonl`, подвал «≈N cloud tokens avoided», обрезку ответа и превращение исключений в `isError`.
  Ошибки для модели — `ToolException` с понятным английским текстом (что не так и как исправить).
- Аннотации `ReadOnly/Destructive/Idempotent/OpenWorld` задаются **все и явно** (Claude Code трактует отсутствующие иначе, чем спецификация).
- Тексты для модели (descriptions, `ServerInstructions`, сообщения `ToolException`) — **английский**, коротко, с примерами аргументов.
  `ServerInstructions` ≤ `MaxLength` (2048) — Claude Code обрезает дальше; важное в начале.
- Новое имя → `McpToolNames` + список `ReadOnly` или `Writing` (от этого зависят автоматические разрешения в Claude Code).
- Работа с моделью — только через `GpuQueue` (межпроцессные мьютексы: несколько IDE = несколько MCP-процессов на один GPU).
- Файлы — только через `PathGuard`/`FileGatherer` (рабочая папка, .gitignore, отказ для секретов/UNC/ADS/устройств/`..`).
  Проверочные команды — только через `VerifyCommand.Validate` (белый список, без операторов оболочки, без путей в имени).
- Записывающие инструменты делают снимок в `JobStore` до записи — чтобы `local_job revert` всегда работал.
- Тесты: `TestEnv` (временный дом + рабочая папка + `Context()`), `FakeLlamaServer` (поддельный OpenAI API), `EndToEndTests` —
  сквозной прогон. Любое изменение защиты — новый кейс в `GuardTests`/`SecurityRegressionTests`.
- Проверка собранного exe: `node scripts/mcp-smoke.mjs --strict publish\Offload.exe local_status "{}"`.
