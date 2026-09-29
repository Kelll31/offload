using System.Text.Json;
using System.Text.Json.Nodes;
using Offload.Core.Util;

namespace Offload.Core.Config;

/// <summary>Итог разбора файла настроек для импорта: какие ключи изменятся и новая конфигурация.</summary>
/// <param name="Changes">Изменяемые ключи вида «ui.theme» (пусто — всё совпадает).</param>
/// <param name="Skipped">Ключи файла, которые не переносятся (секреты, пути, параметры безопасности).</param>
public sealed record SettingsImport(IReadOnlyList<string> Changes, IReadOnlyList<string> Skipped, AppConfig Result);

/// <summary>Файл не является экспортом настроек Offload или повреждён.</summary>
public sealed class SettingsTransferException(string message) : Exception(message);

/// <summary>
/// Перенос настроек между компьютерами: экспорт и импорт только ключей из списка разрешённых — внешний вид, параметры
/// сервера и инструментов. Никогда не переносятся: ключи и токены (API, сетевой, HTTP MCP, Hugging Face), пути и
/// установленные компоненты, подключения к IDE, а также параметры безопасности (белый список команд, шаблоны секретов,
/// запись вне рабочей папки, доступ из сети, зеркала и прокси, доп. аргументы llama-server) — чужой файл не может
/// ослабить защиту или перенаправить загрузки.
/// </summary>
public static class SettingsTransfer
{
    public const string Marker = "offloadSettings";
    public const int FormatVersion = 1;

    /// <summary>Переносимые ключи (camelCase, как в config.json): раздел → поля.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Allowed = new Dictionary<string, string[]>
    {
        ["ui"] =
        [
            "showNotifications", "minimizeToTrayOnClose", "checkAppUpdates", "theme", "navCollapsed", "themePreset", "accentColor",
            "language", "verboseLog", "autoRepairIntegrations", "quietHoursEnabled", "quietHoursFrom", "quietHoursTo", "globalHotkey",
        ],
        ["server"] =
        [
            "contextSize", "parallel", "gpuLayers", "cpuMoeLayers", "flashAttention", "cacheType", "threads", "autoStart",
            "idleUnloadMinutes", "enableMtp", "gpuSelection",
        ],
        ["mcp"] =
        [
            "maxFileBytes", "maxTotalBytes", "maxResponseChars", "serverStartTimeoutSeconds", "extraSystemPrompt",
            "cloudInputPricePerMTok", "cloudOutputPricePerMTok", "jobRetentionDays", "workCache", "workCacheMaxMb", "workCacheTtlDays",
            "autoMemory",
        ],
        ["openCode"] = ["enabled", "taskTimeoutSeconds"],
        ["llama"] = ["backend", "checkUpdates"],
        ["autocomplete"] = ["enabled", "cpuOnly", "configureContinue"],
        ["network"] = ["remoteCatalog"],
    };

    /// <summary>Текст файла экспорта: метка формата, версия Offload, дата и разрешённые ключи.</summary>
    public static string Export(AppConfig cfg, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var full = JsonSerializer.SerializeToNode(cfg, Json.Options) as JsonObject ?? [];
        var root = new JsonObject
        {
            [Marker] = FormatVersion,
            ["appVersion"] = AppInfo.Version,
            ["exportedUtc"] = utcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        };
        foreach (var (section, keys, src) in Allowed
                     .Select(kv => (kv.Key, kv.Value, Src: full[kv.Key] as JsonObject))
                     .Where(x => x.Src is not null))
        {
            var dst = new JsonObject();
            foreach (var (k, v) in keys.Select(k => (k, src![k])).Where(x => x.Item2 is not null))
                dst[k] = v!.DeepClone();
            if (dst.Count > 0) root[section] = dst;
        }
        return root.ToJsonString(Json.Options);
    }

    /// <summary>
    /// Разобрать файл и применить разрешённые ключи к копии <paramref name="current"/>. Сама конфигурация не меняется —
    /// вызывающий сохраняет <see cref="SettingsImport.Result"/> после подтверждения пользователя.
    /// </summary>
    public static SettingsImport Prepare(string text, AppConfig current)
    {
        ArgumentNullException.ThrowIfNull(current);
        JsonObject file;
        try
        {
            file = JsonNode.Parse(text ?? "", Json.NodeOptions, Json.LenientDocument) as JsonObject
                   ?? throw new SettingsTransferException(L.T("файл не содержит объект JSON"));
        }
        catch (JsonException ex)
        {
            throw new SettingsTransferException(L.F("файл не разобран: {0}", ex.Message));
        }
        if (file[Marker] is not JsonValue mv || !mv.TryGetValue<int>(out var version))
            throw new SettingsTransferException(L.T("это не файл экспорта настроек Offload"));
        if (version > FormatVersion)
            throw new SettingsTransferException(L.T("файл создан более новой версией Offload — обновите программу"));

        var merged = JsonSerializer.SerializeToNode(current, Json.Options) as JsonObject ?? [];
        var changes = new List<string>();
        var skipped = new List<string>();
        foreach (var (name, node) in file)
        {
            if (name is Marker or "appVersion" or "exportedUtc") continue;
            if (!Allowed.TryGetValue(name, out var keys) || node is not JsonObject section)
            {
                skipped.Add(name);
                continue;
            }
            if (merged[name] is not JsonObject target) merged[name] = target = new JsonObject();
            foreach (var (key, value) in section)
            {
                if (!keys.Contains(key))
                {
                    skipped.Add($"{name}.{key}");
                    continue;
                }
                if (JsonNode.DeepEquals(target[key], value)) continue;
                target[key] = value?.DeepClone();
                changes.Add($"{name}.{key}");
            }
        }

        AppConfig result;
        try
        {
            result = merged.Deserialize<AppConfig>(Json.Options) ?? throw new SettingsTransferException(L.T("файл не содержит настроек"));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new SettingsTransferException(L.F("недопустимое значение в файле: {0}", ex.Message));
        }
        return new SettingsImport(changes, skipped, result);
    }

    /// <summary>
    /// Перенести разрешённые ключи из <paramref name="source"/> (результат <see cref="Prepare"/>) в <paramref name="target"/>
    /// — например, внутри <c>ConfigStore.Update</c>: остальные поля target (в том числе изменённые после Prepare) не трогаются.
    /// </summary>
    public static void Apply(AppConfig source, AppConfig target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        var src = JsonSerializer.SerializeToNode(source, Json.Options) as JsonObject ?? [];
        var dst = JsonSerializer.SerializeToNode(target, Json.Options) as JsonObject ?? [];
        foreach (var (section, keys) in Allowed)
        {
            if (src[section] is not JsonObject from) continue;
            if (dst[section] is not JsonObject to) dst[section] = to = new JsonObject();
            foreach (var k in keys)
            {
                if (from[k] is { } v) to[k] = v.DeepClone();
                else to.Remove(k);
            }
        }
        var merged = dst.Deserialize<AppConfig>(Json.Options) ?? throw new SettingsTransferException(L.T("файл не содержит настроек"));
        target.Ui = merged.Ui;
        target.Server = merged.Server;
        target.Mcp = merged.Mcp;
        target.OpenCode = merged.OpenCode;
        target.Llama = merged.Llama;
        target.Autocomplete = merged.Autocomplete;
        target.Network = merged.Network;
    }
}
