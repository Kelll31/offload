**Offload** — приложение для Windows, которое запускает бесплатную локальную модель для программирования и подключает её к Claude Code, Cursor, VS Code, Codex и другим IDE как MCP-сервер. Облачный агент отдаёт ей рутину: чтение больших файлов и логов, тесты, шаблонный код, механические правки. Облачных токенов уходит меньше.

Что нового — в [CHANGELOG.md](https://github.com/Kelll31/offload/blob/main/CHANGELOG.md).

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

Run the installer; the setup wizard detects your GPU, downloads llama.cpp and a model that fits, and connects the IDEs it finds. Restart Claude Code (or run `/mcp`) and the `offload` server appears. Requirements: Windows 10 1809+/11 x64, preferably a GPU with 8+ GB VRAM, 10–30 GB of free disk space. See the [README](https://github.com/Kelll31/offload/blob/main/README.en.md) and [CHANGELOG](https://github.com/Kelll31/offload/blob/main/CHANGELOG.md).
