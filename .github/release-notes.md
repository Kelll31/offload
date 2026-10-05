**Offload** — приложение для Windows, которое запускает бесплатную локальную модель для программирования и подключает её к Claude Code, Cursor, VS Code, Codex и другим IDE как MCP-сервер. Облачный агент отдаёт ей рутину: чтение больших файлов и логов, тесты, шаблонный код, механические правки. Облачных токенов уходит меньше.

## Что нового в 1.0.5.1

- **Исправление:** на странице «Состояние» верхние карточки могли зачеркнуться красными крестами, а программа — показать ошибку `Rectangle ... cannot have a width or height equal to 0`. Теперь такие карточки и переключатели корректно пропускают отрисовку при нулевом размере.

## Что нового в 1.0.5

- **Модель с Hugging Face — прямо из строки поиска.** Начните вводить название на вкладке «Модели» — под списком появятся лучшие репозитории GGUF с хаба. Работают несколько слов («qwen coder»), ссылка на страницу, `owner/name` и `hf.co/owner/name:Q4_K_M`: Offload сразу откроет нужный квант.
- **Offload сам подбирает лучшую модель и параметры под ваш компьютер.** Карточка «Подобрано для вашего компьютера»: модель и квант, запасные варианты, быстрая модель для коротких задач, число слотов и тип KV-кэша — по расчёту памяти, скорости и качества, а не по зашитому списку. Сменили видеокарту или добавили ОЗУ — Offload пересчитает и подскажет. Сам ничего не скачивает и не переключает.
- **Роли и команда ролей.** Локальная модель может быть ревьюером, тестировщиком, отладчиком, аудитором безопасности, документатором, мигратором… 20 встроенных ролей с наследованием; облачный агент создаёт свои (`local_roles`), назначает роль (`role=`) и запускает команду ролей над одними файлами (`local_team`) — с общим отчётом и итогом.
- **18 пресетов правил для языков:** C#, Python, TypeScript, React, Go, Rust, Java, Kotlin, C++, C, PHP, Swift, Ruby, SQL, Shell, Unity, Godot, Delphi.
- **Восемь новых слэш-команд:** `team`, `define_role`, `docs`, `migrate`, `perf`, `release_notes`, `debug`, `pr`.
- **Инструкции по делегированию для OpenCode, Windsurf, Cline и Roo Code** — как уже было для Codex и Gemini CLI.

## Что нового в 1.0.4

- **Поставил Offload — и он сам подключился к Claude.** Claude Code и Claude Desktop (в том числе из Microsoft Store), установленные до или после Offload, подключаются без единого клика; про «позже» Offload узнаёт сам — проверяет раз в 15 минут.
- **Подключение проверяется, а не предполагается.** Offload запускает `Offload.exe --mcp` так же, как это сделает Claude, и показывает итог: «проверено, 25 инструментов, 1,2 с» либо причину (антивирус, занятый или повреждённый файл настроек, посторонний текст в stdout) и что делать.
- **Ваш отказ важнее автоматики.** Сняли галочку в мастере или нажали «Отключить» — автоподключение это не вернёт. Чужая запись `offload` не заменяется, Claude сам не перезапускается.
- **Обновление не ломает подключение:** в Claude прописывается путь установленной копии Offload, он не меняется при обновлении.
- Выключается одной настройкой: «Интеграции» → «Подключать Claude автоматически и следить за подключениями к IDE».
- **Новый облик.** Фирменная индиго-палитра с градиентами, скруглённые кнопки и переключатели в стиле Windows 11, карточки с мягкой тенью, шапка раздела со значком и состоянием сервера и Claude, новая боковая панель. Новые схемы «Аврора» и «Графит», а также «Как акцент Windows».
- **Палитра команд Ctrl+K** — найти любой раздел или действие по паре букв. **Ctrl+Alt+O** открывает Offload из любой программы, **F1** — все сочетания клавиш.
- **Центр уведомлений и «тихие часы».** Всё, что Offload сообщал, собрано в одном разделе со счётчиком непрочитанных; ночью всплывающие окна не мешают.
- **Карточка «Claude» на главной** и ежедневная проверка: Offload сам раз в сутки убеждается, что подключения работают, и скажет, только если что-то сломалось. Меню «Claude» в трее — проверить и подключить в один щелчок.
- **Автоподключение Cursor, Windsurf, VS Code и других** — по вашему выбору на странице «Интеграции».
- **Откат настроек IDE** из резервной копии, которую Offload делает перед каждой правкой.
- **Тренды в статистике** («+35 % к прошлой неделе») и экономия за сегодня прямо в подсказке значка.
- **Перенос настроек** на другой компьютер: экспорт и импорт без ключей, путей и параметров безопасности.

## Что нового в 1.0.3

- **Автоподбор параметров под вашу видеокарту.** Одна кнопка на странице «Сервер»: Offload перебирает настройки llama-server, замеряет скорость и запоминает самый быстрый набор для этой модели и этого компьютера. Контекст не уменьшается; при отмене всё возвращается как было.
- **Несколько видеокарт.** Модель сама раскладывается по всем подходящим картам пропорционально свободной видеопамяти — большие модели влезают целиком.
- **Удалённый сервер.** Слабый ноутбук может пользоваться моделью, запущенной на мощном домашнем ПК: на ПК включите «Доступ из сети», на ноутбуке — «Удалённый сервер». Используйте доверенную сеть или VPN: трафик не шифруется.
- **Автодополнение в IDE (локальный Copilot).** Маленькая coder-модель на отдельном сервере для расширений llama.vscode и Continue; Continue настраивается сам.
- **Любая модель с Hugging Face.** Поиск GGUF прямо в программе с оценкой «поместится ли» и рекомендуемым квантом — новые модели доступны, не дожидаясь обновления Offload.
- **Новые инструменты:** `local_pr_ready` проверяет ветку перед PR одним вызовом (конфликты, тесты, секреты, ревью, черновик PR), `local_debug` проходит путь от воспроизведения ошибки до проверенного исправления.
- **Гонка агентов:** `race=2…4` у `local_agent_task` и `local_solve` — несколько локальных агентов решают задачу разными способами, вливается лучший результат.
- **Не думает дважды.** Ответы локальной модели по неизменённому коду берутся из кэша мгновенно, а неочевидные уроки из работы агента сами попадают в память проекта.
- **`local_impact` подсказывает тесты по смыслу.** Кроме тестов, найденных по ссылкам и именам, показываются тесты, близкие к изменению по смыслу (нужна модель эмбеддингов). Они помечены «semantic» и не запускаются автоматически при `run_tests` — это подсказка, что ещё стоит проверить.
- **Надёжнее выпуск.** Установщик собирается закреплённой версией Inno Setup и локально, и в GitHub Actions: ошибка, из-за которой первая сборка 1.0.2 не прошла, больше не повторится.

## В версии 1.0.2

- **Обновление больше не спотыкается об открытые IDE.** Установщик сам закрывает MCP-серверы Offload, которые запустили Claude Code, Cursor и другие IDE (раньше замена файла падала с «Отказано в доступе»); то же — при удалении программы. Самообновление через установщик теперь проходит, даже если IDE открыты.
- **«Что нового» после обновления:** уведомление и этот список в разделе «О программе».
- **Поиск по коду находит связанные тесты по смыслу,** а не только по именам классов (нужна модель эмбеддингов). Память проекта (`local_memory`) сортируется реранкером, если он назначен.
- **Удаление программы не трогает автозапуск другой копии Offload.**

## В версии 1.0.1

- **Самообновление.** Offload сам проверяет новые версии на GitHub и обновляется в два щелчка («О программе» → «Обновить»): файл проверяется по SHA-256 и подписи. Версия 1.0.0 так не умеет — установите 1.0.1 поверх неё, дальше обновления придут сами.
- **Быстрее и точнее поиск по коду.** Постоянный индекс проекта на диске, общий для всех IDE; C# разбирается компилятором Roslyn; `local_find_context` ранжирует по BM25, а с моделью эмбеддингов — по смыслу (векторы + реранкер).
- **Несколько моделей.** Быстрая модель для коротких задач, модели эмбеддингов и реранка; в каталоге — Qwen3-Embedding и BGE Reranker. Справедливая очередь GPU: короткие запросы IDE не ждут долгую задачу агента.
- **Автоматическая настройка под видеокарту.** Контекст и выгрузка экспертов MoE считаются по свободной видеопамяти; подсказка числа параллельных запросов и замер скорости модели.
- **Надёжнее llama.cpp.** Сторож зависания, закрепление версии и автоматический откат неудачного обновления.
- **Фоновые задачи агента** выполняются в трее и не прерываются, когда закрываете IDE.
- **Интеграции.** «Доктор» проверяет подключение к каждой IDE; путь к Offload в конфигах IDE чинится сам после перемещения программы; один пункт разрешает инструменты записи во всех поддерживаемых клиентах; инструкции для Codex и Gemini CLI; Claude Code в WSL; MCP по HTTP.
- **Удобства.** Дашборд экономии с выбором периода и экспортом CSV, пакет диагностики для сообщений об ошибках, установщик на английском, токен Hugging Face, зеркала и прокси.
- **Исправления.** Программа теперь находит свою установку (защита от второй копии), портативная копия больше не перехватывает автозапуск, ручные правки `config.json` не затираются, резервные копии и журналы не растут бесконечно.

Полный список — в [CHANGELOG.md](https://github.com/Kelll31/offload/blob/main/CHANGELOG.md).

## Скачать

| Файл | Для кого |
|---|---|
| **`Offload-Setup-<версия>.exe`** | Установщик. Подходит большинству: ярлык в «Пуске», автозапуск с Windows, удаление через «Параметры → Приложения». Права администратора не нужны. |
| `Offload.exe` | Портативная версия: один файл, запускается без установки. |
| `SHA256SUMS.txt` | Контрольные суммы, если хотите проверить файлы. |

Файлы лежат ниже, в разделе **Assets**.

## Как начать

1. Скачайте и запустите установщик.
2. Откроется мастер настройки. Он определит видеокарту и объём памяти, сам скачает llama.cpp и подходящую модель, затем подключит найденные IDE.
3. Перезапустите Claude Code (или выполните `/mcp`). В списке появится сервер `offload`.

> **Windows SmartScreen** может предупредить, что приложение неизвестно, если файлы не подписаны сертификатом. Нажмите «Подробнее» → «Выполнить в любом случае».
> Подлинность файла можно проверить по `SHA256SUMS.txt` или командой `gh attestation verify <файл> --repo Kelll31/offload` — она подтверждает, что файл собран в GitHub Actions из этого репозитория.

## Требования

- Windows 10 1809+ или Windows 11, x64.
- Желательна видеокарта NVIDIA, AMD или Intel с 8+ ГБ видеопамяти. На процессоре тоже работает, но медленно.
- 10–30 ГБ свободного места под модель.
- Docker, WSL, Python и Node.js не нужны.

Подробности (инструменты MCP, ручное подключение, где лежат файлы) — в [README](https://github.com/Kelll31/offload#readme).

---

## English

**Offload** is a Windows tray app that runs a free local coding model and plugs it into Claude Code, Cursor, VS Code, Codex and other IDEs as an MCP server, so the cloud agent can hand off routine work — reading large files and logs, tests, boilerplate, mechanical edits — and spend fewer cloud tokens.

- **`Offload-Setup-<version>.exe`** — installer for most users (per-user, no admin rights; the language follows Windows). Silent install: `/VERYSILENT`.
- **`Offload.exe`** — portable single file.
- **`SHA256SUMS.txt`** — checksums. Verify provenance with `gh attestation verify <file> --repo Kelll31/offload`.

**New in 1.0.5:** search Hugging Face right from the model search box on the Models tab (several words, links, `owner/name`, `hf.co/owner/name:Q4_K_M`); Offload picks the best model and server settings for your computer by itself (a "Picked for your computer" card, re-run when the hardware changes, it never downloads or switches anything on its own); roles for the local model with inheritance — 20 built-in roles, `local_roles` to manage them, `role=` on `local_ask_files`, `local_review_diff`, `local_agent_task` and `local_solve`, and `local_team` to run several roles over the same files with a lead summary; 18 language rule presets; eight new slash commands (`team`, `define_role`, `docs`, `migrate`, `perf`, `release_notes`, `debug`, `pr`); delegation instructions for OpenCode, Windsurf, Cline and Roo Code.

**New in 1.0.4:** Offload connects itself to Claude — Claude Code and Claude Desktop (including the Microsoft Store build), installed before or after Offload, are connected without a click (checked every 15 minutes); the connection is verified by launching `Offload.exe --mcp` like Claude does, with the result or the reason and what to do; your refusals are remembered and someone else's `offload` entry is never replaced; the path written into Claude is the installed copy's, so updates don't break it. One switch turns it off: Integrations → "Connect Claude automatically and watch IDE connections". Plus a new look (indigo gradients, Windows 11-style rounded buttons and toggles, soft-shadow cards, a page header with server and Claude status, new "Aurora", "Graphite" and "Windows accent" schemes), a Ctrl+K command palette, a global Ctrl+Alt+O hotkey and F1 shortcut help, a notification center with quiet hours, a Claude card on the Status page with a daily connection check and a Claude menu in the tray, opt-in auto-connect for Cursor, Windsurf, VS Code and other IDEs, restoring IDE configs from Offload's backups, trends against last week and today's savings in the tray tooltip, and settings export/import without keys, paths or security settings.

**New in 1.0.3:** one-click auto-tuning of llama-server for your GPU (the fastest set is saved per model and PC); automatic model split across several GPUs; remote server mode — a laptop can use the model running on a powerful PC with Offload (trusted network or VPN, plain HTTP); IDE autocomplete via a small FIM coder model for llama.vscode and Continue; search and install any GGUF model from Hugging Face with a fit estimate; new tools local_pr_ready (pre-PR check in one call) and local_debug (from reproduction to a verified fix); agent race (race=2…4 on local_agent_task and local_solve); a result cache for unchanged code and automatic project memory; local_impact also suggests tests related to the change by meaning (with an embedding model; marked semantic and never run by run_tests); the installer is now built with a pinned Inno Setup version locally and in CI.

**New in 1.0.2:** the installer now closes Offload MCP servers started by your IDEs (previously replacing the file failed with "Access denied" while an IDE was open), so updates and uninstall work with IDEs running; a "What's new" notice after updating; semantic related tests in local_find_context and reranked project memory; uninstalling no longer removes another copy's autostart entry.

**New in 1.0.1:** self-update from GitHub Releases (SHA-256 and signature checked; install 1.0.1 over 1.0.0 once, later versions arrive automatically), a persistent code index with Roslyn for C# and semantic search (embeddings + reranker), fast/embedding/rerank model roles with a fair GPU queue, automatic GPU-aware placement, llama.cpp version pinning with rollback, background agent jobs that survive closing the IDE, an integration doctor, one switch for write-tool approvals, MCP over HTTP and Claude Code in WSL, plus many fixes.

Run the installer; the setup wizard detects your GPU, downloads llama.cpp and a model that fits, and connects the IDEs it finds. Restart Claude Code (or run `/mcp`) and the `offload` server appears. Requirements: Windows 10 1809+/11 x64, preferably a GPU with 8+ GB VRAM, 10–30 GB of free disk space. See the [README](https://github.com/Kelll31/offload/blob/main/README.en.md) and [CHANGELOG](https://github.com/Kelll31/offload/blob/main/CHANGELOG.md).
