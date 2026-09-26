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
internal static partial class VerifyCommand
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
        // Линтеры/форматтеры только проверяют: исправление и запись отчётов в файлы — в обход снимков JobStore.
        new("--fix"), new("--output-file"), new("--write"), new("--rulesdir"), new("--plugin"), new("--resolve-plugins-relative-to"),
        new("-o", Prefix: false, Tools: NodeTools), new("-w", Prefix: false, Tools: NodeTools),
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
        var cmd = CheckSyntax(command);
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
    /// Команда, которую можно предложить пользователю «разрешить один раз» (elicitation): проходит все синтаксические запреты,
    /// не совпадает ни с одним шаблоном белого списка, но её программа уже есть в белом списке (отличаются только аргументы).
    /// null — команда уже разрешена, синтаксически запрещена, программа не из белого списка, интерпретатор/LOLBin или команда
    /// с приметами маскировки (длинные пробелы, длинные «слова» без разделителей): такую нельзя разрешить даже с подтверждением.
    /// </summary>
    internal static string? CandidateForOneTimeAllow(string? command, IReadOnlyList<string>? allowlist)
    {
        var raw = (command ?? "").Trim();
        // Длинная вереница пробелов (в том числе в кавычках) прячет хвост команды за краем окна подтверждения.
        if (raw.Contains("   ", StringComparison.Ordinal)) return null;
        string cmd;
        try { cmd = CheckSyntax(command); }
        catch (ToolException) { return null; }
        var list = allowlist ?? [];
        if (list.Any(p => MatchesPattern(cmd, p))) return null;
        var tokens = Tokenize(cmd);
        var program = ProgramName(tokens[0]);
        if (NeverAllowOnce.Contains(program)) return null;
        if (!list.Any(p => ProgramOfPattern(p) is { } pp && pp == program)) return null;
        foreach (var raw1 in tokens)
        {
            // «Слово» длиннее 64 символов без разделителей или похожее на base64 длиннее 40 — закодированная нагрузка, а не аргумент сборки.
            if (LongOpaqueRun().IsMatch(raw1.Trim('"')) || LooksLikeBase64(raw1.Trim('"'))) return null;
        }
        foreach (var raw1 in tokens.Skip(1))
        {
            if (IsInlineCodeArg(program, raw1.Trim('"'))) return null;
        }
        if (NodeTools.Contains(program) && tokens.Count > 1 && NeverAllowOnceNodeSubcommands.Contains(tokens[1].Trim('"'))) return null;
        // npx — только имя локального инструмента (флаги и версии заставили бы npx скачать пакет).
        if (program == "npx" && (tokens.Count < 2 || !NpxToolName().IsMatch(tokens[1]))) return null;
        return cmd;
    }

    /// <summary>
    /// Программы, которые «один раз» не разрешаются никогда: оболочки и интерпретаторы, LOLBins (выполнение/загрузка через
    /// системные утилиты), загрузчики, планировщики и реестр. Даже если пользователь сам добавил их в белый список.
    /// </summary>
    private static readonly HashSet<string> NeverAllowOnce = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell", "pwsh", "powershell_ise", "cmd", "command", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "regsvcs", "regasm",
        "installutil", "msiexec", "certutil", "bitsadmin", "curl", "wget", "ftp", "tftp", "scp", "sftp", "ssh", "schtasks", "at", "sc", "reg",
        "regedit", "wmic", "net", "netsh", "runas", "start", "explorer", "forfiles", "pcalua", "odbcconf", "cmstp", "hh", "control",
        "msdt", "msbuild", "bash", "sh", "zsh", "fish", "wsl", "wslconfig", "conhost", "mavinject", "presentationhost", "infdefaultinstall", "xwizard",
        "diskshadow", "esentutl", "expand", "extrac32", "makecab", "replace", "certreq", "ieexec", "dfsvc", "msconfig",
        "iwr", "irm", "invoke-webrequest", "osascript", "env", "nohup", "sudo", "gsudo",
    };

    /// <summary>Подкоманды npm/pnpm/yarn, которые скачивают и запускают чужой код или меняют проект.</summary>
    private static readonly HashSet<string> NeverAllowOnceNodeSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "exec", "x", "dlx", "create", "init", "install", "i", "add", "ci", "publish", "link", "set-script", "pkg", "config", "explore",
    };

    /// <summary>Интерпретаторы с флагами «выполнить код из аргумента» (node -e, python -c, ruby -e …).</summary>
    private static bool IsInlineCodeArg(string program, string tok)
    {
        if (tok.Length < 2 || tok[0] != '-') return false;
        var longOpt = tok.StartsWith("--", StringComparison.Ordinal);
        var name = longOpt ? tok.Split('=')[0].ToLowerInvariant() : "";
        var cluster = longOpt ? "" : tok[1..];
        switch (program)
        {
            case "node" or "nodejs" or "deno" or "bun" or "tsx" or "ts-node":
                return name is "--eval" or "--print" or "--require" or "--import" or "--loader" or "--experimental-loader" or "--input-type"
                       || cluster.IndexOfAny(['e', 'p', 'r']) >= 0;
            case "python" or "python3" or "py" or "pythonw" or "pypy" or "pypy3":
                return cluster.Contains('c', StringComparison.Ordinal);
            case "ruby" or "perl" or "php" or "lua" or "luajit" or "julia" or "rscript" or "r":
                return cluster.IndexOfAny(['e', 'E', 'r']) >= 0 || name is "--eval" or "--expr";
            default:
                return false;
        }
    }

    /// <summary>Имя программы без пути, расширения и кавычек, в нижнем регистре.</summary>
    private static string ProgramName(string token) => Path.GetFileNameWithoutExtension(token.Trim('"')).ToLowerInvariant();

    /// <summary>Программа шаблона белого списка («dotnet test*» → dotnet); null — пустой шаблон.</summary>
    private static string? ProgramOfPattern(string? pattern)
    {
        var first = (pattern ?? "").Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.TrimEnd('*');
        return string.IsNullOrEmpty(first) ? null : ProgramName(first);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"[^\s/\\.\-_=:,]{65,}", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex LongOpaqueRun();

    /// <summary>
    /// Похоже на base64 (в том числе после «=» ключа): длиннее 40 символов только из алфавита base64, есть и строчные, и заглавные,
    /// и цифра или +/=. Длинное имя теста в PascalCase без цифр сюда не попадает.
    /// </summary>
    internal static bool LooksLikeBase64(string token)
    {
        foreach (var part in new[] { token, token.Contains('=') && !token.EndsWith('=') ? token[(token.IndexOf('=') + 1)..] : token })
        {
            var s = part.TrimEnd('=');
            if (s.Length <= 40) continue;
            if (!s.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '-' or '_')) continue;
            if (s.Any(char.IsAsciiLetterUpper) && s.Any(char.IsAsciiLetterLower) && (s.Any(char.IsAsciiDigit) || s.Any(c => c is '+' or '/')))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Команда в том виде, в котором её выполнит <see cref="RunAsync"/>: npx → .\node_modules\.bin\X.cmd, gradlew/mvnw →
    /// .\gradlew.bat (обёртка из корня проекта). Её и показывает окно подтверждения — пользователь видит то, что запустится.
    /// ToolException — npx-пакета нет в проекте.
    /// </summary>
    internal static string FinalCommand(string validatedCommand, string workDir)
    {
        var cmd = ResolveNpx(validatedCommand, workDir);
        var first = Tokenize(cmd)[0];
        if (first is "gradlew" or "gradlew.bat" or "mvnw" or "mvnw.cmd")
        {
            string[] candidates = first.Contains('.') ? [first] : [first + ".bat", first + ".cmd", first];
            foreach (var c in candidates)
            {
                if (!File.Exists(Path.Combine(workDir, c))) continue;
                return ".\\" + c + cmd[first.Length..];
            }
        }
        return cmd;
    }

    /// <summary>Синтаксические запреты (без белого списка): метасимволы, не-ASCII, пути в имени программы, опасные аргументы. Нормализованная команда.</summary>
    private static string CheckSyntax(string? command)
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
        return cmd;
    }

    /// <summary>
    /// Внутренние команды инструментов (local_dependency_check) — отдельный фиксированный белый список, не пользовательский.
    /// {manifest} — относительный путь к .sln/.slnx/.csproj/.fsproj/.vbproj без «..», ключей и метасимволов.
    /// </summary>
    internal static readonly string[] InternalCommands =
    [
        "dotnet list {manifest} package --outdated --format json",
        "dotnet list {manifest} package --vulnerable --include-transitive --format json",
        "npm outdated --json",
        "npm audit --json",
        "pip list --outdated --format=json",
    ];

    /// <summary>
    /// Проверить внутреннюю команду: те же синтаксические запреты, что у verify_command (метасимволы, опасные аргументы,
    /// абсолютные пути), и точное совпадение с одним из <see cref="InternalCommands"/>. ToolException при отказе.
    /// </summary>
    public static string ValidateInternal(string command)
    {
        var cmd = CheckSyntax(command);
        var tokens = Tokenize(cmd);
        foreach (var template in InternalCommands)
        {
            var t = template.Split(' ');
            if (t.Length != tokens.Count) continue;
            var ok = true;
            for (var i = 0; i < t.Length && ok; i++)
                ok = t[i] == "{manifest}" ? IsSafeManifestArg(tokens[i]) : t[i].Equals(tokens[i], StringComparison.OrdinalIgnoreCase);
            if (ok) return cmd;
        }
        throw new ToolException($"Internal command \"{cmd}\" is not one of the fixed dependency-check commands; refusing to run it.");
    }

    /// <summary>Аргумент-манифест: «src/App/App.csproj» или «"My App.sln"» — без ключей (-x, /x), @rsp, «..», корней и метасимволов.</summary>
    internal static bool IsSafeManifestArg(string token)
    {
        var quoted = token.Length >= 2 && token[0] == '"' && token[^1] == '"';
        var v = quoted ? token[1..^1] : token;
        if (v.Contains('"') || !quoted && v.Contains(' ')) return false;
        if (!ManifestArg().IsMatch(v)) return false;
        return !v.Split('/').Any(s => s is "" or "." or "..");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_. /\-]{0,200}\.(?:sln|slnx|csproj|fsproj|vbproj)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ManifestArg();

    /// <summary>Имена пакетов для подсказки «установите локально» (npx tsc → typescript).</summary>
    private static readonly Dictionary<string, string> NpxPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tsc"] = "typescript",
    };

    /// <summary>
    /// «npx X …» запускается только из node_modules/.bin проекта: команда переписывается в «.\node_modules\.bin\X.cmd …».
    /// Если пакета нет, npx скачал бы его из реестра (чужой код по имени, выбранному моделью) — отказ с объяснением.
    /// Остальные команды возвращаются без изменений.
    /// </summary>
    public static string ResolveNpx(string validatedCommand, string workDir)
    {
        var tokens = Tokenize(validatedCommand);
        if (tokens.Count == 0 || !Path.GetFileNameWithoutExtension(tokens[0].Trim('"')).Equals("npx", StringComparison.OrdinalIgnoreCase))
            return validatedCommand;
        var tool = tokens.Count > 1 ? tokens[1].Trim('"') : "";
        if (!NpxToolName().IsMatch(tool))
            throw new ToolException(
                $"verify_command: npx must be followed by the name of a tool installed in the project (e.g. \"npx eslint .\"); '{tool}' is not allowed (flags and package specs like eslint@8 make npx download code from the registry).");
        var bin = Path.Combine(workDir, "node_modules", ".bin", tool + ".cmd");
        if (!File.Exists(bin))
        {
            var package = NpxPackages.GetValueOrDefault(tool, tool);
            throw new ToolException(
                $"verify_command: '{tool}' is not installed in this project (node_modules/.bin/{tool}.cmd not found). Offload does not let npx download packages from the registry. " +
                $"Install the dependency first (npm install --save-dev {package}, or npm ci), or use an npm script such as \"npm run lint\".");
        }
        return @".\node_modules\.bin\" + tool + ".cmd" + (tokens.Count > 2 ? " " + string.Join(' ', tokens.Skip(2)) : "");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex NpxToolName();

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

    /// <param name="onLine">Каждая строка вывода (stdout и stderr, из разных потоков) — например, для записи полного лога.</param>
    public static async Task<VerifyResult> RunAsync(string validatedCommand, string workDir, TimeSpan timeout, ProgressReporter progress, CancellationToken ct,
        Action<string>? onLine = null)
    {
        // npx — только локальный бинарник из node_modules/.bin (в песочнице node_modules подключён junction'ом);
        // обёртки из самого репозитория (gradlew, mvnw) запускаются явно из корня проекта.
        var cmd = FinalCommand(validatedCommand, workDir);
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
                    onLine?.Invoke(l);
                    Interlocked.Increment(ref lines);
                    if (l.Trim().Length > 0) last = l.Trim();
                },
            }, ct).ConfigureAwait(false);
            return new VerifyResult(validatedCommand, res.ExitCode, res.TimedOut, res.Tail, res.Duration);
        }
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
