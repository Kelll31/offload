using System.Text;
using Offload.Core.Processes;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Вызовы git для MCP: без пейджера и цветов, пути в UTF-8 без экранирования, без интерактивных запросов и без внешних
/// команд из конфигурации репозитория (<see cref="GitSafety"/>: fsmonitor, хуки, фильтры, textconv, внешний diff, драйверы слияния).
/// </summary>
internal static partial class Git
{
    private static readonly Lazy<string?> ExeLazy = new(FindExecutable);

    public static string? Executable => ExeLazy.Value;

    private static readonly string[] SafeConfig =
        ["-c", "core.quotepath=false", "-c", "color.ui=never", .. GitSafety.SafeConfig];

    /// <summary>Команды, которые выводят diff: им всегда добавляются --no-ext-diff и --no-textconv.</summary>
    private static readonly HashSet<string> DiffCommands = new(StringComparer.Ordinal)
    {
        "diff", "log", "show", "diff-index", "diff-tree", "diff-files", "whatchanged",
    };

    /// <summary>Сколько держать в памяти найденные в конфигурации репозитория команды (одна проверка на серию вызовов).</summary>
    private static readonly TimeSpan NeutralizerTtl = TimeSpan.FromSeconds(30);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Stamp, DateTime ProbedAt, string[] Args)> Neutralizers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Для тестов: забыть закэшированные проверки конфигурации.</summary>
    internal static void ResetNeutralizerCache() => Neutralizers.Clear();

    private static readonly IReadOnlyDictionary<string, string?> Env = new Dictionary<string, string?>
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_OPTIONAL_LOCKS"] = "0",
        ["GIT_PAGER"] = "cat",
        ["PAGER"] = "cat",
        ["GIT_EXTERNAL_DIFF"] = null,
        ["GIT_ASKPASS"] = null,
    };

    private static string? FindExecutable()
    {
        var onPath = ProcessRunner.FindOnPath("git.exe") ?? ProcessRunner.FindOnPath("git");
        if (onPath is not null && onPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return onPath;
        foreach (var candidate in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd", "git.exe"),
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return onPath;
    }

    /// <summary>Корень рабочей копии git (папка с .git — каталогом или файлом worktree/submodule) или null.</summary>
    public static string? FindWorkTreeRoot(string dir)
    {
        try
        {
            var d = new DirectoryInfo(dir);
            while (d is not null)
            {
                var dotGit = Path.Combine(d.FullName, ".git");
                if (Directory.Exists(dotGit) || File.Exists(dotGit)) return d.FullName;
                d = d.Parent;
            }
        }
        catch
        {
            // Нет доступа — считаем, что не git.
        }
        return null;
    }

    /// <param name="extraEnv">Дополнительные переменные окружения (GIT_INDEX_FILE, автор коммита); null-значение удаляет переменную.</param>
    public static async Task<ChildResult> RunAsync(string workDir, IEnumerable<string> args, CancellationToken ct,
        int maxChars = 16_000_000, TimeSpan? timeout = null, Func<string, bool>? onLine = null,
        IReadOnlyDictionary<string, string?>? extraEnv = null)
    {
        var exe = Executable ?? throw new ToolException("git is not installed or not on PATH.");
        var env = Env;
        if (extraEnv is { Count: > 0 })
        {
            var merged = new Dictionary<string, string?>(Env);
            foreach (var (k, v) in extraEnv) merged[k] = v;
            env = merged;
        }
        var list = args as IReadOnlyList<string> ?? [.. args];
        var sub = SubcommandIndex(list);
        var neutral = await NeutralizersAsync(exe, workDir, sub < 0 ? list : list.Take(sub).ToArray(), env, ct).ConfigureAwait(false);
        return await ChildProcess.RunAsync(exe, [.. SafeConfig, .. neutral, .. WithDiffGuards(list, sub)], new ChildProcess.Options
        {
            WorkingDirectory = workDir,
            Environment = env,
            Timeout = timeout ?? TimeSpan.FromSeconds(60),
            MaxCaptureChars = maxChars,
            OnStdOutLine = onLine,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Переопределения «-c», которые гасят драйверы из конфигурации репозитория (<see cref="GitSafety.TryBuildNeutralizers"/>).
    /// Кэш — на <see cref="NeutralizerTtl"/> и только пока не изменился файл .git/config.
    /// </summary>
    private static async Task<string[]> NeutralizersAsync(string exe, string workDir, IReadOnlyList<string> leading,
        IReadOnlyDictionary<string, string?> env, CancellationToken ct)
    {
        var cacheKey = workDir + "\n" + string.Join("\n", leading);
        var stamp = leading.Count == 0 ? ConfigStamp(workDir) : null;
        if (stamp is not null && Neutralizers.TryGetValue(cacheKey, out var cached) && cached.Stamp == stamp.Value
            && DateTime.UtcNow - cached.ProbedAt < NeutralizerTtl)
            return cached.Args;

        var r = await ChildProcess.RunAsync(exe, [.. SafeConfig, .. leading, .. GitSafety.ListConfigArgs],
            new ChildProcess.Options
            {
                WorkingDirectory = workDir,
                Environment = env,
                Timeout = TimeSpan.FromSeconds(30),
                MaxCaptureChars = 1_000_000,
            }, ct).ConfigureAwait(false);
        if (!r.Success)
            throw new ToolException("Could not read this repository's git configuration, so git was not run " +
                                    "(a repository can define commands that git executes). Fix the .git/config file and retry.");
        var result = BuildNeutralizers(ParseCommandKeys(r.StdOut));
        if (stamp is not null) Neutralizers[cacheKey] = (stamp.Value, DateTime.UtcNow, result);
        return result;
    }

    /// <summary>
    /// Время изменения .git/config обычного репозитория — признак, что кэш проверки ещё действителен. null (не кэшировать):
    /// не git, worktree/подмодуль (.git — файл) или вызов с явным --git-dir.
    /// </summary>
    private static DateTime? ConfigStamp(string workDir)
    {
        try
        {
            if (FindWorkTreeRoot(workDir) is not { } root) return null;
            var config = Path.Combine(root, ".git", "config");
            return File.Exists(config) ? File.GetLastWriteTimeUtc(config) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>«-c ключ=» для каждой найденной команды; имя драйвера, которое в «-c» не выразить, — отказ (git не запускается).</summary>
    internal static string[] BuildNeutralizers(IEnumerable<string> keys) =>
        GitSafety.TryBuildNeutralizers(keys, out var args, out var bad)
            ? args
            : throw new ToolException($"This repository's git configuration defines a driver with an unusual name ({bad}) that " +
                                      "Offload cannot neutralize, so git was not run. Remove it from .git/config and retry.");

    /// <summary>Индекс подкоманды git (первый аргумент после глобальных опций) или -1.</summary>
    internal static int SubcommandIndex(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "-c" or "-C" or "--git-dir" or "--work-tree" or "--namespace") { i++; continue; }
            if (a.StartsWith('-')) continue;
            return i;
        }
        return -1;
    }

    /// <summary>Команды вывода diff — с --no-ext-diff и --no-textconv (внешний diff и textconv из .gitattributes не запускаются).</summary>
    internal static IReadOnlyList<string> WithDiffGuards(IReadOnlyList<string> args, int sub)
    {
        if (sub < 0) return args;
        string[] guards = args[sub] switch
        {
            var c when DiffCommands.Contains(c) => ["--no-ext-diff", "--no-textconv"],
            "blame" or "annotate" => ["--no-textconv"],
            _ => [],
        };
        var missing = guards.Where(g => !args.Contains(g, StringComparer.Ordinal)).ToArray();
        if (missing.Length == 0) return args;
        return [.. args.Take(sub + 1), .. missing, .. args.Skip(sub + 1)];
    }

    /// <summary>
    /// Ключи конфигурации, из-за которых git сам запускает внешние команды при слиянии, status и сравнении с рабочим деревом:
    /// драйверы слияния (merge.*.driver — их запускает merge-tree), фильтры (filter.*.clean/smudge/process — status, diff
    /// с рабочим деревом), внешние diff и textconv. Учитываются только области, которые задаёт сам репозиторий (local,
    /// worktree и подключённые ими include-файлы): глобальные и системные (например, фильтр Git LFS) — выбор пользователя.
    /// Конфигурацию не удалось прочитать — возвращается пометка (считаем небезопасной).
    /// </summary>
    public static async Task<List<string>> RepoCommandConfigAsync(string repo, CancellationToken ct)
    {
        var r = await RunAsync(repo, GitSafety.ListConfigArgs, ct, maxChars: 1_000_000).ConfigureAwait(false);
        return r.Success ? ParseCommandKeys(r.StdOut) : ["(git config could not be read)"];
    }

    /// <inheritdoc cref="GitSafety.ParseCommandKeys"/>
    internal static List<string> ParseCommandKeys(string configList) => GitSafety.ParseCommandKeys(configList);

    /// <summary>Снять C-экранирование пути git ("a\"b", восьмеричные байты UTF-8).</summary>
    public static string Unquote(string s)
    {
        if (s.Length < 2 || s[0] != '"' || s[^1] != '"') return s;
        var bytes = new List<byte>(s.Length);
        for (var i = 1; i < s.Length - 1; i++)
        {
            var c = s[i];
            if (c != '\\' || i + 1 >= s.Length - 1)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                continue;
            }
            var n = s[++i];
            switch (n)
            {
                case 'n': bytes.Add((byte)'\n'); break;
                case 't': bytes.Add((byte)'\t'); break;
                case 'r': bytes.Add((byte)'\r'); break;
                case 'a': bytes.Add(7); break;
                case 'b': bytes.Add(8); break;
                case 'f': bytes.Add(12); break;
                case 'v': bytes.Add(11); break;
                case '"': bytes.Add((byte)'"'); break;
                case '\\': bytes.Add((byte)'\\'); break;
                default:
                    if (n is >= '0' and <= '7')
                    {
                        var j = i;
                        var value = 0;
                        while (j < s.Length - 1 && j < i + 3 && s[j] is >= '0' and <= '7')
                        {
                            value = value * 8 + (s[j] - '0');
                            j++;
                        }
                        bytes.Add((byte)(value & 0xFF));
                        i = j - 1;
                    }
                    else
                    {
                        bytes.Add((byte)n);
                    }
                    break;
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>
    /// Проверка ссылки/диапазона git от LLM: без пробелов, без начального «-» (иначе это опция git) и без опасных символов.
    /// </summary>
    public static bool IsSafeRevision(string rev)
    {
        if (string.IsNullOrWhiteSpace(rev) || rev.Length > 200) return false;
        if (rev.StartsWith('-')) return false;
        foreach (var c in rev)
        {
            if (char.IsAsciiLetterOrDigit(c)) continue;
            if ("._/~^@{}:+-".Contains(c)) continue;
            return false;
        }
        return !rev.Contains("..-") && !rev.Contains("...-");
    }
}
