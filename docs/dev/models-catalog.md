# Каталог моделей (Offload.Models)

- `catalog.json` — JSONC (комментарии разрешены), встроенный ресурс; порядок в UI — по `priority` (меньше = выше).
- Инварианты (их проверяет `CatalogTests`, нарушение = красный CI):
  - `id` уникален; `repo` — `org/name`; `revision` — 40 hex (закреплённый коммит HF, не `main`);
  - `quants` непуст, первый — по умолчанию; для **каждого** кванта есть запись в `files` с `path`, точным `size` и `sha256` (64 hex,
    из HF tree API `lfs.oid`); `approxSizeBytes` = размер файла кванта по умолчанию;
  - `displayName` и `description` — по-русски; `description` 120–700 символов и содержит «Лицензия»; `license` непуст;
  - `paramsB ≥ activeParamsB`; `defaultContext ≤ nativeContext`; `kv` (layers/kvHeads/headDim > 0), `blockCount`, `architecture` —
    из заголовка GGUF; `isMoe` ⇒ заполнен `moe`; `releaseDate` — `YYYY-MM-DD`; `sampling` задан.
- Никогда не выдумывать размеры, SHA-256, ревизии и параметры архитектуры — только из HF API / GGUF. Данные заголовка GGUF можно снять Range-запросами без скачивания весов.
- `FitCalculator` решает, влезет ли модель (веса + KV-кэш + запас) в VRAM; для MoE считает выгрузку экспертов в ОЗУ (`MinRamGb`).
  При изменении формул — обновить `FitTests` с конкретными числами.
- Загрузка — `HttpDownloader` (докачка `.part` через Range, проверка SHA-256, атомарное переименование).
