# Безопасность / Security

## Как сообщить об уязвимости

**Не открывайте публичный issue.** Сообщите приватно через GitHub:
[Security → Report a vulnerability](https://github.com/Kelll31/offload/security/advisories/new).

Приложите:

- версию Offload (окно «О программе» или `Offload.exe` → «Свойства → Подробно»);
- шаги воспроизведения, лучше всего — минимальный репозиторий или запрос MCP (JSON), который вызывает проблему;
- что удалось сделать: прочитать файл, записать файл, выполнить команду, повысить права и т. п.;
- фрагмент журнала `%LOCALAPPDATA%\Offload\logs\` **без ключей API**.

Мы подтвердим получение в течение 7 дней, согласуем с вами срок исправления и дату раскрытия и укажем вас в заметках к релизу,
если вы не против.

## Поддерживаемые версии

Исправления безопасности выходят для последней выпущенной версии. Обновляйтесь до неё: установщик обновляет установку на месте,
настройки и модели сохраняются.

## Что считается уязвимостью

Offload выполняет действия по запросу облачной модели, которая может находиться под prompt injection из файлов проекта.
Поэтому уязвимость — это любой обход защит, описанных в [модели угроз](docs/threat-model.md). Например:

- чтение секретов (`.env`, ключи, `~/.ssh` …) или файлов вне рабочей папки через MCP-инструменты;
- запись вне рабочей папки или в защищённые файлы (`.git`, `.claude`, `.vscode`, `.github/workflows`, `CLAUDE.md` …);
- запуск команды не из белого списка `local_verify` или обход запрещённых аргументов;
- доступ к llama-server с другого компьютера или без ключа API;
- установка компонентов (llama.cpp, OpenCode, модели) без проверки SHA-256;
- доступ к IPC трея от имени другого пользователя;
- порча конфигурации IDE (потеря чужих записей или комментариев) — это скорее ошибка, но тоже присылайте.

**Не уязвимость** (это задокументированное поведение): `local_verify` и песочница агента выполняют код самого репозитория — сборку,
тесты, npm-скрипты. Не запускайте Offload на недоверенном коде. Подробности — в модели угроз.

---

## Reporting a vulnerability (English)

**Please do not open a public issue.** Report privately via
[Security → Report a vulnerability](https://github.com/Kelll31/offload/security/advisories/new) with the Offload version,
reproduction steps (ideally a minimal repository or an MCP request) and the impact. We acknowledge reports within 7 days,
agree on a fix timeline and a disclosure date, and credit reporters in the release notes if they wish.

Only the latest release receives security fixes. See [docs/threat-model.md](docs/threat-model.md) (in Russian) for what Offload
protects against and what it intentionally does not: `local_verify` and the agent sandbox execute the repository's own build and
test code by design.
