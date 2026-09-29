# Интерфейс (Offload.App, WinForms)

## Каркас

- Главное окно (`Forms/MainForm.cs`): слева `Controls/NavigationRail` (группы «Главное / Настройка / Сервис» — `MainForm.GroupOf`,
  значки, сворачивание до полосы значков — `Ui.NavCollapsed`, статус сервера и кнопка темы внизу), справа заголовок
  (`page.Title` + `page.Subtitle`) и страница. Ctrl+1…9 и Ctrl+Tab — разделы.
- Страница: `internal sealed class XPage : PageBase` с `Key` (константа в `Tabs`), `Title`, `Subtitle`, `Glyph` (`Controls/Glyphs`),
  регистрация — список `_pages` в `MainForm`. Несохранённые изменения — `HasUnsavedChanges` (спросим перед пересозданием окна).
- Шаг мастера — наследник `WizardStep` (`Title`, `Heading`); фон шагов — `Theme.Card`, колонка шагов — `Theme.Surface`.

## Тема (светлая / тёмная)

- Палитра — только `Theme.*`: `Surface` (фон окна/страниц), `Card` (карточки, плитки), `Input` (поля, списки, журнал), `Border`,
  `Track`, `NavSelected/NavHover`, `Accent/AccentHover/AccentPressed/OnAccent/AccentLight`, `TextPrimary/TextMuted/TextFaint`,
  `OkText/WarnText/ErrorText`, `Green/Amber/Red/Gray`, `Series(i)`, `LoadColor(f)`, `Blend`. Никаких `Color.FromArgb`/`Color.White`
  и `SystemColors.*` в страницах — иначе тёмная тема сломается.
- Тема применяется при старте (`Program.ApplyColorMode` → `Theme.Initialize` + `Application.SetColorMode`) **после**
  `InstallExceptionHandlers` (SetColorMode(Dark) создаёт окно — после этого `SetUnhandledExceptionMode` падает).
- Смена темы (`IAppShell.ApplyTheme`, режим «как в Windows» следит за `SystemEvents.UserPreferenceChanged`) пересоздаёт окно:
  цвета берутся при создании элементов. Ничего не кэшировать в статиках между окнами.
- Заголовок окна под тему — `Theme.ApplyWindowFrame(form)` в `OnHandleCreated`.

## Элементы

- Раскладка — фабрики `Util/Kit.cs`; карточка — `Controls/CardPanel`; свои рисованные элементы — наследники `PaintedControl`
  (`Controls/Charts.cs`: `StatTile`, `BarChart`, `BarList`) и помощники `Controls/Draw` (скругления, карточки, текст, значки).
- Дизайн-система (1.0.4): кнопки — `Kit.Button` / `Kit.IconButton` / `Kit.Subtle` / `Kit.Primary` (рисованные
  `Controls/ModernButton`, основная — градиент `Theme.Fill` → `Theme.FillEnd`, текст `Theme.OnFill`); флажки — `Kit.Check`
  возвращает `Controls/ToggleSwitch` (переключатель, остаётся `CheckBox`, промежуточное состояние поддерживается); карточки —
  `CardPanel` (радиус `Theme.RadiusCard`, мягкая тень `Draw.Shadow`, выделенная — `Hero = true`, полоса слева — `EdgeColor`);
  шапка страницы — `Controls/PageHeader` (значок раздела в градиентной плашке, «таблетки» состояния сервера и Claude);
  градиенты — `Draw.Gradient`, фон под элементом — `Draw.EffectiveBack`. Цвета из палитры, без `Color.FromArgb` в страницах.
- Палитра команд — `Forms/CommandPaletteForm` (поиск `FuzzyMatch` из Offload.Core), команды собирает `MainForm.PaletteCommands`;
  справка по клавишам — `Forms/ShortcutsForm` (новое сочетание — добавить и туда).
- Значки — шрифт `Theme.Icons(size)` (Segoe Fluent Icons / MDL2), коды — `Controls/Glyphs`.
- Размеры — логические пиксели 96 DPI; в `OnPaint` — `Px(n)` (`LogicalToDeviceUnits`). Высоты рисованных элементов задавать
  строкой таблицы (`AddFixedRow`), а не вычислять в конструкторе — форма масштабирует их сама.
- Диалоги — `Ui.Info/Warn/ShowError/Confirm`, ошибки — `Ui.FriendlyError(ex)`; числа — `Ui.N`, `Ui.Short` (12,4 тыс.), `Ui.Plural`.

## Потоки и данные

- Долгие операции — `RunBusyAsync(...)` или `Ui.RunSafeAsync(owner, ...)`; фон → UI: `Ui.Post` / `Shell.PostToUi`.
  Никаких `.Result`/`.Wait()`/`GetAwaiter().GetResult()` в UI-потоке. Таймеры — `CreateTimer` (только активная страница).
- Периодически обновляемые данные: считать в `Task.Run`, сравнивать «подпись» и не перерисовывать без изменений (мигание).
- При переключении раздела `MainForm` сбрасывает прокрутку и уводит фокус со скрытой страницы — не ставить фокус в поля страниц в `OnActivated`.

## Тексты

- По-русски, коротко; «ёлочки», длинное тире, единицы через пробел (`12 ГБ`), склонения — `Ui.Plural`. Перечисления → `Services/Texts.cs`.
- **Каждый текст интерфейса — через локализацию** (`Offload.Core/Localization/L.cs`): `L.T("Текст")`, `L.F("Модель {0}", x)` (без `$"…"`
  и склейки внутри), `Ui.Plural(n, "строка", "строки", "строк")`. Английский — `src/Offload.Core/Localization/en/*.json`
  (ключ — русский текст, для склонений ключ `одна|несколько|много` → `one|many`). Никаких `static readonly`/`const` с текстом UI —
  только свойства `=> L.T(...)`. Журналы `Log.*` не переводятся; строки не для интерфейса — пометка `// l10n-ignore`,
  ключ перевода в данных — `// l10n-key`. Проверка — `LocalizationCoverageTests` (Offload.Core.Tests; `OFFLOAD_L10N_FILTER=путь`).
- Смена языка/схемы/акцента — `MainForm.RequestAppearance` → `IAppShell.ApplyAppearance` (окно пересоздаётся, меню трея переводится).
- Режим `--mcp` в `Program.Main` обрабатывается до `ApplicationConfiguration.Initialize()`.

## Проверка вживую

Dev-сборку в режиме трея запускайте только с `OFFLOAD_DEV=1` и отдельной папкой данных (`OFFLOAD_HOME`): иначе трей направит
автозапуск Windows и пути в подключённых IDE на временный exe. Режим разработчика включается и сам, если exe запущен из
`bin\Debug` или `bin\Release`.
