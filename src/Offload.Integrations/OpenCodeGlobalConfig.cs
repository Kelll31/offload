using System.Text.Json.Nodes;
using Offload.Core.Logging;
using Offload.Integrations.Clients;
using Offload.Integrations.Editing;

namespace Offload.Integrations;

/// <summary>
/// Провайдер Offload в глобальном конфиге собственного OpenCode пользователя (~/.config/opencode или XDG_CONFIG_HOME).
/// Это чужой файл, поэтому правила те же, что у конфигов IDE: путь — от корней <see cref="IntegrationEnvironment"/>
/// (в тестах — песочница), запись — через <see cref="ConfigFile.Edit"/> (резервная копия, атомарная замена, повтор
/// при гонке), правка — точечная через <see cref="JsoncEditor"/>: комментарии, порядок ключей и форматирование
/// сохраняются, меняются только наши ключи.
/// </summary>
public static class OpenCodeGlobalConfig
{
    /// <summary>OpenCode сливает config.json → opencode.json → opencode.jsonc (последний главнее).</summary>
    internal static readonly string[] FileNames = ["config.json", "opencode.json", "opencode.jsonc"];

    /// <summary>Папка глобального конфига OpenCode пользователя.</summary>
    public static string ConfigDir => ClientLocations.OpenCodeGlobalDir;

    /// <summary>Файл, в который прописывается провайдер: opencode.jsonc, если он есть, иначе opencode.json.</summary>
    public static string TargetFile
    {
        get
        {
            var jsonc = Path.Combine(ConfigDir, "opencode.jsonc");
            return File.Exists(jsonc) ? jsonc : Path.Combine(ConfigDir, "opencode.json");
        }
    }

    /// <summary>
    /// Прописать (или обновить) провайдера <paramref name="providerId"/>: заменяется только «provider.&lt;id&gt;»;
    /// если в файле есть список «enabled_providers», в него добавляется id. Новый файл получает «$schema».
    /// Модель по умолчанию пользователя не меняется. Идемпотентно: без изменений файл не переписывается.
    /// </summary>
    public static IntegrationResult RegisterProvider(string providerId, JsonObject provider, string schemaUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(provider);
        var path = TargetFile;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var res = ConfigFile.Edit(path, snap =>
            {
                var ed = JsoncEditor.Parse(snap.Text);
                Validate(ed);
                var changed = false;
                if (ed.IsEmpty) changed |= ed.Set(["$schema"], JsonValue.Create(schemaUrl));
                changed |= ed.Set(["provider", providerId], provider.DeepClone());
                if (ed.KindAt(["enabled_providers"]) == JsoncKind.Array
                    && ed.TryGet(["enabled_providers"], out var enabled)
                    && !JsonTree.AsStringListLenient(enabled).Any(s => Is(s, providerId)))
                    changed |= ed.AppendToArray(["enabled_providers"], [JsonValue.Create(providerId)]);
                return changed ? ed.Text : null;
            });
            if (res.Outcome == WriteOutcome.Written) Log.Info("opencode", $"Провайдер {providerId} прописан в глобальный конфиг OpenCode: {path}");
            return new IntegrationResult(true,
                res.Outcome == WriteOutcome.Unchanged
                    ? L.F("OpenCode: провайдер Offload уже прописан ({0}).", path)
                    : L.F("OpenCode: провайдер Offload добавлен в глобальный конфиг ({0}).", path),
                res.BackupPath);
        }
        catch (Exception ex) when (ex is JsoncParseException or ConfigReadException or JsoncEditException)
        {
            return new IntegrationResult(false,
                L.F("Не удалось разобрать файл {0}: {1}. Файл не изменён — исправьте ошибку в нём вручную и повторите.", path, ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new IntegrationResult(false, L.F("Не удалось записать файл {0}: {1}", path, ex.Message));
        }
    }

    /// <summary>
    /// Убрать провайдера <paramref name="providerId"/> из всех файлов глобального конфига: «provider.&lt;id&gt;»
    /// (и опустевший «provider»), «model»/«small_model» вида «&lt;id&gt;/…», id из «enabled_providers».
    /// Чужие ключи не трогаются; файл, где нечего удалять, не переписывается. Неразборчивый файл пропускается
    /// (OpenCode его тоже не загрузит, значит и наш провайдер в нём не действует).
    /// </summary>
    public static IntegrationResult UnregisterProvider(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var messages = new List<string>();
        var ok = true;
        string? firstBackup = null;
        foreach (var name in FileNames)
        {
            var path = Path.Combine(ConfigDir, name);
            if (!File.Exists(path)) continue;
            try
            {
                var res = ConfigFile.Edit(path, snap => RemoveOurs(snap.Text, providerId));
                if (res.Outcome != WriteOutcome.Written) continue;
                firstBackup ??= res.BackupPath;
                messages.Add(L.F("OpenCode: провайдер Offload удалён из {0}.", path));
                Log.Info("opencode", $"Провайдер {providerId} удалён из глобального конфига OpenCode: {path}");
            }
            catch (Exception ex) when (ex is JsoncParseException or ConfigReadException or JsoncEditException)
            {
                Log.Warn("opencode", $"Глобальный конфиг OpenCode не разобран, пропущен: {path}: {ex.Message}");
                messages.Add(L.F("Файл {0} не разобран и оставлен без изменений: {1}", path, ex.Message));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ok = false;
                messages.Add(L.F("Не удалось записать файл {0}: {1}", path, ex.Message));
            }
        }
        if (messages.Count == 0) messages.Add(L.T("OpenCode: провайдер Offload в глобальном конфиге не прописан."));
        return new IntegrationResult(ok, string.Join(Environment.NewLine, messages), firstBackup);
    }

    /// <summary>Новый текст без наших ключей или null, если удалять нечего.</summary>
    private static string? RemoveOurs(string text, string providerId)
    {
        var ed = JsoncEditor.Parse(text);
        if (ed.IsEmpty || ed.KindAt([]) != JsoncKind.Object) return null;
        var changed = false;
        if (ed.KindAt(["provider"]) == JsoncKind.Object && ed.KindAt(["provider", providerId]) is not null)
        {
            changed |= ed.Remove(["provider", providerId]);
            if (ed.TryGet(["provider"], out var rest) && rest is JsonObject { Count: 0 }) ed.Remove(["provider"]);
        }
        foreach (var key in new[] { "model", "small_model" })
        {
            if (ed.TryGet([key], out var v) && JsonTree.AsString(v) is { } s
                && s.StartsWith(providerId + "/", StringComparison.OrdinalIgnoreCase))
                changed |= ed.Remove([key]);
        }
        if (ed.KindAt(["enabled_providers"]) == JsoncKind.Array)
            changed |= ed.RemoveFromArray(["enabled_providers"], n => JsonTree.AsString(n) is { } s && Is(s, providerId));
        return changed ? ed.Text : null;
    }

    private static void Validate(JsoncEditor ed)
    {
        if (ed.IsEmpty) return;
        if (ed.KindAt([]) != JsoncKind.Object) throw new JsoncEditException(L.T("корень файла не является объектом JSON"));
        if (ed.KindAt(["provider"]) is not null and not JsoncKind.Object and not JsoncKind.Null)
            throw new JsoncEditException(L.T("«provider» не является объектом"));
    }

    private static bool Is(string s, string providerId) => string.Equals(s, providerId, StringComparison.OrdinalIgnoreCase);
}
