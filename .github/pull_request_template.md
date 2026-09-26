## Что и зачем

<!-- Коротко: какую проблему решает PR, ссылка на issue или пункт ROADMAP.md. -->

## Как проверено

- [ ] `dotnet test --solution Offload.slnx -c Release` — зелёный
- [ ] для изменений MCP: `node scripts/mcp-smoke.mjs --strict publish\Offload.exe`
- [ ] для интерфейса: скриншоты в светлой и тёмной теме приложены
- [ ] запись в `CHANGELOG.md` («Не выпущено»)

## Чек-лист инвариантов

- [ ] stdout MCP-процесса — только JSON-RPC
- [ ] файлы в MCP — через `PathGuard`, команды — через `VerifyCommand`
- [ ] конфиги IDE — через `ConfigFile.Edit`
- [ ] тексты интерфейса — через `L.T`/`L.F` с английским переводом; описания MCP-инструментов — по-английски
- [ ] тесты не трогают реальный профиль (`TempHome`, `Sandbox`)
