using System.Text.RegularExpressions;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Пути WSL для MCP-процесса, который Claude Code внутри Linux-дистрибутива запускает через interop
/// (<c>/mnt/c/…/Offload.exe --mcp --wsl-distro Ubuntu</c>). Linux-пути клиента переводятся в Windows-форму:
/// <c>/mnt/c/x</c> → <c>C:\x</c>, <c>/home/u/proj</c> → <c>\\wsl.localhost\Ubuntu\home\u\proj</c>.
/// Из сетевых путей допускается только префикс <c>\\wsl.localhost\&lt;известный дистрибутив&gt;\</c>; дистрибутив известен
/// лишь из аргумента <see cref="DistroArg"/> или текущей папки процесса (interop ставит её в UNC-форме). Без этого
/// (обычный запуск из Windows-IDE) поведение прежнее: любые UNC отклоняются.
/// </summary>
internal static partial class WslPaths
{
    /// <summary>Аргумент командной строки с именем дистрибутива (записывается интеграцией claude-code-wsl).</summary>
    public const string DistroArg = "--wsl-distro";

    /// <summary>Единственный допустимый сетевой префикс (без имени дистрибутива).</summary>
    public const string UncHost = @"\\wsl.localhost\";

    /// <summary>Старое имя общего ресурса WSL — приводится к <see cref="UncHost"/>.</summary>
    private const string LegacyUncHost = @"\\wsl$\";

    /// <summary>Linux-папки, которые не являются файлами проекта (устройства, псевдо-ФС ядра) — не читаются никогда.</summary>
    private static readonly string[] DeviceDirs = ["dev", "proc", "sys"];

    private static string? _processDistro;

    private sealed record Holder(string? Distro);

    private static readonly AsyncLocal<Holder?> TestDistro = new();

    /// <summary>Дистрибутив, из которого запущен процесс; null — процесс запущен не из WSL.</summary>
    public static string? Distro => TestDistro.Value is { } h ? h.Distro : Volatile.Read(ref _processDistro);

    public static bool IsActive => Distro is not null;

    /// <summary>Определить дистрибутив при старте: аргумент <see cref="DistroArg"/>, иначе текущая папка \\wsl.localhost\&lt;d&gt;\….</summary>
    public static void Configure(IReadOnlyList<string> args, string currentDirectory)
    {
        var d = FromArgs(args) ?? FromUncPath(currentDirectory);
        Volatile.Write(ref _processDistro, d);
    }

    /// <summary>Только для тестов: подменить дистрибутив в текущем асинхронном контексте.</summary>
    internal static IDisposable Override(string? distro)
    {
        var previous = TestDistro.Value;
        TestDistro.Value = new Holder(distro is not null && IsValidDistroName(distro) ? distro : null);
        return new Restore(previous);
    }

    internal static string? FromArgs(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], DistroArg, StringComparison.OrdinalIgnoreCase) && IsValidDistroName(args[i + 1])) return args[i + 1];
        }
        return null;
    }

    /// <summary>Имя дистрибутива из пути \\wsl.localhost\&lt;d&gt;\… или \\wsl$\&lt;d&gt;\… (иначе null).</summary>
    internal static string? FromUncPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var p = NormalizeHost(path);
        if (!p.StartsWith(UncHost, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = p[UncHost.Length..];
        var end = rest.IndexOf('\\');
        var name = end < 0 ? rest : rest[..end];
        return IsValidDistroName(name) ? name : null;
    }

    /// <summary>Имя дистрибутива: буквы, цифры, «.», «_», «-» (как допускает wsl --import), не длиннее 64 символов.</summary>
    public static bool IsValidDistroName(string? name) => name is not null && DistroName().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex DistroName();

    /// <summary>\\wsl$\… → \\wsl.localhost\… (тот же ресурс); прочее без изменений.</summary>
    public static string NormalizeHost(string path) =>
        path.StartsWith(LegacyUncHost, StringComparison.OrdinalIgnoreCase) ? UncHost + path[LegacyUncHost.Length..] : path;

    /// <summary>Префикс \\wsl.localhost\&lt;известный дистрибутив&gt;\ (null — WSL не активен).</summary>
    private static string? Prefix => Distro is { } d ? UncHost + d + "\\" : null;

    /// <summary>
    /// Разрешённый сетевой путь: \\wsl.localhost\&lt;известный дистрибутив&gt;\… (полный, без «..»), не устройство
    /// и не псевдо-ФС ядра. Другие UNC, \\?\ и \\.\ — false всегда.
    /// </summary>
    public static bool IsAllowedUnc(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath) || Prefix is not { } prefix) return false;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = fullPath[prefix.Length..];
        if (rest.Contains('/') || rest.Contains(':')) return false;
        var segments = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s is "." or "..")) return false;
        if (segments.Length > 0 && DeviceDirs.Contains(segments[0], StringComparer.Ordinal)) return false;
        // /mnt/<буква> — это диск Windows: такой путь должен приводиться к C:\… (ToDrivePath) и проверяться как обычный.
        return ToDrivePath(fullPath) is null;
    }

    /// <summary>
    /// \\wsl.localhost\&lt;d&gt;\mnt\c\x → C:\x (тот же файл через drvfs): проверки Windows-путей (AppData, данные Offload)
    /// нельзя обойти через общий ресурс WSL. null — путь не из /mnt/&lt;буква&gt; известного дистрибутива.
    /// </summary>
    public static string? ToDrivePath(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath) || Prefix is not { } prefix) return null;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = fullPath[prefix.Length..];
        if (!rest.StartsWith(@"mnt\", StringComparison.Ordinal) || rest.Length < 5 || !char.IsAsciiLetter(rest[4])) return null;
        if (rest.Length > 5 && rest[5] != '\\') return null;
        return char.ToUpperInvariant(rest[4]) + @":\" + (rest.Length > 6 ? rest[6..] : "");
    }

    /// <summary>
    /// Абсолютный Linux-путь клиента → Windows-путь: /mnt/c/x → C:\x (всегда), /home/u → \\wsl.localhost\&lt;d&gt;\home\u
    /// (только при известном дистрибутиве). null — это не Linux-путь (или перевести нельзя). Результат ещё не проверен:
    /// вызывающий обязан нормализовать его (GetFullPath) и проверить префикс.
    /// </summary>
    public static string? LinuxToWindows(string path)
    {
        if (path.Length == 0 || path[0] != '/' || path.StartsWith("//", StringComparison.Ordinal)) return null;
        // Обратная косая черта в Linux-пути — часть имени файла, а в Windows стала бы разделителем: такие пути не переводим.
        if (path.Contains('\\')) return null;
        if (path.StartsWith("/mnt/", StringComparison.Ordinal) && path.Length >= 6 && char.IsAsciiLetter(path[5])
            && (path.Length == 6 || path[6] == '/'))
            return char.ToUpperInvariant(path[5]) + @":\" + (path.Length > 7 ? path[7..].Replace('/', '\\') : "");
        if (Distro is not { } d) return null;
        return UncHost + d + path.Replace('/', '\\');
    }

    /// <summary>
    /// Windows-путь → вид для клиента в WSL: \\wsl.localhost\&lt;d&gt;\x → /x, C:\x → /mnt/c/x. Без WSL или для других путей —
    /// без изменений.
    /// </summary>
    public static string ToClientPath(string fullPath)
    {
        if (Prefix is not { } prefix) return fullPath;
        if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return "/" + fullPath[prefix.Length..].Replace('\\', '/');
        if (fullPath.Length >= 3 && char.IsAsciiLetter(fullPath[0]) && fullPath[1] == ':' && fullPath[2] == '\\')
            return "/mnt/" + char.ToLowerInvariant(fullPath[0]) + "/" + fullPath[3..].Replace('\\', '/');
        return fullPath;
    }

    /// <summary>
    /// \\wsl.localhost\&lt;известный дистрибутив&gt;\x\y → /x/y (null — не путь этого дистрибутива). Обратное к <see cref="LinuxToWindows"/>.
    /// </summary>
    public static string? ToLinuxPath(string fullPath)
    {
        if (Prefix is not { } prefix) return null;
        var p = NormalizeHost(fullPath);
        if (p.Equals(prefix.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return "/";
        if (!p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = p[prefix.Length..];
        return rest.Contains('/') ? null : "/" + rest.Replace('\\', '/');
    }

    private sealed record RealPathHolder(Func<IReadOnlyList<string>, IReadOnlyList<string>?> Resolve);

    private static readonly AsyncLocal<RealPathHolder?> TestRealPath = new();

    /// <summary>Только для тестов: подменить разрешение ссылок внутри дистрибутива (Linux-пути → realpath или null).</summary>
    internal static IDisposable OverrideRealPath(Func<IReadOnlyList<string>, IReadOnlyList<string>?> resolve)
    {
        var previous = TestRealPath.Value;
        TestRealPath.Value = new RealPathHolder(resolve);
        return new RestoreRealPath(previous);
    }

    /// <summary>Сколько ждать wsl.exe (первый вызов может поднимать ВМ WSL).</summary>
    private static readonly TimeSpan RealPathTimeout = TimeSpan.FromSeconds(20);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Ticks, string? Real)> RealPathCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Разрешить символические ссылки внутри дистрибутива: <c>wsl.exe -d &lt;d&gt; -- realpath -m -z -- &lt;пути&gt;</c>.
    /// Сервер 9P (\\wsl.localhost) может разрешать Linux-ссылки сам — тогда GetFinalPathNameByHandle возвращает исходный путь,
    /// и ссылка «x → /» в репозитории незаметно вела бы за корни. -m: несуществующий хвост допускается (путь будущего файла).
    /// Результат — Linux-пути в том же порядке; null — WSL недоступен, ошибка или таймаут. Кэш — несколько секунд.
    /// </summary>
    public static IReadOnlyList<string>? RealPaths(IReadOnlyList<string> linuxPaths)
    {
        if (linuxPaths.Count == 0) return [];
        if (TestRealPath.Value is { } test) return test.Resolve(linuxPaths);
        if (Distro is not { } distro) return null;
        var now = DateTime.UtcNow.Ticks;
        var ttl = TimeSpan.FromSeconds(5).Ticks;
        var result = new string?[linuxPaths.Count];
        var missing = new List<int>();
        for (var i = 0; i < linuxPaths.Count; i++)
        {
            if (RealPathCache.TryGetValue(distro + "|" + linuxPaths[i], out var hit) && now - hit.Ticks < ttl && hit.Real is not null) result[i] = hit.Real;
            else missing.Add(i);
        }
        if (missing.Count > 0)
        {
            var resolved = RunRealPath(distro, [.. missing.Select(i => linuxPaths[i])]);
            if (resolved is null || resolved.Count != missing.Count) return null;
            if (RealPathCache.Count > 512) RealPathCache.Clear();
            for (var k = 0; k < missing.Count; k++)
            {
                result[missing[k]] = resolved[k];
                RealPathCache[distro + "|" + linuxPaths[missing[k]]] = (now, resolved[k]);
            }
        }
        return [.. result.Select(r => r!)];
    }

    private sealed record FindLinksHolder(Func<IReadOnlyList<string>, IReadOnlyList<string>?> Find);

    private static readonly AsyncLocal<FindLinksHolder?> TestFindLinks = new();

    /// <summary>Только для тестов: подменить поиск символических ссылок среди компонентов пути.</summary>
    internal static IDisposable OverrideFindSymlinks(Func<IReadOnlyList<string>, IReadOnlyList<string>?> find)
    {
        var previous = TestFindLinks.Value;
        TestFindLinks.Value = new FindLinksHolder(find);
        return new RestoreFindLinks(previous);
    }

    /// <summary>
    /// Запасная проверка, когда realpath недоступен: какие из путей — символические ссылки
    /// (<c>wsl.exe -d &lt;d&gt; --exec find &lt;пути&gt; -maxdepth 0 -type l -print0</c>). Несуществующие пути пропускаются.
    /// null — WSL не ответил.
    /// </summary>
    public static IReadOnlyList<string>? FindSymlinks(IReadOnlyList<string> linuxPaths)
    {
        if (linuxPaths.Count == 0) return [];
        if (TestFindLinks.Value is { } test) return test.Find(linuxPaths);
        if (Distro is not { } distro) return null;
        // find не принимает «--» перед путями: путь, начинающийся с «-», был бы выражением. Все пути абсолютные («/…»).
        if (linuxPaths.Any(p => !p.StartsWith('/'))) return null;
        var output = RunWsl(distro, ["find", .. linuxPaths, "-maxdepth", "0", "-type", "l", "-print0"], allowExitCode1: true);
        if (output is null) return null;
        return [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
    }

    private static List<string>? RunRealPath(string distro, IReadOnlyList<string> linuxPaths)
    {
        var output = RunWsl(distro, ["realpath", "-m", "-z", "--", .. linuxPaths], allowExitCode1: false);
        if (output is null) return null;
        var parts = output.Split('\0');
        // Вывод -z: каждый путь завершается NUL, последний элемент после разбиения — пустой.
        if (parts.Length != linuxPaths.Count + 1 || parts[^1].Length != 0) return null;
        var list = parts[..^1].ToList();
        return list.All(p => p.StartsWith('/')) ? list : null;
    }

    /// <summary>
    /// Команда внутри дистрибутива без оболочки (<c>wsl.exe -d &lt;d&gt; --exec …</c>, аргументы списком, wsl.exe из System32),
    /// stdout в UTF-8. null — wsl.exe недоступен, таймаут или код выхода не 0 (или не 1, если он допустим).
    /// </summary>
    private static string? RunWsl(string distro, IReadOnlyList<string> command, bool allowExitCode1)
    {
        var wsl = Path.Combine(Environment.SystemDirectory, "wsl.exe");
        if (!File.Exists(wsl)) return null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(wsl)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new System.Text.UTF8Encoding(false),
                StandardErrorEncoding = new System.Text.UTF8Encoding(false),
            };
            foreach (var a in new[] { "-d", distro, "--exec" }) psi.ArgumentList.Add(a);
            foreach (var a in command) psi.ArgumentList.Add(a);
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return null;
            try { proc.StandardInput.Close(); } catch (IOException) { }
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(RealPathTimeout))
            {
                try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                Core.Logging.Log.Warn("wsl", $"{command[0]} в WSL не ответил вовремя — путь WSL не проверен");
                return null;
            }
            if (!stdout.Wait(TimeSpan.FromSeconds(2))) return null;
            if (proc.ExitCode != 0 && !(allowExitCode1 && proc.ExitCode == 1))
            {
                Core.Logging.Log.Debug("wsl", $"{command[0]} в WSL: код {proc.ExitCode}: {(stderr.Wait(TimeSpan.FromSeconds(1)) ? stderr.Result.Trim() : "")}");
                return null;
            }
            return stdout.Result;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Core.Logging.Log.Debug("wsl", $"{command[0]} в WSL недоступен: {ex.Message}");
            return null;
        }
    }

    private sealed class RestoreFindLinks(FindLinksHolder? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            TestFindLinks.Value = previous;
        }
    }

    private sealed class RestoreRealPath(RealPathHolder? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            TestRealPath.Value = previous;
        }
    }

    private sealed class Restore(Holder? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            TestDistro.Value = previous;
        }
    }
}
