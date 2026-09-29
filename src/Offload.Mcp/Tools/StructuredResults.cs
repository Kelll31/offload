using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Offload.Mcp.Tools;

// Структурированные результаты (outputSchema + structuredContent) для local_verify, local_diagnostics, local_impact, local_status.
// Текстовый ответ остаётся прежним (совместимость); IDE и агенты разбирают эти поля без регулярных выражений.
// Имена полей — snake_case (как параметры инструментов); описания — английские (их читает модель).

/// <summary>Одна диагностика компилятора/линтера.</summary>
internal sealed record DiagnosticItem
{
    [JsonPropertyName("file"), Description("Path relative to the project root when inside it.")]
    public required string File { get; init; }

    [JsonPropertyName("line")]
    public required int Line { get; init; }

    [JsonPropertyName("column"), Description("0 when unknown.")]
    public int Column { get; init; }

    [JsonPropertyName("severity"), Description("error | warning | info")]
    public required string Severity { get; init; }

    [JsonPropertyName("code"), Description("Compiler/linter code, e.g. CS0103.")]
    public string? Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("symbol"), Description("Enclosing symbol in the source file, when resolved.")]
    public string? Symbol { get; init; }

    [JsonPropertyName("source_line"), Description("The source line the diagnostic points at, trimmed.")]
    public string? SourceLine { get; init; }
}

/// <summary>Итог local_verify.</summary>
internal sealed record VerifyOutput
{
    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("status"), Description("passed | failed | timed_out")]
    public required string Status { get; init; }

    [JsonPropertyName("exit_code")]
    public required int ExitCode { get; init; }

    [JsonPropertyName("duration_sec")]
    public required double DurationSec { get; init; }

    [JsonPropertyName("output_lines")]
    public required long OutputLines { get; init; }

    [JsonPropertyName("log"), Description("Full log file, relative to the project root.")]
    public required string Log { get; init; }

    [JsonPropertyName("log_uri"), Description("MCP resource with the full log (resources/read).")]
    public required string LogUri { get; init; }

    [JsonPropertyName("summary"), Description("Summary lines of the test runner / build.")]
    public required IReadOnlyList<string> Summary { get; init; }

    [JsonPropertyName("warning_count")]
    public required int WarningCount { get; init; }

    [JsonPropertyName("warnings"), Description("First distinct warnings (only when the command passed).")]
    public required IReadOnlyList<string> Warnings { get; init; }

    [JsonPropertyName("errors"), Description("Structured compiler/linter errors (first 15).")]
    public required IReadOnlyList<DiagnosticItem> Errors { get; init; }

    [JsonPropertyName("error_count"), Description("Distinct structured errors found in the log.")]
    public required int ErrorCount { get; init; }

    [JsonPropertyName("error_lines"), Description("Distinct error-looking output lines when no structured diagnostics were recognized.")]
    public required IReadOnlyList<string> ErrorLines { get; init; }

    [JsonPropertyName("stack_frames"), Description("Stack frames in project code: path:line in Symbol - code.")]
    public required IReadOnlyList<string> StackFrames { get; init; }

    [JsonPropertyName("analysis"), Description("Local model analysis of a failure, when requested and available.")]
    public string? Analysis { get; init; }
}

/// <summary>Итог local_diagnostics.</summary>
internal sealed record DiagnosticsOutput
{
    [JsonPropertyName("source"), Description("Log that was parsed, relative to the project root.")]
    public required string Source { get; init; }

    [JsonPropertyName("log_uri"), Description("MCP resource with the full log when the command was run by Offload.")]
    public string? LogUri { get; init; }

    [JsonPropertyName("command"), Description("Command that was run (null when an existing log was parsed).")]
    public string? Command { get; init; }

    [JsonPropertyName("exit_code")]
    public int? ExitCode { get; init; }

    [JsonPropertyName("timed_out")]
    public bool TimedOut { get; init; }

    [JsonPropertyName("error_count"), Description("All errors parsed from the log (before filters).")]
    public required int ErrorCount { get; init; }

    [JsonPropertyName("warning_count"), Description("All warnings parsed from the log (before filters).")]
    public required int WarningCount { get; init; }

    [JsonPropertyName("diagnostics"), Description("Diagnostics after severity/paths filters, up to max_results.")]
    public required IReadOnlyList<DiagnosticItem> Diagnostics { get; init; }

    [JsonPropertyName("more"), Description("Matching diagnostics beyond max_results.")]
    public required int More { get; init; }

    [JsonPropertyName("stack_frames")]
    public required IReadOnlyList<string> StackFrames { get; init; }
}

/// <summary>Изменённый символ.</summary>
internal sealed record ImpactSymbol
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("file")]
    public required string File { get; init; }

    [JsonPropertyName("line")]
    public required int Line { get; init; }
}

/// <summary>Вызов изменённого символа из другого места.</summary>
internal sealed record ImpactCaller
{
    [JsonPropertyName("symbol"), Description("The changed symbol.")]
    public required string Symbol { get; init; }

    [JsonPropertyName("caller"), Description("The calling function or method.")]
    public required string Caller { get; init; }

    [JsonPropertyName("file")]
    public required string File { get; init; }

    [JsonPropertyName("line")]
    public required int Line { get; init; }
}

/// <summary>Связанный тестовый файл.</summary>
internal sealed record ImpactTest
{
    [JsonPropertyName("file")]
    public required string File { get; init; }

    [JsonPropertyName("classes")]
    public required IReadOnlyList<string> Classes { get; init; }

    /// <summary>Найден только по смыслу (векторы роли embed): подсказка, run_tests его не запускает.</summary>
    [JsonPropertyName("semantic"), Description("Found only by meaning (embeddings); a suggestion, not run by run_tests.")]
    public bool Semantic { get; init; }
}

/// <summary>Запуск связанных тестов.</summary>
internal sealed record ImpactTestRun
{
    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("status"), Description("passed | failed | timed_out")]
    public required string Status { get; init; }

    [JsonPropertyName("exit_code")]
    public required int ExitCode { get; init; }

    [JsonPropertyName("duration_sec")]
    public required double DurationSec { get; init; }

    [JsonPropertyName("log")]
    public required string Log { get; init; }

    [JsonPropertyName("log_uri")]
    public required string LogUri { get; init; }

    [JsonPropertyName("summary")]
    public required IReadOnlyList<string> Summary { get; init; }

    [JsonPropertyName("errors")]
    public required IReadOnlyList<string> Errors { get; init; }
}

/// <summary>Итог local_impact.</summary>
internal sealed record ImpactOutput
{
    [JsonPropertyName("changed_files")]
    public required IReadOnlyList<string> ChangedFiles { get; init; }

    [JsonPropertyName("changed_symbols")]
    public required IReadOnlyList<ImpactSymbol> ChangedSymbols { get; init; }

    [JsonPropertyName("callers"), Description("Callers outside tests (up to 8 per symbol).")]
    public required IReadOnlyList<ImpactCaller> Callers { get; init; }

    [JsonPropertyName("related_tests")]
    public required IReadOnlyList<ImpactTest> RelatedTests { get; init; }

    [JsonPropertyName("projects"), Description("Project manifests that own the affected files.")]
    public required IReadOnlyList<string> Projects { get; init; }

    [JsonPropertyName("test_runs"), Description("Only with run_tests=true.")]
    public required IReadOnlyList<ImpactTestRun> TestRuns { get; init; }
}

/// <summary>Состояние локальной модели.</summary>
internal sealed record StatusModel
{
    [JsonPropertyName("state"), Description("ready | loading | offline | not_set_up")]
    public required string State { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("context_per_request"), Description("Tokens per request (one slot), when the server is ready.")]
    public int? ContextPerRequest { get; init; }

    [JsonPropertyName("slots")]
    public int? Slots { get; init; }

    [JsonPropertyName("asleep"), Description("The model is unloaded to save memory and reloads on the next call.")]
    public bool Asleep { get; init; }

    [JsonPropertyName("remote"), Description("host:port of the remote model server when the main model runs on another PC; null for the local server.")]
    public string? Remote { get; init; }

    [JsonPropertyName("latency_ms"), Description("Round-trip time of the remote server's /health, ms; null for the local server or when unreachable.")]
    public int? LatencyMs { get; init; }
}

/// <summary>Очередь GPU.</summary>
internal sealed record StatusQueue
{
    [JsonPropertyName("busy")]
    public required int Busy { get; init; }

    [JsonPropertyName("slots")]
    public required int Slots { get; init; }

    [JsonPropertyName("waiting")]
    public required int Waiting { get; init; }
}

/// <summary>Вспомогательный сервер роли модели (fast, embed, rerank).</summary>
internal sealed record StatusRole
{
    [JsonPropertyName("role"), Description("fast | embed | rerank")]
    public required string Role { get; init; }

    [JsonPropertyName("state"), Description("running | loading | idle (starts on the first call) | not_configured")]
    public required string State { get; init; }

    [JsonPropertyName("model"), Description("Assigned model; null when not configured.")]
    public string? Model { get; init; }

    [JsonPropertyName("port"), Description("Port of the role's llama-server on the local host; null when not configured.")]
    public int? Port { get; init; }
}

/// <summary>Итог local_status.</summary>
internal sealed record StatusOutput
{
    [JsonPropertyName("model")]
    public required StatusModel Model { get; init; }

    [JsonPropertyName("speed_tps"), Description("Generation speed, tokens/s (null until measured).")]
    public double? SpeedTps { get; init; }

    [JsonPropertyName("queue")]
    public required StatusQueue Queue { get; init; }

    [JsonPropertyName("agent_mode"), Description("OpenCode agent for local_edit_files: available | disabled | not_installed")]
    public required string AgentMode { get; init; }

    [JsonPropertyName("saved_tokens_session")]
    public required long SavedTokensSession { get; init; }

    [JsonPropertyName("calls_session")]
    public required long CallsSession { get; init; }

    [JsonPropertyName("saved_tokens_lifetime")]
    public required long SavedTokensLifetime { get; init; }

    [JsonPropertyName("calls_lifetime")]
    public required long CallsLifetime { get; init; }

    [JsonPropertyName("workspace")]
    public required IReadOnlyList<string> Workspace { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("roles"), Description("Auxiliary role servers (fast/embed/rerank); absent in older versions.")]
    public IReadOnlyList<StatusRole>? Roles { get; init; }
}
