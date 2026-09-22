using System.Security.Cryptography;
using System.Text.Json;
using Offload.Core.Logging;
using Offload.Core.Util;

namespace Offload.Core.Config;

/// <summary>
/// Загрузка и сохранение config.json. Файл читают и трей, и MCP-процессы,
/// поэтому запись атомарная, а чтение — «свежее» при каждом обращении MCP.
/// </summary>
public static class ConfigStore
{
    private static readonly object Lock = new();
    private static AppConfig? _current;

    /// <summary>
    /// Режим только для чтения (MCP-процессы): конфиг никогда не записывается на диск.
    /// Важно для Claude Desktop из Microsoft Store: запись дочернего процесса MSIX в %LOCALAPPDATA%
    /// виртуализируется, и такая копия навсегда «затенила» бы настоящий config.json.
    /// </summary>
    public static bool ReadOnly { get; set; }

    /// <summary>Вызывается после каждого сохранения в этом процессе.</summary>
    public static event Action<AppConfig>? Saved;

    /// <summary>Кэшированная конфигурация процесса (загружается при первом обращении).</summary>
    public static AppConfig Current
    {
        get
        {
            lock (Lock) return _current ??= LoadFromDisk();
        }
    }

    /// <summary>Перечитать конфигурацию с диска (MCP-процессы делают это перед каждым вызовом).</summary>
    public static AppConfig Reload()
    {
        var cfg = LoadFromDisk();
        lock (Lock) _current = cfg;
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
            config ??= _current ?? LoadFromDisk();
            _current = config;
            var json = JsonSerializer.Serialize(config, Json.Options);
            FileUtil.WriteAllTextAtomic(AppPaths.ConfigFile, json);
        }
        try { Saved?.Invoke(config); } catch (Exception ex) { Log.Warn("config", $"Обработчик Saved: {ex.Message}"); }
    }

    /// <summary>Изменить конфигурацию и сохранить.</summary>
    public static void Update(Action<AppConfig> mutate)
    {
        AppConfig cfg;
        lock (Lock)
        {
            cfg = _current ??= LoadFromDisk();
            mutate(cfg);
        }
        Save(cfg);
    }

    private static AppConfig LoadFromDisk()
    {
        AppConfig? cfg = null;
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
            {
                var text = File.ReadAllText(AppPaths.ConfigFile);
                cfg = JsonSerializer.Deserialize<AppConfig>(text, Json.Options);
            }
        }
        catch (Exception ex)
        {
            Log.Error("config", "Файл настроек повреждён, будут использованы значения по умолчанию", ex);
            FileUtil.Backup(AppPaths.ConfigFile);
        }

        cfg ??= new AppConfig();
        var changed = Normalize(cfg);
        if (!ReadOnly && (changed || !File.Exists(AppPaths.ConfigFile)))
        {
            try
            {
                FileUtil.WriteAllTextAtomic(AppPaths.ConfigFile, JsonSerializer.Serialize(cfg, Json.Options));
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
        cfg.Integrations ??= [];

        if (string.IsNullOrWhiteSpace(cfg.Server.ApiKey))
        {
            cfg.Server.ApiKey = "pc-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
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
        if (cfg.Server.Parallel < 1)
        {
            cfg.Server.Parallel = 1;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(cfg.Models.ModelsDir))
        {
            cfg.Models.ModelsDir = AppPaths.DefaultModelsDir;
            changed = true;
        }
        return changed;
    }

    public static InstalledModel? ActiveModel(this AppConfig cfg) =>
        cfg.Models.Installed.FirstOrDefault(m => m.Id == cfg.Models.ActiveModelId)
        ?? cfg.Models.Installed.FirstOrDefault();
}
