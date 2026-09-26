# Процессы, сервер, загрузки

- Дочерние процессы — через `ProcessRunner` (короткие утилиты: git, nvidia-smi, claude, opencode; без окна) или с привязкой к
  `JobObject` (kill-on-close: при падении Offload llama-server/opencode не остаются сиротами). Без `cmd /c` и склейки строк —
  аргументы списком (`ArgumentList`). `UseShellExecute=true` — только открыть ссылку/папку, UAC `runas`, новое окно консоли.
- llama-server: `--host` по умолчанию `127.0.0.1`, порт **всегда явно**, ключ API из конфига — переменной окружения `LLAMA_API_KEY` (не в командной строке); аргументы собираются в `LlamaServer`
  (тесты — `ServerArgsTests`). Жизненный цикл (автозапуск, перезапуск при сбое, выгрузка при простое) — `ServerController` в App.
- Установка llama.cpp: релизы GitHub (`LlamaReleases`/`GitHubReleases`), выбор сборки — `BackendAdvisor`, маркер установки
  `.offload-install.json`; VC++ Runtime — `VcRuntimeCheck` (UAC). Фикстуры релизов — `tests/Offload.Llama.Tests/Fixtures`.
- Загрузки — `HttpDownloader`: `.part` + Range-докачка, повторы, SHA-256, атомарное переименование. Не писать сразу в итоговый файл.
- OpenCode изолирован: свой каталог `AppPaths.OpenCodeDir`, конфиг пишет `OpenCodeConfigWriter`, переопределения — через
  `OPENCODE_CONFIG_CONTENT`; глобальный конфиг пользователя (`~/.config/opencode`) — только по явной настройке и только через `OpenCodeGlobalConfig` (Integrations: `ConfigFile.Edit` + `JsoncEditor`). Запуск — `OpenCodeRunner`
  (неинтерактивно, `NO_COLOR=1`), очередь запусков — `RunQueueLock`, изменения файлов — `ChangeTracker`.
- `GpuQueue` (Offload.Mcp) — именованные мьютексы, а не семафор: при падении владельца Windows отдаёт слот (`AbandonedMutexException`).
- Кодировки вывода процессов задавать явно (UTF-8), ANSI-цвета чистить (`OutputCleaner`).
