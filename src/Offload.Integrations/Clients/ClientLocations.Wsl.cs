using System.Text.RegularExpressions;
using Offload.Core.Logging;
using Offload.Core.Processes;

namespace Offload.Integrations.Clients;

/// <summary>
/// WSL: список дистрибутивов (wsl.exe -l -q), домашняя папка пользователя по умолчанию в дистрибутиве и пути к его файлам
/// через общий ресурс \\wsl.localhost. В песочнице тестов wsl.exe не запускается: дистрибутивы и их домашние папки задаёт
/// песочница, а общий ресурс подменяется папкой <see cref="IntegrationEnvironment.Sandbox.WslRoot"/>.
/// </summary>
internal static partial class ClientLocations
{
    /// <summary>Общий ресурс WSL (в песочнице — папка внутри неё).</summary>
    public static string WslRoot => IntegrationEnvironment.Current?.WslRoot ?? @"\\wsl.localhost";

    /// <summary>Служебные дистрибутивы Docker Desktop: без пользователей и Claude Code.</summary>
    private static readonly string[] ServiceDistros = ["docker-desktop", "docker-desktop-data", "rancher-desktop", "rancher-desktop-data"];

    private static readonly TimeSpan WslCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly Lock WslLock = new();
    private static (DateTime At, IReadOnlyList<string> Names)? _wslDistros;
    private static readonly Dictionary<string, (DateTime At, string? Home)> WslHomes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Установленные дистрибутивы WSL (без служебных). Результат wsl.exe кэшируется на 5 минут.</summary>
    public static IReadOnlyList<string> WslDistros()
    {
        if (IntegrationEnvironment.Current is { } sb)
            return sb.WslDistros.Keys.Where(IsValidWslDistroName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        lock (WslLock)
        {
            if (_wslDistros is { } c && DateTime.UtcNow - c.At < WslCacheTtl) return c.Names;
        }
        IReadOnlyList<string> names = [];
        if (FindWslExe() is { } wsl)
        {
            // WSL_UTF8=1 — вывод wsl.exe в UTF-8; старые версии его не знают и пишут UTF-16LE (ParseWslList понимает оба).
            var r = RunWsl(wsl, ["-l", "-q"], TimeSpan.FromSeconds(10));
            if (r is { Success: true }) names = ParseWslList(r.StdOut);
            else if (r is not null) Log.Debug("Integrations", $"wsl -l -q: код {r.ExitCode}");
        }
        lock (WslLock) _wslDistros = (DateTime.UtcNow, names);
        return names;
    }

    /// <summary>
    /// Разбор вывода «wsl.exe -l -q»: UTF-16LE, прочитанный как UTF-8 (нулевые байты между символами), или UTF-8;
    /// строки — имена дистрибутивов. Служебные и некорректные имена отбрасываются.
    /// </summary>
    internal static IReadOnlyList<string> ParseWslList(string output)
    {
        // BOM UTF-16 (FF FE), \u043F\u0440\u043E\u0447\u0438\u0442\u0430\u043D\u043D\u044B\u0439 \u043A\u0430\u043A UTF-8, \u043F\u0440\u0435\u0432\u0440\u0430\u0449\u0430\u0435\u0442\u0441\u044F \u0432 \u0441\u0438\u043C\u0432\u043E\u043B\u044B \u0437\u0430\u043C\u0435\u043D\u044B U+FFFD \u2014 \u0443\u0431\u0438\u0440\u0430\u0435\u043C \u0438 \u0438\u0445.
        var text = output.Replace("\0", "", StringComparison.Ordinal).Replace("\uFEFF", "", StringComparison.Ordinal)
            .Replace("\uFFFD", "", StringComparison.Ordinal);
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsValidWslDistroName)
            .Where(n => !ServiceDistros.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Имя дистрибутива: буквы, цифры, «.», «_», «-», до 64 символов (то же правило, что у MCP-сервера).</summary>
    public static bool IsValidWslDistroName(string? name) => name is not null && WslDistroName().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex WslDistroName();

    /// <summary>Сколько доверять списку запущенных дистрибутивов (запрос дешёвый, но WslHome вызывается много раз за обновление).</summary>
    private static readonly TimeSpan WslRunningTtl = TimeSpan.FromSeconds(10);
    private static (DateTime At, IReadOnlySet<string> Names)? _wslRunning;

    /// <summary>Явное действие пользователя: в этом потоке выполнения дистрибутивы WSL можно запускать (<see cref="WslProbe.AllowStart"/>).</summary>
    private static readonly AsyncLocal<bool> WslStartAllowed = new();

    /// <summary>Только для тестов: дистрибутивы песочницы, которые считаются остановленными (в текущем асинхронном контексте).</summary>
    private static readonly AsyncLocal<IReadOnlySet<string>?> SandboxStopped = new();

    private static int _sandboxWslStarts;

    /// <summary>Сколько раз песочница «запускала» остановленный дистрибутив (для тестов: фоновые пути не должны его запускать).</summary>
    internal static int SandboxWslStarts => Volatile.Read(ref _sandboxWslStarts);

    /// <summary>
    /// Домашняя папка пользователя по умолчанию в дистрибутиве (Linux-путь, например /home/user); null — неизвестна.
    /// Дистрибутив не запускается ради этого: и «wsl -d &lt;d&gt; …», и обращение к \\wsl.localhost\&lt;d&gt; поднимают его
    /// виртуальную машину, а страница «Интеграции» обновляется каждые несколько минут, удаление программы тоже опрашивает WSL.
    /// Поэтому для остановленного дистрибутива — null (Claude Code в нём «не найден», файлы не читаются), кроме явного действия
    /// пользователя (<see cref="WslProbe.AllowStart"/>: «Определить», «Подключить»). Для запущенного — «wsl -d &lt;d&gt; -e sh -c
    /// 'echo $HOME'» (дистрибутив уже работает, ничего не стартует); ответ кэшируется на 5 минут.
    /// </summary>
    public static string? WslHome(string distro)
    {
        if (!IsValidWslDistroName(distro)) return null;
        var mayStart = WslStartAllowed.Value;
        if (IntegrationEnvironment.Current is { } sb)
        {
            if (!sb.WslDistros.TryGetValue(distro, out var h) || !IsSafeLinuxHome(h)) return null;
            if (IsWslDistroRunning(distro)) return h;
            if (!mayStart) return null;
            Interlocked.Increment(ref _sandboxWslStarts);
            return h;
        }
        if (!mayStart && !IsWslDistroRunning(distro)) return null;
        lock (WslLock)
        {
            if (WslHomes.TryGetValue(distro, out var c) && DateTime.UtcNow - c.At < WslCacheTtl) return c.Home;
        }
        string? home = null;
        if (FindWslExe() is { } wsl)
        {
            var r = RunWsl(wsl, ["-d", distro, "-e", "sh", "-c", "echo $HOME"], TimeSpan.FromSeconds(30));
            var line = r is { Success: true } ? r.StdOut.Replace("\0", "", StringComparison.Ordinal).Trim() : null;
            if (IsSafeLinuxHome(line)) home = line;
            else if (r is not null) Log.Debug("Integrations", $"WSL {distro}: домашняя папка не определена (код {r.ExitCode})");
        }
        lock (WslLock)
        {
            WslHomes[distro] = (DateTime.UtcNow, home);
            // Запущенный по действию пользователя дистрибутив теперь работает: список запущенных устарел.
            if (mayStart) _wslRunning = null;
        }
        return home;
    }

    /// <summary>
    /// Дистрибутив запущен («wsl -l --running -q» — сам ничего не запускает; кэш 10 секунд). В песочнице — все её дистрибутивы,
    /// кроме заданных <see cref="SimulateStoppedWslDistros"/>.
    /// </summary>
    public static bool IsWslDistroRunning(string distro)
    {
        if (!IsValidWslDistroName(distro)) return false;
        if (IntegrationEnvironment.Current is { } sb)
            return sb.WslDistros.ContainsKey(distro) && SandboxStopped.Value?.Contains(distro) != true;
        lock (WslLock)
        {
            if (_wslRunning is { } c && DateTime.UtcNow - c.At < WslRunningTtl) return c.Names.Contains(distro);
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (FindWslExe() is { } wsl)
        {
            // Нет запущенных — wsl.exe отвечает текстом и ненулевым кодом: тогда список пуст.
            var r = RunWsl(wsl, ["-l", "--running", "-q"], TimeSpan.FromSeconds(10));
            if (r is { Success: true }) names = ParseWslList(r.StdOut).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        lock (WslLock) _wslRunning = (DateTime.UtcNow, names);
        return names.Contains(distro);
    }

    /// <summary>Разрешить запуск дистрибутивов в текущем потоке выполнения (до Dispose).</summary>
    internal static IDisposable AllowWslStart()
    {
        var previous = WslStartAllowed.Value;
        WslStartAllowed.Value = true;
        return new Restore(() => WslStartAllowed.Value = previous);
    }

    /// <summary>Только для тестов: считать дистрибутивы песочницы остановленными (до Dispose, в текущем асинхронном контексте).</summary>
    internal static IDisposable SimulateStoppedWslDistros(params string[] distros)
    {
        var previous = SandboxStopped.Value;
        SandboxStopped.Value = new HashSet<string>(distros, StringComparer.OrdinalIgnoreCase);
        return new Restore(() => SandboxStopped.Value = previous);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    /// <summary>Абсолютный Linux-путь без «..», управляющих символов и обратных косых черт.</summary>
    internal static bool IsSafeLinuxHome(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 512 || path[0] != '/' || path.Contains('\\') || path.Contains(':')) return false;
        if (path.Any(char.IsControl)) return false;
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries).All(s => s is not ("." or ".."));
    }

    /// <summary>Linux-путь дистрибутива → путь Windows через общий ресурс: /home/u/.claude.json → \\wsl.localhost\Ubuntu\home\u\.claude.json.</summary>
    public static string WslPath(string distro, string linuxPath) =>
        Path.Combine([WslRoot, distro, .. linuxPath.Split('/', StringSplitOptions.RemoveEmptyEntries)]);

    /// <summary>
    /// Путь Windows к exe → путь внутри WSL (interop): C:\Program Files\Offload\Offload.exe → /mnt/c/Program Files/Offload/Offload.exe.
    /// Сетевые и относительные пути — null (из WSL такой exe не запустить). Корень монтирования — стандартный /mnt.
    /// </summary>
    public static string? WslInteropPath(string windowsPath)
    {
        if (string.IsNullOrWhiteSpace(windowsPath)) return null;
        var p = windowsPath.Trim();
        if (p.Length < 3 || !char.IsAsciiLetter(p[0]) || p[1] != ':' || p[2] is not ('\\' or '/')) return null;
        return "/mnt/" + char.ToLowerInvariant(p[0]) + "/" + p[3..].Replace('\\', '/').TrimStart('/');
    }

    private static string? FindWslExe()
    {
        if (!IntegrationEnvironment.CliAllowed || IntegrationEnvironment.IsSandboxed || !OperatingSystem.IsWindows()) return null;
        var sys = Path.Combine(Environment.SystemDirectory, "wsl.exe");
        return File.Exists(sys) ? sys : null;
    }

    private static ProcessResult? RunWsl(string exe, IReadOnlyList<string> args, TimeSpan timeout)
    {
        try
        {
            return ProcessRunner.RunAsync(exe, args,
                    workingDirectory: Environment.SystemDirectory,
                    environment: new Dictionary<string, string?> { ["WSL_UTF8"] = "1" },
                    timeout: timeout)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            Log.Debug("Integrations", $"wsl.exe: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// WSL для интерфейса: фоновые опросы (обновление страницы «Интеграции», удаление программы) остановленные дистрибутивы
/// не запускают — Claude Code в них выглядит «не найденным». Запуск — только по явному действию пользователя:
/// кнопка «Определить» (<see cref="Detect"/>) или действие над строкой WSL внутри <see cref="AllowStart"/>.
/// Вызывать не из потока интерфейса (wsl.exe).
/// </summary>
public static class WslProbe
{
    /// <summary>Дистрибутив запущен (сам ничего не запускает).</summary>
    public static bool IsRunning(string distro) => ClientLocations.IsWslDistroRunning(distro);

    /// <summary>Установленные, но остановленные дистрибутивы — для подсказки «запустите дистрибутив или нажмите «Определить»».</summary>
    public static IReadOnlyList<string> Stopped() =>
        [.. ClientLocations.WslDistros().Where(d => !ClientLocations.IsWslDistroRunning(d))];

    /// <summary>
    /// Явное действие пользователя: до Dispose в текущем потоке выполнения (в том числе в await-продолжениях) можно запускать
    /// дистрибутивы WSL — например, на время «Подключить»/«Отключить» для строки Claude Code (WSL).
    /// </summary>
    public static IDisposable AllowStart() => ClientLocations.AllowWslStart();

    /// <summary>
    /// «Определить»: запустить каждый установленный дистрибутив и узнать домашнюю папку пользователя по умолчанию
    /// (кэш — 5 минут; пока дистрибутив работает, строки WSL на странице обновляются без запуска). Возвращает дистрибутив → папка или null.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Detect()
    {
        using var _ = AllowStart();
        return ClientLocations.WslDistros().ToDictionary(d => d, ClientLocations.WslHome, StringComparer.OrdinalIgnoreCase);
    }
}
