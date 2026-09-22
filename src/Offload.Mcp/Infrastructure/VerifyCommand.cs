using System.Text;

namespace Offload.Mcp.Infrastructure;

internal sealed record VerifyResult(string Command, int ExitCode, bool TimedOut, IReadOnlyList<string> Tail, TimeSpan Duration)
{
    public bool Passed => ExitCode == 0 && !TimedOut;

    /// <summary>Для IDE: одна строка при успехе; при ошибке — ещё последние строки вывода (не больше maxLines/maxChars).</summary>
    public string Summary(int attempt, int maxLines = 40, int maxChars = 4000)
    {
        var head = TimedOut
            ? $"verify: `{Command}` → TIMED OUT after {Duration.TotalSeconds:0} s (attempt {attempt})"
            : $"verify: `{Command}` → exit {ExitCode} ({Duration.TotalSeconds:0} s, attempt {attempt})";
        if (Passed) return head;
        var lines = Tail.Where(l => l.Trim().Length > 0).TakeLast(maxLines).Select(l => l.Length > 300 ? l[..300] + "…" : l).ToList();
        var sb = new StringBuilder(head).Append("\nlast output lines:\n");
        var body = string.Join('\n', lines);
        if (body.Length > maxChars) body = "…" + body[^maxChars..];
        sb.Append(body);
        return sb.ToString();
    }

    /// <summary>Для модели: отфильтрованный вывод (ошибки и окружение, хвост), не больше maxChars.</summary>
    public string ForModel(int maxChars = 6000)
    {
        var digest = LogPrefilter.Filter(Tail, headLines: 10, tailLines: 60, context: 2);
        return LogPrefilter.Render(digest, maxChars);
    }
}

/// <summary>
/// Проверочная команда (verify_command): только из списка разрешённых шаблонов, без метасимволов cmd.exe,
/// без опасных аргументов; запуск через «cmd.exe /d /s /c» в корне проекта с таймаутом и ограничением вывода.
/// </summary>
internal static class VerifyCommand
{
    /// <summary>Символы, которые cmd.exe трактует как операторы/подстановки (или опасны после «best-fit» преобразований).</summary>
    private const string ForbiddenChars = "&|;<>^`$%!()\r\n\t\0";

    /// <summary>Правило запрета аргумента. Tools — только для этих программ (null — для всех).</summary>
    private sealed record ArgRule(string Text, bool Prefix = true, bool CaseSensitive = false, string[]? Tools = null);

    private static readonly string[] NodeTools = ["npm", "npx", "pnpm", "yarn"];

    /// <summary>
    /// Аргументы, через которые разрешённая команда может выполнить произвольный код: свойства/цели/логгеры MSBuild
    /// (-p:PreBuildEvent=…), response-файлы, -exec/-toolexec (go), --config/-Z (cargo), -D (surefire jvm=…),
    /// init-скрипты gradle, оболочка npm, --eval make, -S ctest и т.п. Защита «в глубину» поверх списка разрешённых команд.
    /// </summary>
    private static readonly ArgRule[] ForbiddenArgs =
    [
        new("-p:"), new("/p:"), new("-property:"), new("/property:"), new("--property"), new("-t:"), new("/t:"),
        new("-target:"), new("/target:"), new("-rp:"), new("/rp:"), new("-restoreproperty:"), new("/restoreproperty:"),
        new("-logger:"), new("/logger:"), new("-l:"), new("/l:"), new("-dl:"), new("/dl:"), new("-distributedlogger"),
        new("/distributedlogger"), new("-noautoresponse"), new("/noautoresponse"), new("-noautorsp"), new("/noautorsp"), new("@"),
        new("--test-adapter-path"), new("-exec"), new("--exec"), new("-toolexec"), new("--toolexec"), new("--config"),
        new("--script-shell"), new("--shell"), new("--node-options"), new("--userconfig"), new("--globalconfig"),
        new("--registry"), new("--prefix"), new("--call"), new("--package"), new("--eval"), new("--python-executable"),
        new("--init-script"), new("--testrunner"), new("--globalsetup"), new("--globalteardown"), new("--setupfiles"),
        new("--script"), new("--build-and-test"), new("--build-options"), new("--include-dir"), new("--makefile"),
        new("--file"), new("--directory"),
        new("-Z", CaseSensitive: true, Tools: ["cargo"]),
        new("-I", CaseSensitive: true, Tools: ["gradle", "gradlew", "make"]), new("-P", CaseSensitive: true, Tools: ["gradle", "gradlew"]),
        new("-c", Prefix: false, Tools: NodeTools),
        new("-f", Prefix: false, Tools: ["make"]), new("-C", Prefix: false, CaseSensitive: true, Tools: ["make"]),
        new("-E", Prefix: false, CaseSensitive: true, Tools: ["make"]),
        new("-S", CaseSensitive: true, Tools: ["ctest"]),
    ];

    /// <summary>Проверить и нормализовать команду. ToolException с объяснением при отказе.</summary>
    public static string Validate(string? command, IReadOnlyList<string>? allowlist)
    {
        var cmd = (command ?? "").Trim();
        if (cmd.Length == 0) throw new ToolException("verify_command is empty.");
        if (cmd.Length > 400) throw new ToolException("verify_command is too long (max 400 chars).");
        foreach (var ch in cmd)
        {
            if (ch > 0x7E || (ch < 0x20 && ch != '\t'))
                throw new ToolException("verify_command may contain only plain ASCII characters.");
            if (ForbiddenChars.Contains(ch))
                throw new ToolException(
                    $"verify_command contains '{Printable(ch)}': shell operators, redirections, variables and grouping are not allowed. Pass a single command, e.g. \"dotnet test --filter FooTests\".");
        }
        if (cmd.Count(c => c == '"') % 2 != 0) throw new ToolException("verify_command has unbalanced quotes.");
        cmd = string.Join(' ', Tokenize(cmd));

        var tokens = Tokenize(cmd);
        if (tokens.Count == 0) throw new ToolException("verify_command is empty.");
        var first = tokens[0].Trim('"');
        if (first.Contains('\\') || first.Contains('/') || first.Contains(':'))
            throw new ToolException("verify_command must start with a plain tool name from the allowlist (no paths).");

        var tool = Path.GetFileNameWithoutExtension(first).ToLowerInvariant();
        foreach (var raw in tokens.Skip(1))
        {
            var tok = raw.Trim('"');
            var bad = FindForbiddenArg(tool, tok);
            if (bad is not null)
                throw new ToolException($"verify_command argument '{tok}' is not allowed (it could run arbitrary code). Use a plain build/test command.");
            if (IsAbsoluteOrEscaping(tok))
                throw new ToolException($"verify_command argument '{tok}' points outside the project; use paths relative to the project root.");
        }

        var list = allowlist ?? [];
        if (!list.Any(p => MatchesPattern(cmd, p)))
        {
            var sample = string.Join(", ", list.Take(12).Select(p => $"\"{p}\""));
            throw new ToolException(
                $"verify_command \"{cmd}\" is not in the Offload allowlist. Allowed patterns: {sample}{(list.Count > 12 ? ", …" : "")}. " +
                "The user can extend the list in the Offload tray app (Settings → MCP).");
        }
        return cmd;
    }

    /// <summary>
    /// Шаблон «X*»: команда равна X или начинается с «X » / «X:» (npm-скрипты test:unit); «X *» — X и аргументы;
    /// без «*» — точное совпадение. Регистр не учитывается.
    /// </summary>
    internal static bool MatchesPattern(string cmd, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var p = string.Join(' ', pattern.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (pattern.TrimEnd().EndsWith(" *", StringComparison.Ordinal)) p = p[..^1] + "*";
        if (!p.EndsWith('*')) return cmd.Equals(p, StringComparison.OrdinalIgnoreCase);
        var prefix = p[..^1];
        if (prefix.EndsWith(' ')) return cmd.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && cmd.Length > prefix.Length;
        if (cmd.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return cmd.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase) || cmd.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindForbiddenArg(string tool, string tok)
    {
        // Системные свойства -D: разрешено только -Dtest=… (выбор тестов maven); остальное (jvm=…, exec.*) — нет.
        if (tok.StartsWith("-D", StringComparison.Ordinal) && !tok.StartsWith("-Dtest=", StringComparison.Ordinal)) return "-D";
        // make VAR=value переопределяет команды рецептов.
        if (tool == "make" && tok.Length > 1 && (char.IsAsciiLetter(tok[0]) || tok[0] == '_') && tok.Contains('=')
            && tok[..tok.IndexOf('=')].All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return "VAR=";
        foreach (var r in ForbiddenArgs)
        {
            if (r.Tools is not null && !r.Tools.Contains(tool)) continue;
            var cmp = r.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (r.Prefix ? tok.StartsWith(r.Text, cmp) : tok.Equals(r.Text, cmp)) return r.Text;
        }
        return null;
    }

    private static bool IsAbsoluteOrEscaping(string tok)
    {
        // Значение после «=» тоже проверяем (--results-directory=C:\…).
        var value = tok.Contains('=') ? tok[(tok.IndexOf('=') + 1)..] : tok;
        foreach (var v in new[] { tok, value })
        {
            if (v.Length >= 2 && char.IsAsciiLetter(v[0]) && v[1] == ':') return true;
            if (v.StartsWith(@"\\", StringComparison.Ordinal) || v.StartsWith("//", StringComparison.Ordinal)) return true;
            if (v.Replace('\\', '/').Split('/').Any(s => s == "..")) return true;
        }
        return false;
    }

    private static List<string> Tokenize(string cmd)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        var inQuote = false;
        foreach (var c in cmd)
        {
            if (c == '"') inQuote = !inQuote;
            if (!inQuote && (c == ' ' || c == '\t'))
            {
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    private static string Printable(char c) => c switch
    {
        '\r' => "\\r",
        '\n' => "\\n",
        '\t' => "\\t",
        '\0' => "\\0",
        _ => c.ToString(),
    };

    /// <summary>Окружение проверочной команды: неинтерактивно (CI), без цветов и телеметрии, без повторного использования узлов MSBuild.</summary>
    private static readonly IReadOnlyDictionary<string, string?> Env = new Dictionary<string, string?>
    {
        ["CI"] = "1",
        ["NO_COLOR"] = "1",
        ["FORCE_COLOR"] = "0",
        ["TERM"] = "dumb",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        ["DOTNET_CLI_UI_LANGUAGE"] = "en-US",
        ["VSLANG"] = "1033",
        ["MSBUILDDISABLENODEREUSE"] = "1",
        ["npm_config_yes"] = "false",
        ["npm_config_fund"] = "false",
        ["npm_config_audit"] = "false",
        ["npm_config_update_notifier"] = "false",
        ["PYTHONIOENCODING"] = "utf-8",
        ["PYTHONUTF8"] = "1",
        ["GIT_TERMINAL_PROMPT"] = "0",
        // cmd.exe не ищет программы в текущей папке: «dotnet.cmd» в репозитории не подменит dotnet.
        ["NoDefaultCurrentDirectoryInExePath"] = "1",
        ["CLAUDE_PROJECT_DIR"] = null,
    };

    public static async Task<VerifyResult> RunAsync(string validatedCommand, string workDir, TimeSpan timeout, ProgressReporter progress, CancellationToken ct)
    {
        var cmd = validatedCommand;
        var tokens = Tokenize(cmd);
        // Обёртки из самого репозитория (gradlew, mvnw) запускаются явно из корня проекта.
        var first = tokens[0];
        if (first is "gradlew" or "gradlew.bat" or "mvnw" or "mvnw.cmd")
        {
            string[] candidates = first.Contains('.') ? [first] : [first + ".bat", first + ".cmd", first];
            foreach (var c in candidates)
            {
                if (!File.Exists(Path.Combine(workDir, c))) continue;
                cmd = ".\\" + c + cmd[first.Length..];
                break;
            }
        }
        var cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var raw = $"/d /s /c \"chcp 65001 >nul & {cmd}\"";
        var lines = 0L;
        var last = "";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        progress.Report($"verify: running `{validatedCommand}`");
        await using (progress.StartHeartbeat(() => $"verify: `{Shorten(validatedCommand, 60)}` running {sw.Elapsed.TotalSeconds:0} s, {Interlocked.Read(ref lines)} lines{(last.Length > 0 ? ": " + Shorten(last, 80) : "")}"))
        {
            var res = await ChildProcess.RunRawAsync(cmdExe, raw, new ChildProcess.Options
            {
                WorkingDirectory = workDir,
                Environment = Env,
                Timeout = timeout,
                MaxCaptureChars = 0,
                TailLines = 300,
                MaxLineChars = 1000,
                OnAnyLine = l =>
                {
                    Interlocked.Increment(ref lines);
                    if (l.Trim().Length > 0) last = l.Trim();
                },
            }, ct).ConfigureAwait(false);
            return new VerifyResult(validatedCommand, res.ExitCode, res.TimedOut, res.Tail, res.Duration);
        }
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
