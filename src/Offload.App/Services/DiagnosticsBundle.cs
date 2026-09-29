using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Offload.App.Util;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Core.Logging;
using Offload.Core.Security;
using Offload.Core.Usage;
using Offload.Core.Util;
using Offload.Integrations;

namespace Offload.App.Services;

/// <summary>
/// Пакет диагностики для отчёта об ошибке: zip с журналами (и ротациями .1), config.json с замаскированными ключами,
/// сводкой (версии, железо, сервер, статусы интеграций, статистика без содержимого запросов). Ничего не отправляет —
/// файл прикрепляет к issue сам пользователь. Сводка — по-английски: её читают разработчики.
/// </summary>
internal static partial class DiagnosticsBundle
{
    public const string Mask = "***";

    /// <summary>Имя файла по умолчанию.</summary>
    public static string DefaultFileName(DateTime now) =>
        $"offload-diagnostics-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.zip";

    /// <summary>Собрать сведения и записать zip (вызывать в фоновом потоке).</summary>
    public static void Create(string zipPath, IAppShell? shell, HardwareInfo? hardware)
    {
        var cfg = ConfigStore.Current;
        var secrets = SecretsOf(cfg);
        var summary = BuildSummary(cfg, shell, hardware, CollectIntegrations(), Ui.Try(() => UsageLog.ReadAll(), [], "UsageLog.ReadAll"), DateTime.Now);
        string? config = null;
        try
        {
            if (File.Exists(AppPaths.ConfigFile)) config = ReadShared(AppPaths.ConfigFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            config = $"(config.json not read: {ex.Message})";
        }
        Write(zipPath, summary, config, LogFiles(AppPaths.LogsDir), secrets, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>Журналы программы: *.log и ротации *.log.1 (только папка журналов, без вложенных).</summary>
    internal static IReadOnlyList<string> LogFiles(string logsDir)
    {
        if (!Directory.Exists(logsDir)) return [];
        return Directory.EnumerateFiles(logsDir)
            .Where(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".log.1", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Значения, которые нельзя выпускать из компьютера: ключ API llama-server, токен MCP по HTTP и логин/пароль
    /// из адреса своего прокси (user:pass@host — и в исходном, и в раскодированном виде).
    /// </summary>
    internal static IReadOnlyList<string> SecretsOf(AppConfig cfg)
    {
        // Сетевой ключ («Доступ из сети») и ключ удалённого сервера — расшифрованные: в журналы они не пишутся, но маска — страховка.
        var list = new List<string?> { cfg.Server.ApiKey, cfg.Autocomplete?.ApiKey, cfg.Mcp.HttpToken, Ui.Try(() => cfg.Server.LanApiKey(), null, "LanApiKey"),
            Ui.Try(() => cfg.RemoteApiKey(), null, "RemoteApiKey") };
        list.AddRange(ProxyCredentials(cfg.Network?.ProxyUrl));
        return list.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Логин и пароль из адреса прокси (часть до «@»): целиком, пароль как есть и раскодированный.</summary>
    private static IEnumerable<string> ProxyCredentials(string? proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl) || !Uri.TryCreate(proxyUrl.Trim(), UriKind.Absolute, out var u) || u.UserInfo.Length == 0)
            yield break;
        yield return u.UserInfo;
        var parts = u.UserInfo.Split(':', 2);
        if (parts.Length == 2 && parts[1].Length >= 4)
        {
            yield return parts[1];
            yield return Uri.UnescapeDataString(parts[1]);
        }
    }

    /// <summary>Записать zip: summary.txt, config.json (маскированный), logs/*. Файл пишется во временный и переименовывается.</summary>
    internal static void Write(string zipPath, string summary, string? configJson, IEnumerable<string> logFiles, IReadOnlyCollection<string> secrets,
        string? userProfile = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(zipPath))!;
        Directory.CreateDirectory(dir);
        var tmp = zipPath + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                AddText(zip, "summary.txt", HideProfile(MaskText(summary, secrets), userProfile));
                if (configJson is not null) AddText(zip, "config.json", HideProfile(MaskConfig(configJson, secrets), userProfile));
                foreach (var file in logFiles)
                {
                    string text;
                    try
                    {
                        text = ReadShared(file);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        text = $"(not read: {ex.Message})";
                    }
                    AddText(zip, "logs/" + Path.GetFileName(file), HideProfile(MaskText(text, secrets), userProfile));
                }
            }
            File.Move(tmp, zipPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Путь к профилю (содержит имя пользователя) → %USERPROFILE% (и в JSON-виде с двойными «\\»).</summary>
    internal static string HideProfile(string text, string? userProfile)
    {
        if (string.IsNullOrWhiteSpace(userProfile) || userProfile.Length < 4) return text;
        const string Placeholder = "%USERPROFILE%";
        var p = userProfile.TrimEnd('\\', '/');
        foreach (var form in new[] { p.Replace(@"\", @"\\", StringComparison.Ordinal), p.Replace('\\', '/'), p })
        {
            // Только целое имя папки: C:\Users\Ivan не должен задеть C:\Users\Ivanov.
            text = Regex.Replace(text, Regex.Escape(form) + @"(?![\w.-])", Placeholder.Replace("$", "$$", StringComparison.Ordinal),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return text;
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        using var w = new StreamWriter(s, new UTF8Encoding(false));
        w.Write(text);
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return r.ReadToEnd();
    }

    // ---------- Маскирование ----------

    /// <summary>Имя поля, значение которого — секрет (ключ, токен, пароль…).</summary>
    [GeneratedRegex(@"(api[-_]?key|key$|token|secret|password|passwd|pwd$|credential|auth|bearer|cookie|session)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretName();

    /// <summary>Ключ в командной строке и заголовках: --api-key X, Authorization: Bearer X.</summary>
    [GeneratedRegex(@"(--api-key[= ]+|Bearer\s+|api[-_]?key[""']?\s*[:=]\s*[""']?)([^\s""',;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyInText();

    /// <summary>Логин и пароль в адресе: scheme://user:pass@host → scheme://***@host (прокси, зеркала, строки подключения).</summary>
    [GeneratedRegex(@"://[^/@\s""'<>]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfo();

    /// <summary>
    /// Маскирование конфигурации: строковые значения полей с «секретными» именами (apiKey, hfToken, password…),
    /// значения, похожие на токены, и известные секреты. Не JSON — маскируется как текст.
    /// </summary>
    internal static string MaskConfig(string json, IReadOnlyCollection<string> secrets)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: Json.LenientDocument);
        }
        catch (JsonException)
        {
            return MaskText(json, secrets);
        }
        if (root is null) return MaskText(json, secrets);
        MaskNode(root, null, secrets);
        return MaskText(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = Json.Options.Encoder }), secrets);
    }

    private static void MaskNode(JsonNode node, string? name, IReadOnlyCollection<string> secrets)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    if (child is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        if (s.Length > 0 && (SecretName().IsMatch(key) || IsSecretValue(s, secrets))) obj[key] = Mask;
                    }
                    else if (child is not null)
                    {
                        MaskNode(child, key, secrets);
                    }
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        if (s.Length > 0 && IsSecretValue(s, secrets)) arr[i] = Mask;
                    }
                    else if (arr[i] is { } child)
                    {
                        MaskNode(child, name, secrets);
                    }
                }
                break;
        }
    }

    private static bool IsSecretValue(string value, IReadOnlyCollection<string> secrets) =>
        secrets.Any(k => value.Contains(k, StringComparison.Ordinal)) || SecretPatterns.LooksLikeToken(value);

    /// <summary>
    /// Маскирование текста (журналы, сводка): известные секреты, ключи в командной строке, логин и пароль в адресах
    /// (://user:pass@ → ://***@), похожие на токены значения.
    /// </summary>
    internal static string MaskText(string text, IReadOnlyCollection<string> secrets)
    {
        foreach (var s in secrets)
            if (!string.IsNullOrEmpty(s)) text = text.Replace(s, Mask, StringComparison.Ordinal);
        text = KeyInText().Replace(text, m => m.Groups[2].Value == Mask ? m.Value : m.Groups[1].Value + Mask);
        text = UrlUserInfo().Replace(text, "://" + Mask + "@");
        // Похожие на токены значения — общие шаблоны (SecretPatterns): sk-/sk_live_, hf_, ghp_/github_pat_, AIza, npm_, JWT, AKIA ….
        return SecretPatterns.MaskTokens(text, Mask);
    }

    // ---------- Сводка ----------

    internal sealed record IntegrationLine(string Id, string Name, string Status);

    private static List<IntegrationLine> CollectIntegrations()
    {
        var list = new List<IntegrationLine>();
        McpServerSpec spec;
        try
        {
            spec = McpServerSpec.ForCurrentExecutable();
        }
        catch (Exception ex)
        {
            Log.Debug("diagnostics", $"Спецификация MCP: {ex.Message}");
            return list;
        }
        foreach (var i in IntegrationRegistry.All)
        {
            var status = Ui.Try(() => i.GetStatus(spec).ToString(), "Error", $"{i.Id}.GetStatus");
            var name = Ui.Try(() => i.DisplayName, i.Id, "DisplayName");
            list.Add(new IntegrationLine(i.Id, name, status));
        }
        return list;
    }

    internal static string BuildSummary(AppConfig cfg, IAppShell? shell, HardwareInfo? hw, IReadOnlyList<IntegrationLine> integrations,
        IReadOnlyList<UsageRecord> usage, DateTime nowLocal)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("Offload diagnostics");
        sb.AppendLine(c, $"Created: {nowLocal:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();

        sb.AppendLine("[Versions]");
        sb.AppendLine(c, $"Offload: {AppInfo.Version} (update mode: {Ui.Try(() => AppUpdater.CurrentMode.ToString(), "?", "CurrentMode")}, dev: {DevMode.Active})");
        sb.AppendLine(c, $"Installed copy: {Ui.Try(() => InstallInfo.Installed is { } i ? $"{i.Version} per-machine={i.PerMachine} this={i.IsCurrentProcess}" : "none", "?", "InstallInfo")}");
        sb.AppendLine(c, $"Windows: {Environment.OSVersion.VersionString} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine(c, $".NET: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine(c, $"Language: {L.Language}, theme: {cfg.Ui.Theme}/{cfg.Ui.ThemePreset}, high contrast: {Theme.SystemHighContrast()}");
        sb.AppendLine(c, $"llama.cpp: {cfg.Llama.InstalledTag ?? "not installed"} (installed backend: {cfg.Llama.InstalledBackend}, configured: {cfg.Llama.Backend})");
        sb.AppendLine(c, $"OpenCode: {cfg.OpenCode.InstalledVersion ?? "not installed"} (enabled: {cfg.OpenCode.Enabled})");
        var model = cfg.ActiveModel();
        sb.AppendLine(c, $"Model: {(model is null ? "none" : $"{model.DisplayName} {model.Quant} {FileUtil.FormatBytes(model.SizeBytes)} ({model.Architecture}, MoE: {model.IsMoe})")}");
        sb.AppendLine(c, $"Setup completed: {cfg.SetupCompleted}");
        sb.AppendLine();

        sb.AppendLine("[Hardware]");
        if (hw is null)
        {
            sb.AppendLine("(not detected yet)");
        }
        else
        {
            sb.AppendLine(c, $"CPU: {hw.CpuName}, {hw.LogicalCores} threads, AVX2: {hw.CpuHasAvx2}, ARM64: {hw.IsArm64}");
            sb.AppendLine(c, $"RAM: {hw.TotalRamGb:0.0} GB (available {hw.AvailableRamBytes / 1024d / 1024d / 1024d:0.0} GB)");
            foreach (var g in hw.Gpus)
                sb.AppendLine(c, $"GPU: {g.Name} ({g.Vendor}{(g.IsIntegrated ? ", integrated" : "")}), VRAM {g.DedicatedMemoryGb:0.0} GB, driver {g.DriverVersion ?? "?"}, CC {g.ComputeCapability ?? "-"}");
            if (hw.Gpus.Count == 0) sb.AppendLine("GPU: none");
        }
        sb.AppendLine();

        sb.AppendLine("[Server]");
        if (shell is not null)
        {
            var server = shell.Server;
            sb.AppendLine(c, $"State: {server.State}");
            if (!string.IsNullOrWhiteSpace(server.LastError)) sb.AppendLine(c, $"Last error: {server.LastError}");
        }
        sb.AppendLine(c, $"Endpoint: {cfg.Server.Host}:{cfg.Server.Port}, context: {cfg.Server.ContextSize}, parallel: {cfg.Server.Parallel}, auto start: {cfg.Server.AutoStart}");
        sb.AppendLine(c, $"Network access: {(cfg.Server.IsActive() ? "on, " + cfg.Server.LanBindAddress : cfg.Server.LanAccess ? "requested, no LAN key" : "off")}; " +
                         $"remote server: {(cfg.IsRemote() ? RemoteServer.DisplayHost(cfg.MainEndpoint().BaseUrl) : "off")}");
        sb.AppendLine();

        sb.AppendLine("[Integrations]");
        sb.AppendLine(c, $"Registered by this installation: {(cfg.Integrations.Count == 0 ? "none" : string.Join(", ", cfg.Integrations))}");
        foreach (var i in integrations) sb.AppendLine(c, $"{i.Id} ({i.Name}): {i.Status}");
        sb.AppendLine();

        sb.AppendLine("[Usage] (counts only: usage records contain no prompt or file content)");
        AppendUsage(sb, "All time", usage);
        var weekStart = nowLocal.Date.AddDays(-6);
        AppendUsage(sb, "Last 7 days", usage.Where(r => r.TimestampUtc.ToLocalTime().Date >= weekStart).ToList());
        return sb.ToString();
    }

    private static void AppendUsage(StringBuilder sb, string title, IReadOnlyList<UsageRecord> records)
    {
        var c = CultureInfo.InvariantCulture;
        var s = UsageLog.Summarize(records);
        sb.AppendLine(c, $"{title}: calls {s.Calls}, failed {s.Failed}, prompt {s.PromptTokens}, completion {s.CompletionTokens}, saved {s.EstimatedSavedTokens}, model time {s.TotalDuration.TotalSeconds:0}s");
        foreach (var g in records.GroupBy(r => r.Tool).OrderByDescending(g => g.Count()))
            sb.AppendLine(c, $"  {g.Key}: {g.Count()} calls, {g.Count(r => !r.Ok)} failed, avg {g.Average(r => r.DurationMs):0} ms");
        foreach (var (client, n) in s.CallsByClient.OrderByDescending(kv => kv.Value))
            sb.AppendLine(c, $"  client {client}: {n}");
    }

    // ---------- Issue на GitHub ----------

    /// <summary>
    /// Ссылка на новое issue по шаблону bug.yml с заполненными полями (версия, видеокарта, модель).
    /// Пакет диагностики не прикрепляется — пользователь добавляет его сам.
    /// </summary>
    internal static string IssueUrl(string version, string? gpu, string? model)
    {
        var q = new List<string>
        {
            "template=bug.yml",
            "title=" + Uri.EscapeDataString($"[{version}] "),
            "version=" + Uri.EscapeDataString(version),
        };
        if (!string.IsNullOrWhiteSpace(gpu)) q.Add("gpu=" + Uri.EscapeDataString(gpu));
        if (!string.IsNullOrWhiteSpace(model)) q.Add("model=" + Uri.EscapeDataString(model));
        return $"{AppInfo.RepositoryUrl}/issues/new?{string.Join("&", q)}";
    }

    /// <summary>Видеокарта и драйвер для шаблона issue.</summary>
    internal static string? GpuLine(HardwareInfo? hw) =>
        hw?.PrimaryGpu is { } g ? $"{g.Name}, {g.DriverVersion ?? "?"}" : null;

    /// <summary>Модель и сборка llama.cpp для шаблона issue.</summary>
    internal static string ModelLine(AppConfig cfg)
    {
        var m = cfg.ActiveModel();
        var model = m is null ? "-" : $"{m.DisplayName} {m.Quant}".Trim();
        return $"{model}, {cfg.Llama.InstalledTag ?? "-"} {cfg.Llama.InstalledBackend}";
    }
}
