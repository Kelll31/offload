# Как участвовать в Offload

Спасибо, что хотите помочь! Offload — открытый проект под лицензией [MIT](LICENSE). Подойдёт любая помощь: сообщение об ошибке,
отчёт о работе на вашем железе, новая модель в каталоге, поддержка IDE, перевод, документация, код.

Не знаете, с чего начать, — откройте [issue](https://github.com/Kelll31/offload/issues) с вопросом или посмотрите
[ROADMAP.md](ROADMAP.md): пункты с объёмом **S** хорошо подходят для первого PR.

## Сообщить об ошибке

Используйте шаблон «Ошибка» в Issues. Укажите версию Offload, видеокарту и драйвер, модель и сборку llama.cpp и приложите фрагмент
журнала `%LOCALAPPDATA%\Offload\logs\` (`app.log`, `llama-server.log`, `mcp.log`). **Удалите из журнала ключи API.**

Уязвимости — не в Issues, а приватно: см. [SECURITY.md](SECURITY.md).

## Сборка и тесты

Нужны Windows 10/11 x64 и [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0) (версия — в `global.json`).

```powershell
dotnet test --solution Offload.slnx -c Release                                   # все тесты (офлайн, без видеокарты)
dotnet test --project tests/Offload.Mcp.Tests/Offload.Mcp.Tests.csproj -c Release   # один проект
dotnet test --project tests/Offload.Mcp.Tests/Offload.Mcp.Tests.csproj -c Release --filter-class "*GuardTests"
.\scripts\build.ps1                                                              # тесты + publish\Offload.exe
.\scripts\build.ps1 -Installer                                                   # + установщик (Inno Setup 6/7)
node scripts\mcp-smoke.mjs --strict .\publish\Offload.exe                        # MCP-сервер глазами IDE
```

Тесты — xUnit v3 на Microsoft.Testing.Platform: фильтры `--filter-class` / `--filter-method` / `--filter-trait`
(VSTest-овский `--filter` не работает).

Живые тесты с сетью, GPU и настоящими моделями помечены `[Trait("Category","Live")]` и запускаются только с `OFFLOAD_LIVE=1`
(`OFFLOAD_TEST_LLAMA_BIN`, `OFFLOAD_TEST_MODEL`, `OFFLOAD_LIVE_CLI` — см. тесты).

> **Осторожно с dev-сборкой `Offload.exe` в режиме трея.** При старте трей направляет автозапуск Windows и пути в подключённых IDE
> на *запущенный* exe. Для ручной проверки задайте `$env:OFFLOAD_DEV = "1"` (трей тогда не трогает автозапуск и конфиги IDE)
> и отдельную папку данных `$env:OFFLOAD_HOME = "C:\temp\offload-dev"`. Режим `--mcp` безопасен.

## Устройство решения

| Проект | Что внутри |
|---|---|
| `Offload.Core` | пути (`AppPaths`, `OFFLOAD_HOME`), настройки (`ConfigStore`), журнал, загрузчик, железо, IPC трея, локализация |
| `Offload.Llama` | релизы и установка llama.cpp, `LlamaServer`, `LlamaClient` |
| `Offload.Models` | каталог `catalog.json`, `FitCalculator`, Hugging Face, чтение GGUF |
| `Offload.OpenCode` | изолированный OpenCode, неинтерактивный запуск агента, `ChangeTracker` |
| `Offload.Integrations` | правка конфигов IDE (`JsoncEditor`, `TomlPatcher`), дополнения для Claude Code |
| `Offload.Mcp` | MCP-сервер: `OffloadTools` (объявления) → `Tools/*` (реализация) → `Infrastructure/*` |
| `Offload.App` | трей, окна (`Forms/Pages`), мастер (`Forms/Wizard`), `Program.cs` |

## Правила, которые проверяют тесты и ревью

Подробные правила по областям кода (MCP, интеграции, процессы, каталог, тесты, интерфейс, сборка) — в [docs/dev/](docs/dev/README.md).

**Инварианты — не ослаблять:**

- `--mcp` обрабатывается в `Program.Main` до WinForms; **stdout MCP-процесса — только JSON-RPC** (никаких `Console.WriteLine`).
- Файлы в MCP-инструментах — только через `PathGuard`; команды — только через `VerifyCommand` (белый список). См.
  [модель угроз](docs/threat-model.md). Новая защита — с тестом в `SecurityRegressionTests`.
- Конфиги IDE — только через `IntegrationEnvironment` + `ConfigFile.Edit` (бэкап, атомарная запись, комментарии сохраняются).
- llama-server — на `127.0.0.1` с ключом API; загрузки — с SHA-256.
- Тесты не трогают реальный профиль: `TempHome` + `[Collection("AppPaths")]`, в интеграциях — `Sandbox` (`RealProfileGuard` упадёт,
  если профиль изменился).

**Язык:**

- Комментарии, XML-doc, журнал, исключения, скрипты — **по-русски**.
- Тексты интерфейса и видимых пользователю исключений — через `L.T` / `L.F` / `Ui.Plural`, с английским переводом в
  `src/Offload.Core/Localization/en/*.json` (ключ — русский текст). Полноту проверяет `LocalizationCoverageTests`.
- Описания MCP-инструментов, `ServerInstructions`, тексты `ToolException` — **по-английски** (их читает модель).

**Стиль C#:** Nullable, file-scoped namespace, primary constructors, `sealed` по умолчанию, `internal` + `InternalsVisibleTo` вместо
`public` ради тестов. Git нормализует концы строк в LF; не «исправляйте» CRLF массово.

**Зависимости NuGet** добавляйте осознанно: exe самодостаточный, каждая библиотека увеличивает его размер и поверхность атаки.

## Частые задачи

- **Модель в каталог** — `src/Offload.Models/catalog.json`: закреплённая ревизия Hugging Face, размеры и SHA-256 каждого файла,
  параметры архитектуры из заголовка GGUF, сэмплинг и лицензия из карточки модели, описание на русском и английском.
  Тесты `Offload.Models.Tests` проверяют схему.
- **Новая IDE** — запись в `src/Offload.Integrations/Clients/KnownIntegrations.cs` (путь к конфигу, формат, ключ-контейнер, форма записи)
  и путь в `ClientLocations.cs`; тест полного цикла «подключить → отключить» в песочнице есть для каждого клиента.
- **Новый MCP-инструмент** — имя в `Offload.Core/McpToolNames.cs`, объявление в `src/Offload.Mcp/OffloadTools.cs`, реализация
  в `src/Offload.Mcp/Tools/`, тесты в `tests/Offload.Mcp.Tests`, строка в README и в текстах для Claude Code (`ClaudeTexts`).
- **Страница интерфейса** — наследник `PageBase`, элементы из `Kit`, цвета из `Theme`; долгие операции — асинхронно, без блокировки
  UI-потока. Приложите к PR скриншоты в светлой и тёмной теме.

## Pull request

1. Форк и ветка от `main`.
2. Тесты зелёные: `dotnet test --solution Offload.slnx -c Release`.
3. Для изменений MCP — `node scripts/mcp-smoke.mjs --strict publish\Offload.exe` после `.\scripts\build.ps1 -SkipTests`.
4. Запись в `CHANGELOG.md` в раздел «Не выпущено».
5. В описании PR — что и зачем; для интерфейса — скриншоты.

Участвуя в проекте, вы соглашаетесь с [правилами поведения](CODE_OF_CONDUCT.md).
