# Пакеты для менеджеров

## scoop

`scoop/offload.json` — манифест портативной версии (`Offload.exe`). Хэш и URL обновляются автоматически
(`checkver` + `autoupdate` по `SHA256SUMS.txt` релиза). Манифест нужно положить в bucket — отдельный репозиторий
(например, `Kelll31/scoop-offload`) — и один раз заполнить `hash` для текущей версии:

```powershell
scoop install git
scoop bucket add offload https://github.com/Kelll31/scoop-offload
scoop install offload
```

## winget

Установщик ставится «для себя» и поддерживает тихий режим (`/VERYSILENT /SUPPRESSMSGBOXES`), поэтому подходит для winget
как тип `inno`. Первый манифест создаётся вручную:

```powershell
winget install wingetcreate
wingetcreate new https://github.com/Kelll31/offload/releases/download/v<версия>/Offload-Setup-<версия>.exe
```

Идентификатор пакета — `Kelll31.Offload`, область — `user`. Для следующих версий:

```powershell
wingetcreate update Kelll31.Offload --version <версия> --urls https://github.com/Kelll31/offload/releases/download/v<версия>/Offload-Setup-<версия>.exe --submit
```

Автоматизация в CI — после первого принятого манифеста: шаг `wingetcreate update … --submit` с токеном GitHub
(секрет `WINGET_TOKEN`, права `public_repo`) в job релиза.
