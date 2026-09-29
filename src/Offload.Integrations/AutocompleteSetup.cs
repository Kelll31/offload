using System.Text;
using Offload.Core.Logging;
using Offload.Integrations.Clients;
using Offload.Integrations.Editing;

namespace Offload.Integrations;

/// <summary>Сервер автодополнения (FIM) для IDE: адрес (без /v1), ключ API и имя модели для запросов.</summary>
public sealed record FimEndpoint(string BaseUrl, string ApiKey, string ModelName);

/// <summary>Состояние подключения автодополнения в одной IDE.</summary>
public enum AutocompleteTargetState
{
    /// <summary>IDE (или расширение) не найдена на этом компьютере.</summary>
    ClientNotFound,
    /// <summary>IDE есть, наш файл не записан.</summary>
    NotConfigured,
    /// <summary>Наш файл записан и совпадает с текущим адресом, ключом и моделью.</summary>
    Configured,
    /// <summary>Наш файл записан, но адрес, ключ или модель устарели.</summary>
    Outdated,
    /// <summary>На месте нашего файла — файл пользователя (без метки Offload): не трогаем.</summary>
    Foreign,
    /// <summary>Файл не читается.</summary>
    Error,
    /// <summary>IDE настраивается вручную (показываем адрес и ключ).</summary>
    Manual,
}

/// <summary>Подключение автодополнения к одной IDE для интерфейса.</summary>
public sealed record AutocompleteTargetStatus(string Id, string DisplayName, AutocompleteTargetState State, string? Path, string? Detail = null);

/// <summary>
/// Подключение сервера автодополнения (роль fim) к IDE.
/// <list type="bullet">
/// <item>Continue: отдельный файл-блок в глобальной папке models Continue (<see cref="ContinueBlockPath"/>). Continue сам загружает
/// блоки всех типов из ~/.continue/&lt;тип&gt;/*.yaml (BLOCK_TYPES = models, mcpServers, rules…), поэтому config.yaml пользователя
/// не трогаем — так же, как с MCP-блоком (<see cref="ContinueIntegration"/>). Формат модели с ролью autocomplete и провайдером
/// llama.cpp: https://docs.continue.dev/customize/model-providers/more/llamacpp,
/// https://docs.continue.dev/customize/deep-dives/autocomplete; загрузка локальных блоков —
/// https://github.com/continuedev/continue/blob/main/core/config/yaml/loadYaml.ts. Провайдер llama.cpp шлёт запросы на /completion
/// с заголовком «Authorization: Bearer &lt;apiKey&gt;», шаблон FIM выбирается по имени модели («qwen» + «coder» — токены Qwen).</item>
/// <item>llama.vscode (ggml-org.llama-vscode): настройки llama-vscode.endpoint и llama-vscode.api_key в settings.json VS Code
/// (https://github.com/ggml-org/llama.vscode/blob/master/package.json). Правки settings.json VS Code в Offload нет — показываем
/// готовый фрагмент (<see cref="LlamaVscodeSnippet"/>) и адрес с ключом для копирования.</item>
/// </list>
/// Запись — только через <see cref="ConfigFile"/> (резервная копия, атомарная замена); файл без метки Offload не перезаписывается и не удаляется.
/// </summary>
public static class AutocompleteSetup
{
    public const string ContinueId = "continue";
    public const string LlamaVscodeId = "llama-vscode";

    /// <summary>Идентификатор расширения llama.vscode (папка расширения — &lt;id&gt;-&lt;версия&gt;).</summary>
    public const string LlamaVscodeExtensionId = "ggml-org.llama-vscode";

    private const string Marker = ContinueIntegration.Marker;

    private static string ContinueDir => Path.Combine(IntegrationEnvironment.UserProfile, ".continue");

    /// <summary>Наш файл-блок моделей Continue.</summary>
    public static string ContinueBlockPath => Path.Combine(ContinueDir, "models", "offload-autocomplete.yaml");

    public static bool ContinueInstalled() => Directory.Exists(ContinueDir);

    /// <summary>Содержимое блока Continue с моделью автодополнения. Строки YAML — в одинарных кавычках.</summary>
    internal static string BuildContinueYaml(FimEndpoint e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var baseUrl = e.BaseUrl.TrimEnd('/') + "/";
        var sb = new StringBuilder();
        sb.Append("# ").Append(Marker).Append(" (created by Offload; this file is rewritten or deleted automatically)\n");
        sb.Append("name: Offload Autocomplete\n");
        sb.Append("version: 0.0.1\n");
        sb.Append("schema: v1\n");
        sb.Append("models:\n");
        sb.Append("  - name: ").Append(ContinueIntegration.Quote("Offload autocomplete")).Append('\n');
        sb.Append("    provider: llama.cpp\n");
        sb.Append("    model: ").Append(ContinueIntegration.Quote(e.ModelName)).Append('\n');
        sb.Append("    apiBase: ").Append(ContinueIntegration.Quote(baseUrl)).Append('\n');
        if (!string.IsNullOrEmpty(e.ApiKey)) sb.Append("    apiKey: ").Append(ContinueIntegration.Quote(e.ApiKey)).Append('\n');
        sb.Append("    roles:\n");
        sb.Append("      - autocomplete\n");
        return sb.ToString();
    }

    /// <summary>Состояние блока Continue относительно ожидаемого адреса (null — только наличие нашего файла).</summary>
    public static AutocompleteTargetStatus ContinueStatus(FimEndpoint? expected)
    {
        var path = ContinueBlockPath;
        AutocompleteTargetStatus Make(AutocompleteTargetState s, string? detail = null) => new(ContinueId, "Continue", s, path, detail);
        if (!ContinueInstalled()) return Make(AutocompleteTargetState.ClientNotFound);
        try
        {
            if (!File.Exists(path)) return Make(AutocompleteTargetState.NotConfigured);
            var text = ConfigFile.Read(path).Text;
            if (!text.Contains(Marker, StringComparison.Ordinal)) return Make(AutocompleteTargetState.Foreign);
            if (expected is null) return Make(AutocompleteTargetState.Configured);
            return Make(Normalize(text) == BuildContinueYaml(expected) ? AutocompleteTargetState.Configured : AutocompleteTargetState.Outdated);
        }
        catch (Exception ex) when (ex is ConfigReadException or IOException or UnauthorizedAccessException)
        {
            return Make(AutocompleteTargetState.Error, ex.Message);
        }
    }

    /// <summary>
    /// Записать (или обновить) блок Continue. Continue не установлен — ничего не делает (Ok = false, без ошибки в журнале).
    /// Файл пользователя без метки Offload не перезаписывается.
    /// </summary>
    public static IntegrationResult ApplyContinue(FimEndpoint e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!ContinueInstalled()) return new IntegrationResult(false, L.T("Continue не найден на этом компьютере."));
        var path = ContinueBlockPath;
        try
        {
            var content = BuildContinueYaml(e);
            var foreign = false;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var res = ConfigFile.Edit(path, snap =>
            {
                foreign = snap.Exists && !snap.Text.Contains(Marker, StringComparison.Ordinal);
                if (foreign) return null;
                return snap.Exists && Normalize(snap.Text) == content ? null : content;
            });
            if (foreign)
                return new IntegrationResult(false, L.F("Файл {0} создан не Offload — он оставлен без изменений.", path));
            return new IntegrationResult(true,
                res.Outcome == WriteOutcome.Written
                    ? L.F("Continue: модель автодополнения прописана (файл {0}).", path)
                    : L.F("Continue: модель автодополнения уже прописана (файл {0}).", path),
                res.BackupPath);
        }
        catch (Exception ex) when (ex is ConfigReadException or IOException or UnauthorizedAccessException)
        {
            Log.Warn("Integrations", $"continue (автодополнение): {ex.Message}");
            return new IntegrationResult(false, L.F("Не удалось записать файл {0}: {1}", path, ex.Message));
        }
    }

    /// <summary>Удалить наш блок Continue (файл без метки Offload не трогаем). Отсутствие файла — успех.</summary>
    public static IntegrationResult RemoveContinue()
    {
        var path = ContinueBlockPath;
        try
        {
            if (!File.Exists(path)) return new IntegrationResult(true, L.T("Continue: модель автодополнения не была прописана."));
            var snap = ConfigFile.Read(path);
            if (!snap.Text.Contains(Marker, StringComparison.Ordinal))
                return new IntegrationResult(false, L.F("Файл {0} создан не Offload — он оставлен без изменений.", path));
            var backup = ConfigFile.Delete(snap);
            try
            {
                var dir = Path.GetDirectoryName(path)!;
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (IOException)
            {
                // Папка занята — не важно.
            }
            return new IntegrationResult(true, L.F("Continue: модель автодополнения убрана (файл {0}).", path), backup);
        }
        catch (Exception ex) when (ex is ConfigReadException or IOException or UnauthorizedAccessException)
        {
            Log.Warn("Integrations", $"continue (автодополнение): {ex.Message}");
            return new IntegrationResult(false, L.F("Не удалось удалить файл {0}: {1}", path, ex.Message));
        }
    }

    /// <summary>Установлено ли расширение llama.vscode (VS Code или VS Code Insiders).</summary>
    public static bool LlamaVscodeInstalled()
    {
        foreach (var dir in new[] { ".vscode", ".vscode-insiders" })
        {
            var ext = Path.Combine(IntegrationEnvironment.UserProfile, dir, "extensions");
            try
            {
                if (Directory.Exists(ext) && Directory.EnumerateDirectories(ext, LlamaVscodeExtensionId + "-*").Any()) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Нет доступа к папке расширений — считаем, что не установлено.
            }
        }
        return false;
    }

    /// <summary>Состояние llama.vscode: настраивается вручную (адрес и ключ показываются для копирования).</summary>
    public static AutocompleteTargetStatus LlamaVscodeStatus() =>
        new(LlamaVscodeId, "llama.vscode", LlamaVscodeInstalled() ? AutocompleteTargetState.Manual : AutocompleteTargetState.ClientNotFound, null);

    /// <summary>Фрагмент settings.json VS Code для llama.vscode (адрес FIM-сервера и ключ API).</summary>
    public static string LlamaVscodeSnippet(FimEndpoint e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var o = new System.Text.Json.Nodes.JsonObject
        {
            ["llama-vscode.endpoint"] = e.BaseUrl.TrimEnd('/'),
            ["llama-vscode.api_key"] = e.ApiKey,
        };
        var json = o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = JsonTree.Encoder });
        // Без внешних скобок — строки вставляются внутрь объекта settings.json.
        var lines = json.Replace("\r\n", "\n").Split('\n');
        return string.Join(Environment.NewLine, lines.Skip(1).Take(lines.Length - 2).Select(l => l.Trim()));
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");
}
