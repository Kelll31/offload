**Offload** — приложение для Windows, которое запускает бесплатную локальную модель для программирования и подключает её к Claude Code, Cursor, VS Code, Codex и другим IDE как MCP-сервер. Облачный агент отдаёт ей рутину: чтение больших файлов и логов, тесты, шаблонный код, механические правки. Облачных токенов уходит меньше.

## Что нового в 1.0.2

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

**New in 1.0.2:** the installer now closes Offload MCP servers started by your IDEs (previously replacing the file failed with "Access denied" while an IDE was open), so updates and uninstall work with IDEs running; a "What's new" notice after updating; semantic related tests in local_find_context and reranked project memory; uninstalling no longer removes another copy's autostart entry.

**New in 1.0.1:** self-update from GitHub Releases (SHA-256 and signature checked; install 1.0.1 over 1.0.0 once, later versions arrive automatically), a persistent code index with Roslyn for C# and semantic search (embeddings + reranker), fast/embedding/rerank model roles with a fair GPU queue, automatic GPU-aware placement, llama.cpp version pinning with rollback, background agent jobs that survive closing the IDE, an integration doctor, one switch for write-tool approvals, MCP over HTTP and Claude Code in WSL, plus many fixes.

Run the installer; the setup wizard detects your GPU, downloads llama.cpp and a model that fits, and connects the IDEs it finds. Restart Claude Code (or run `/mcp`) and the `offload` server appears. Requirements: Windows 10 1809+/11 x64, preferably a GPU with 8+ GB VRAM, 10–30 GB of free disk space. See the [README](https://github.com/Kelll31/offload/blob/main/README.en.md) and [CHANGELOG](https://github.com/Kelll31/offload/blob/main/CHANGELOG.md).
