using System.Text.Json;
using Offload.Core;

namespace Offload.Mcp.Http;

/// <summary>
/// Инструменты, которые в HTTP-режиме без <c>--allow-exec</c> не выполняются: они запускают код проекта (сборка, тесты,
/// скрипты npm, агент в песочнице) или пишут файлы. Токен HTTP-режима может оказаться у другого локального процесса
/// (config.json, резервные копии, проброс портов), поэтому по умолчанию HTTP — только чтение и вопросы к модели.
/// </summary>
internal static class HttpExecPolicy
{
    /// <summary>Отключены целиком (и скрыты из tools/list).</summary>
    public static readonly IReadOnlySet<string> AlwaysBlocked = new HashSet<string>(StringComparer.Ordinal)
    {
        McpToolNames.Verify, McpToolNames.WriteFile, McpToolNames.EditFiles, McpToolNames.AgentTask, McpToolNames.Solve,
        McpToolNames.ApplyPatch, McpToolNames.Refactor, McpToolNames.Debug, McpToolNames.PrReady,
    };

    /// <summary>
    /// Причина отказа (английский — читает модель) или null, если вызов допустим без <c>--allow-exec</c>. Аргументы разбираются
    /// так же терпимо, как их привязывает SDK (строка "true" для bool, регистр значений action).
    /// </summary>
    public static string? Refusal(string? tool, IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
    {
        if (string.IsNullOrEmpty(tool)) return null;
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (k, v) in arguments ?? []) args[k] = v;

        string? why = tool switch
        {
            _ when AlwaysBlocked.Contains(tool) => "it runs project code or writes files",
            McpToolNames.Diagnostics when HasText(args, "command") || !HasText(args, "log_path") =>
                "without log_path it runs a build command; pass log_path of an existing log",
            McpToolNames.Roles when Text(args, "action") is "define" or "delete" => "define/delete write role files",
            McpToolNames.Impact when IsTrue(args, "run_tests") => "run_tests=true runs the project's tests",
            McpToolNames.Job when Text(args, "action") is "merge" or "retry" or "revert" => "merge/retry/revert write files or rerun an agent",
            McpToolNames.Dependencies when Text(args, "action") is "outdated" or "vulnerable" =>
                "outdated/vulnerable run package-manager commands that evaluate project files",
            _ => null,
        };
        return why is null
            ? null
            : $"{tool} is disabled on this Offload HTTP endpoint: {why}. Code-executing and writing tools are off by default over HTTP; " +
              "the user can enable them by restarting 'Offload.exe --mcp-http --root <dir> --allow-exec'. Read-only tools still work.";
    }

    private static bool HasText(Dictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString());

    private static string? Text(Dictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim().ToLowerInvariant() : null;

    private static bool IsTrue(Dictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var v)
        && (v.ValueKind == JsonValueKind.True
            || (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString()?.Trim(), "true", StringComparison.OrdinalIgnoreCase)));
}
