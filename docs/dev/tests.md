# Тесты (xUnit v3 + Microsoft.Testing.Platform)

- Каждый тестовый проект — `OutputType=Exe` (общий `tests/Directory.Build.props`), `using Xunit` глобально.
- Запуск: `dotnet test --project tests/<Проект>/<Проект>.csproj -c Release --filter-class "*Имя"` (`--filter-method`, `--filter-trait`).
  VSTest-овский `--filter "FullyQualifiedName~..."` здесь не работает. 
- Отмена: `TestContext.Current.CancellationToken`; вывод: `TestContext.Current.TestOutputHelper`.
  Пропуск: `Assert.Skip(...)` / `Assert.SkipUnless(cond, "...")` (не `[Fact(Skip=...)]` для условных пропусков).
- Изоляция данных: всё, что трогает `AppPaths`/`ConfigStore`/статические URL, — `using var home = new TempHome();` и класс
  с `[Collection("AppPaths")]` (коллекция без параллелизма; определение — в каждом тестовом проекте).
- Помощники по проектам:
  - Mcp: `TestEnv` (дом + рабочая папка, `WriteFile`, `PathOf`, `Context()`), `FakeLlamaServer` (OpenAI API, `UserText`/`SystemText`).
  - Llama: `FakeHttpServer` (обработчик запроса, `WriteResponseAsync`/`WriteSseHeadersAsync`), `TestEnv.RequireLlama*`, фикстуры релизов `Fixtures/b*.json`.
  - Models: `LocalHub` (поддельный Hugging Face: `AddFile(repo, path, data, sha256)`, `AddTreeEntry`), `GgufWriter` (синтетические GGUF).
  - Integrations: `Sandbox` на каждый тест; `RealProfileGuard` (assembly fixture) падает, если изменился реальный профиль.
- Сеть, GPU, настоящие модели — только в `[Trait("Category","Live")]` за `OFFLOAD_LIVE=1` (`OFFLOAD_LIVE_HOME`, `OFFLOAD_LIVE_CLI`,
  `OFFLOAD_TEST_LLAMA_BIN`, `OFFLOAD_TEST_MODEL`). Обычный прогон обязан проходить офлайн.
- Имена: `Метод_Условие` / `Сценарий_Ожидание` на английском, сообщения ассертов — по-русски, как в соседних тестах.
- Шаблонные тесты по спецификации можно отдать `mcp__offload__local_write_file` с `verify_command` = `dotnet test --project ... --filter-class ...`.
