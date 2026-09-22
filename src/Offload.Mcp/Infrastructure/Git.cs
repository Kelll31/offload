using System.Text;
using Offload.Core.Processes;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Вызовы git для MCP: без пейджера, цветов и fsmonitor (он запускает внешние команды из .git/config),
/// пути в UTF-8 без экранирования, без интерактивных запросов.
/// </summary>
internal static class Git
{
    private static readonly Lazy<string?> ExeLazy = new(FindExecutable);

    public static string? Executable => ExeLazy.Value;

    private static readonly string[] SafeConfig =
    [
        "-c", "core.quotepath=false", "-c", "core.fsmonitor=false", "-c", "color.ui=never", "-c", "core.pager=cat",
        "-c", "core.hooksPath=NUL",
    ];

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

    public static async Task<ChildResult> RunAsync(string workDir, IEnumerable<string> args, CancellationToken ct,
        int maxChars = 16_000_000, TimeSpan? timeout = null, Func<string, bool>? onLine = null)
    {
        var exe = Executable ?? throw new ToolException("git is not installed or not on PATH.");
        return await ChildProcess.RunAsync(exe, [.. SafeConfig, .. args], new ChildProcess.Options
        {
            WorkingDirectory = workDir,
            Environment = Env,
            Timeout = timeout ?? TimeSpan.FromSeconds(60),
            MaxCaptureChars = maxChars,
            OnStdOutLine = onLine,
        }, ct).ConfigureAwait(false);
    }

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
