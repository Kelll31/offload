using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Offload.Core.Logging;
using Offload.Core.Util;

namespace Offload.Core.Config;

/// <summary>
/// Загрузка и сохранение config.json. Файл читают и трей, и MCP-процессы,
/// поэтому запись атомарная, а чтение — «свежее» при каждом обращении MCP.
/// </summary>
/// <remarks>
/// Пользователь может править config.json вручную (раздел «Настройки» → «Открыть config.json»). Поэтому перед каждым
/// обращением к <see cref="Current"/> и перед <see cref="Update"/> сверяется «отпечаток» файла (размер и время записи),
/// а при его изменении — хеш содержимого. Если файл изменили извне, конфигурация перечитывается с диска и вызывается
/// <see cref="ExternallyChanged"/> — ручные правки не затираются кэшированной копией при следующем сохранении.
/// <para>
/// Если ручная правка сделала файл неверным JSON, в памяти остаются последние корректные настройки (предупреждение
/// в журнал и событие <see cref="InvalidFile"/>, без падения). Сломанный файл не трогается, пока программе не понадобится
/// сохранить настройки; тогда он сначала копируется в папку backups (правка пользователя не пропадает молча),
/// а затем перезаписывается корректными настройками.
/// </para>
/// </remarks>
public static class ConfigStore
{
    private static readonly object Lock = new();
    private static AppConfig? _current;

    /// <summary>Отпечаток файла, соответствующий <see cref="_current"/> (последнее чтение или запись этим процессом).</summary>
    private static FileStamp? _stamp;

    /// <summary>Хеш содержимого файла, соответствующего <see cref="_current"/> (отличает настоящую правку от «touch»).</summary>
    private static string? _contentHash;

    /// <summary>На диске лежит неверный JSON (ручная правка), в памяти — последние корректные настройки.</summary>
    private static bool _diskInvalid;

    private static string? _invalidMessage;

    /// <summary>
    /// Перед следующей записью скопировать файл в backups: он мигрирован со старой схемы, миграция не удалась или файл
    /// записан более новой версией программы (её поля при сохранении потерялись бы). Текст — причина для журнала.
    /// </summary>
    private static string? _backupReason;

    /// <summary>
    /// Файл есть, но прочитать его не удалось (занят, нет доступа): в памяти — значения по умолчанию, файл не перезаписывается
    /// ими, а при каждом обращении делается новая попытка чтения.
    /// </summary>
    private static bool _diskUnreadable;

    /// <summary>
    /// Режим только для чтения (MCP-процессы): конфиг никогда не записывается на диск.
    /// Важно для Claude Desktop из Microsoft Store: запись дочернего процесса MSIX в %LOCALAPPDATA%
    /// виртуализируется, и такая копия навсегда «затенила» бы настоящий config.json.
    /// </summary>
    public static bool ReadOnly { get; set; }

    /// <summary>Вызывается после каждого сохранения в этом процессе.</summary>
    public static event Action<AppConfig>? Saved;

    /// <summary>
    /// Файл изменили извне (вручную или другой программой), и конфигурация перечитана с диска.
    /// Вызывается из потока, который обратился к настройкам.
    /// </summary>
    public static event Action<AppConfig>? ExternallyChanged;

    /// <summary>
    /// Файл изменили извне, но он не разбирается как JSON — остаются прежние настройки. Аргумент — текст ошибки разбора.
    /// Вызывается один раз на каждую версию сломанного файла.
    /// </summary>
    public static event Action<string>? InvalidFile;

    /// <summary>Кэшированная конфигурация процесса (загружается при первом обращении, перечитывается после ручной правки).</summary>
    public static AppConfig Current
    {
        get
        {
            AppConfig cfg;
            Notice notice;
            lock (Lock) cfg = EnsureFresh(out notice);
            Raise(notice, cfg);
            return cfg;
        }
    }

    /// <summary>
    /// Перечитать конфигурацию с диска (MCP-процессы делают это перед каждым вызовом). Сломанный ручной правкой файл
    /// не заменяет корректные настройки в памяти — как и в <see cref="Current"/>.
    /// </summary>
    public static AppConfig Reload()
    {
        AppConfig cfg;
        Notice notice;
        lock (Lock) cfg = EnsureFresh(out notice, force: true);
        Raise(notice, cfg);
        return cfg;
    }

    public static void Save(AppConfig? config = null)
    {
        if (ReadOnly)
        {
            Log.Warn("config", "Попытка сохранить настройки в режиме только для чтения (MCP) — пропущено.");
            return;
        }
        lock (Lock)
        {
            config ??= _current ?? LoadFromDisk(out _);
            _current = config;
            WriteLocked(config);
        }
        try { Saved?.Invoke(config); } catch (Exception ex) { Log.Warn("config", $"Обработчик Saved: {ex.Message}"); }
    }

    /// <summary>Изменить конфигурацию и сохранить. Ручные правки файла перед этим перечитываются.</summary>
    public static void Update(Action<AppConfig> mutate)
    {
        AppConfig cfg;
        Notice notice;
        lock (Lock)
        {
            cfg = EnsureFresh(out notice);
            mutate(cfg);
        }
        Raise(notice, cfg);
        Save(cfg);
    }

    // ── Ручные правки файла ─────────────────────────────────────────────────────────

    private enum Notice { None, Reloaded, Invalid }

    /// <summary>Отпечаток файла: путь, размер и время последней записи.</summary>
    private readonly record struct FileStamp(string Path, bool Exists, long Length, DateTime WriteTimeUtc)
    {
        public static FileStamp Of(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return fi.Exists ? new FileStamp(path, true, fi.Length, fi.LastWriteTimeUtc) : new FileStamp(path, false, 0, default);
            }
            catch
            {
                return new FileStamp(path, false, 0, default);
            }
        }
    }

    /// <summary>
    /// Вернуть кэш, перечитав файл, если его изменили извне (вызывать под Lock).
    /// <paramref name="force"/> — читать содержимое даже при неизменном отпечатке (правка в пределах точности mtime).
    /// </summary>
    private static AppConfig EnsureFresh(out Notice notice, bool force = false)
    {
        notice = Notice.None;
        var path = AppPaths.ConfigFile;
        if (_current is null || _diskUnreadable || _stamp is not { } known || !string.Equals(known.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            // Первое обращение, сменилась папка данных (тесты, OFFLOAD_HOME) или прошлая попытка чтения не удалась — загрузка.
            var retry = _diskUnreadable && _current is not null && string.Equals(_stamp?.Path, path, StringComparison.OrdinalIgnoreCase);
            var previous = _current;
            var loaded = LoadFromDisk(out var invalid);
            // Файл всё ещё недоступен — остаются прежние настройки в памяти (а не новые умолчания со случайным ключом API).
            if (retry && _diskUnreadable) return _current = previous!;
            _current = loaded;
            if (invalid) notice = Notice.Invalid;
            else if (retry) notice = Notice.Reloaded;
            return _current;
        }

        var now = FileStamp.Of(path);
        if (now == known && !force) return _current;

        // Файл удалили — оставляем текущие настройки (при сохранении он будет создан заново).
        if (!now.Exists)
        {
            _stamp = now;
            return _current;
        }

        string text;
        try
        {
            text = ReadShared(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Редактор ещё держит файл — проверим при следующем обращении.
            Log.Debug("config", $"config.json занят, проверка отложена: {ex.Message}");
            return _current;
        }

        var hash = Hash(text);
        if (hash == _contentHash)
        {
            _stamp = now; // «touch» без изменения содержимого
            return _current;
        }

        AppConfig? parsed;
        bool migrated;
        string? backupReason;
        try
        {
            parsed = Deserialize(text, out migrated, out backupReason);
        }
        catch (JsonException ex)
        {
            _stamp = now;
            _contentHash = hash;
            _diskInvalid = true;
            _invalidMessage = ex.Message;
            Log.Warn("config", $"config.json изменён вручную, но содержит ошибку ({ex.Message}) — используются прежние настройки. " +
                               "Исправьте файл; если программа сохранит настройки раньше, сломанный файл будет скопирован в backups и перезаписан.");
            notice = Notice.Invalid;
            return _current;
        }

        Log.Info("config", "config.json изменён вне программы — настройки перечитаны.");
        var cfg = parsed ?? new AppConfig();
        _diskInvalid = false;
        _backupReason = backupReason;
        _stamp = now;
        _contentHash = hash;
        if ((Normalize(cfg) | migrated) && !ReadOnly)
        {
            try
            {
                WriteLocked(cfg);
            }
            catch (Exception ex)
            {
                Log.Warn("config", $"Не удалось сохранить настройки: {ex.Message}");
            }
        }
        _current = cfg;
        notice = Notice.Reloaded;
        return cfg;
    }

    private static void Raise(Notice notice, AppConfig cfg)
    {
        try
        {
            switch (notice)
            {
                case Notice.Reloaded:
                    ExternallyChanged?.Invoke(cfg);
                    break;
                case Notice.Invalid:
                    InvalidFile?.Invoke(_invalidMessage ?? "");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("config", $"Обработчик изменения config.json: {ex.Message}");
        }
    }

    /// <summary>Записать настройки на диск и запомнить отпечаток (вызывать под Lock).</summary>
    private static void WriteLocked(AppConfig config)
    {
        var path = AppPaths.ConfigFile;
        if (_diskUnreadable && File.Exists(path))
        {
            // Настоящие настройки на диске прочитать не удалось — не затираем их значениями по умолчанию.
            Log.Warn("config", "config.json недоступен для чтения — настройки не сохранены, чтобы не затереть файл значениями по умолчанию.");
            return;
        }
        if (_diskInvalid && string.Equals(_stamp?.Path, path, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            // Сломанную ручную правку не затираем молча: сначала копия в backups.
            var copy = FileUtil.Backup(path);
            Log.Warn("config", $"config.json с ошибкой перезаписан настройками программы; копия ручной правки: {copy ?? "не сохранена"}");
        }
        else if (_backupReason is { } reason && string.Equals(_stamp?.Path, path, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            var copy = FileUtil.Backup(path);
            Log.Info("config", $"Копия config.json перед записью ({reason}): {copy ?? "не сохранена"}");
        }
        // Содержимое всегда записано в схеме этой сборки: файл более новой версии получает текущую метку, чтобы новая
        // версия программы снова применила к нему свои миграции (незнакомые поля уже сохранены копией в backups).
        config.SchemaVersion = ConfigMigrations.CurrentVersion;
        var json = JsonSerializer.Serialize(config, Json.Options);
        FileUtil.WriteAllTextAtomic(path, json);
        _diskInvalid = false;
        _backupReason = null;
        _stamp = FileStamp.Of(path);
        _contentHash = Hash(json);
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    /// <summary>
    /// Разобрать текст config.json и привести старую схему к текущей (<see cref="ConfigMigrations"/>).
    /// <paramref name="migrated"/> — схема обновлена, файл нужно сохранить; <paramref name="backupReason"/> — прежний файл
    /// нужно скопировать в backups перед записью. <see cref="JsonException"/> — текст не разбирается как JSON.
    /// </summary>
    private static AppConfig? Deserialize(string text, out bool migrated, out string? backupReason)
    {
        migrated = false;
        backupReason = null;
        var cfg = JsonSerializer.Deserialize<AppConfig>(text, Json.Options);
        if (cfg is null) return null;

        var from = cfg.SchemaVersion;
        if (from > ConfigMigrations.CurrentVersion)
        {
            Log.Warn("config", $"config.json записан более новой версией Offload (схема {from}, эта версия знает {ConfigMigrations.CurrentVersion}): " +
                               "незнакомые настройки не сохранятся, перед первой записью файл будет скопирован в backups.");
            backupReason = $"схема {from} новее {ConfigMigrations.CurrentVersion}"; // l10n-ignore — только для журнала
            return cfg;
        }
        if (from == ConfigMigrations.CurrentVersion) return cfg;

        try
        {
            if (ConfigMigrations.Migrate(text, from) is not { } next) return cfg;
            var result = JsonSerializer.Deserialize<AppConfig>(next, Json.Options) ?? cfg;
            Log.Info("config", $"config.json: схема {from} → {result.SchemaVersion}");
            migrated = true;
            backupReason = $"миграция схемы {from} → {result.SchemaVersion}"; // l10n-ignore — только для журнала
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Ошибка в шаге миграции не должна ронять трей при старте.
            Log.Error("config", $"Не удалось обновить config.json со схемы {from} — настройки прочитаны как есть, прежний файл будет сохранён в backups", ex);
            backupReason = $"неудачная миграция схемы {from}"; // l10n-ignore — только для журнала
            return cfg;
        }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// Загрузка с диска с восстановлением значений по умолчанию (вызывать под Lock). Повреждённый файл не перезаписывается
    /// сразу: в памяти — значения по умолчанию, файл остаётся для исправления и копируется в backups только перед первым
    /// сохранением настроек (см. <see cref="WriteLocked"/>). <paramref name="invalid"/> — файл есть, но не разбирается.
    /// </summary>
    private static AppConfig LoadFromDisk(out bool invalid)
    {
        invalid = false;
        var path = AppPaths.ConfigFile;
        AppConfig? cfg = null;
        string? text = null;
        var migrated = false;
        var wasUnreadable = _diskUnreadable;
        _backupReason = null;
        _diskUnreadable = false;
        var stamp = FileStamp.Of(path);
        try
        {
            if (stamp.Exists)
            {
                text = ReadShared(path);
                cfg = Deserialize(text, out migrated, out _backupReason);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            invalid = ex is JsonException;
            _diskUnreadable = !invalid;
            // Недоступный файл перечитывается при каждом обращении — в журнал только первая неудача подряд.
            if (invalid || !wasUnreadable)
                Log.Error("config", "Файл настроек повреждён или недоступен, используются значения по умолчанию (файл не перезаписывается)", ex);
            _invalidMessage = ex.Message;
            cfg = null;
        }

        if (_diskUnreadable)
        {
            cfg = new AppConfig();
            Normalize(cfg);
            _diskInvalid = false;
            _stamp = stamp;
            _contentHash = null;
            return cfg;
        }

        if (invalid)
        {
            cfg = new AppConfig();
            Normalize(cfg);
            _diskInvalid = true;
            _stamp = stamp;
            _contentHash = text is null ? null : Hash(text);
            return cfg;
        }

        cfg ??= new AppConfig();
        var changed = Normalize(cfg);
        _diskInvalid = false;
        _stamp = stamp;
        _contentHash = text is null ? null : Hash(text);
        if (!ReadOnly && (changed || migrated || text is null))
        {
            try
            {
                WriteLocked(cfg);
            }
            catch (Exception ex)
            {
                Log.Warn("config", $"Не удалось сохранить настройки: {ex.Message}");
            }
        }
        return cfg;
    }

    /// <summary>Заполнение обязательных значений. Возвращает true, если что-то изменилось.</summary>
    private static bool Normalize(AppConfig cfg)
    {
        var changed = false;
        cfg.Llama ??= new();
        cfg.Server ??= new();
        cfg.Models ??= new();
        cfg.Models.Installed ??= [];
        cfg.OpenCode ??= new();
        cfg.Mcp ??= new();
        cfg.Ui ??= new();
        cfg.Network ??= new();
        cfg.Remote ??= new();
        cfg.Integrations ??= [];
        cfg.DeclinedIntegrations ??= [];
        cfg.Autocomplete ??= new();

        // Старая версия схемы (в том числе файл без поля) поднимается; метку более новой версии меняет только запись (WriteLocked).
        if (cfg.SchemaVersion < ConfigMigrations.CurrentVersion)
        {
            cfg.SchemaVersion = ConfigMigrations.CurrentVersion;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(cfg.Server.ApiKey))
        {
            cfg.Server.ApiKey = "pc-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(cfg.Autocomplete.ApiKey) || string.Equals(cfg.Autocomplete.ApiKey.Trim(), cfg.Server.ApiKey.Trim(), StringComparison.Ordinal))
        {
            cfg.Autocomplete.ApiKey = "fim-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            changed = true;
        }
        if (cfg.Server.Port is <= 0 or > 65535)
        {
            cfg.Server.Port = 8765;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(cfg.Server.Host))
        {
            cfg.Server.Host = "127.0.0.1";
            changed = true;
        }
        if (cfg.Server.Parallel is < 1 or > ServerSettings.MaxParallel)
        {
            // Один предел для аргументов llama-server, очереди GPU в MCP и интерфейса.
            cfg.Server.Parallel = Math.Clamp(cfg.Server.Parallel, 1, ServerSettings.MaxParallel);
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(cfg.Models.ModelsDir))
        {
            cfg.Models.ModelsDir = AppPaths.DefaultModelsDir;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Активная (основная) модель: установленная чат-модель с идентификатором <see cref="ModelSettings.ActiveModelId"/>,
    /// иначе первая установленная чат-модель. Модели эмбеддингов и реранкеры активными не бывают (только роли) —
    /// даже если идентификатор в конфиге указывает на такую модель; null — чат-моделей нет.
    /// </summary>
    public static InstalledModel? ActiveModel(this AppConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (cfg.Models is not { Installed: { } installed } models) return null;
        return installed.FirstOrDefault(m => m.Kind == ModelKind.Chat && m.Id == models.ActiveModelId)
               ?? installed.FirstOrDefault(m => m.Kind == ModelKind.Chat);
    }
}
