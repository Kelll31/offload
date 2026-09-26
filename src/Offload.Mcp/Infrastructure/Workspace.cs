using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Offload.Core;
using Offload.Core.Logging;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Корни рабочей области: закреплённые при запуске (HTTP-режим, <c>--root</c> — тогда корни клиента не спрашиваются вовсе),
/// иначе roots/list клиента (если он их объявил), затем CLAUDE_PROJECT_DIR, затем текущая папка.
/// Все корни — в канонической форме (без ссылок и 8.3-имён). Клиент в WSL присылает Linux-пути — они переводятся
/// (<see cref="WslPaths"/>).
/// </summary>
/// <remarks>
/// Ревизии протокола до 2026-07-28: roots/list — обычный запрос сервера к клиенту, ответ кэшируется на сессию до
/// notifications/roots/list_changed. С 2026-07-28 (SEP-2575, SEP-2322) возможности клиента объявляются в каждом запросе,
/// а запросы сервера к клиенту идут через MRTR: SDK сам отвечает клиенту InputRequiredResult и продолжает обработчик после
/// повтора вызова. Поэтому корни запрашиваются в каждом вызове инструмента (через сервер, привязанный к запросу) и не
/// кэшируются: сведения о клиенте нельзя переносить между запросами.
/// </remarks>
internal static class Workspace
{
    public const string ClaudeProjectDirEnv = "CLAUDE_PROJECT_DIR";

    /// <summary>Первая ревизия протокола с корнями через MRTR и возможностями клиента в каждом запросе.</summary>
    internal const string PerRequestProtocolVersion = "2026-07-28";

    public static async Task<IReadOnlyList<string>> GetRootsAsync(McpServer? server, SessionState state, CancellationToken ct)
    {
        // HTTP-режим: корни закреплены аргументами --root при запуске. Корни клиента не спрашиваются вовсе — иначе любой
        // владелец токена мог бы направить инструменты (и local_verify) в произвольную папку.
        if (state.PinnedRoots is { Count: > 0 } pinned) return pinned;
        var perRequest = server is not null && IsPerRequestProtocol(server.NegotiatedProtocolVersion);
        if (!perRequest && state.TryGetCachedRoots(out var cached, out var unavailable))
        {
            if (cached is { Count: > 0 }) return cached;
            if (unavailable) return [Fallback()];
        }
        if (server is not null && CanAskRoots(server))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                // MRTR — круговой путь через клиента (InputRequiredResult → повтор вызова): даём больше времени.
                cts.CancelAfter(perRequest ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(3));
#pragma warning disable MCP9005 // roots/list устарел в 2026-07-28, но Claude Code его поддерживает
                var result = await server.RequestRootsAsync(new ListRootsRequestParams(), cts.Token).ConfigureAwait(false);
#pragma warning restore MCP9005
                var list = ParseRoots(result.Roots?.Select(r => r.Uri));
                if (!perRequest) state.SetClientRoots(list);
                if (list.Count > 0)
                {
                    Log.Debug("mcp", "Корни клиента: " + string.Join("; ", list));
                    return list;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Debug("mcp", $"roots/list недоступен: {ex.GetType().Name}: {ex.Message}");
                if (!perRequest) state.SetClientRoots(null);
            }
        }
        return [Fallback()];
    }

    /// <summary>Ревизия протокола 2026-07-28 или новее (даты ISO-8601 сравниваются как строки).</summary>
    internal static bool IsPerRequestProtocol(string? version) =>
        version is not null && string.CompareOrdinal(version, PerRequestProtocolVersion) >= 0;

    /// <summary>
    /// Клиент объявил roots. Сервер, привязанный к запросу, отдаёт возможности из _meta этого запроса (2026-07-28+) или из
    /// initialize (раньше); без состояния (HTTP stateless) — null, и корни не запрашиваются.
    /// </summary>
    private static bool CanAskRoots(McpServer server)
    {
#pragma warning disable MCP9005
        return server.ClientCapabilities?.Roots is not null;
#pragma warning restore MCP9005
    }

    /// <summary>URI корней клиента → существующие папки в канонической форме, без повторов, в исходном порядке.</summary>
    internal static List<string> ParseRoots(IEnumerable<string?>? uris)
    {
        var list = new List<string>();
        foreach (var uri in uris ?? [])
        {
            var p = UriToLocalPath(uri);
            if (p is null || !Directory.Exists(p)) continue;
            var c = SafeCanonical(p);
            if (IsNetworkPath(c) && !WslPaths.IsAllowedUnc(c)) continue;
            if (!list.Contains(c, StringComparer.OrdinalIgnoreCase)) list.Add(c);
        }
        return list;
    }

    /// <summary>
    /// Корни HTTP-режима из аргументов <c>--root</c>: существующие папки в канонической форме, без повторов. Отклоняются
    /// сетевые пути (кроме разрешённого WSL), корень диска, профиль целиком, системные папки и данные Offload — как корни
    /// для записи. ArgumentException с понятной причиной (английский: печатается в терминал).
    /// </summary>
    internal static List<string> PinRoots(IEnumerable<string> raw)
    {
        var list = new List<string>();
        foreach (var r in raw)
        {
            var value = (r ?? "").Trim().Trim('"');
            if (value.Length == 0) throw new ArgumentException("--root needs a folder path.");
            string full;
            try { full = Path.GetFullPath(value); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new ArgumentException($"--root '{value}' is not a valid path.");
            }
            if (!Directory.Exists(full)) throw new ArgumentException($"--root '{value}' does not exist or is not a folder.");
            var c = SafeCanonical(full);
            if (IsNetworkPath(c) && !WslPaths.IsAllowedUnc(c)) throw new ArgumentException($"--root '{value}' is a network path; use a local project folder.");
            if (IsUnsafeWriteRoot(c) || PathGuard.IsOffloadData(c))
                throw new ArgumentException($"--root '{value}' is not a project folder (drive root, user profile, system or Offload data folder).");
            if (!list.Contains(c, StringComparer.OrdinalIgnoreCase)) list.Add(c);
        }
        return list;
    }

    public static string Fallback()
    {
        var env = Environment.GetEnvironmentVariable(ClaudeProjectDirEnv);
        if (!string.IsNullOrWhiteSpace(env))
        {
            try
            {
                var raw = env.Trim().Trim('"');
                var full = Path.GetFullPath(WslPaths.LinuxToWindows(raw) ?? raw);
                if (Directory.Exists(full)) return SafeCanonical(full);
            }
            catch
            {
                // Некорректное значение — игнорируем.
            }
        }
        return SafeCanonical(Environment.CurrentDirectory);
    }

    /// <summary>
    /// URI корня → локальный путь. file:///C:/x и «C:\x» — как есть; сетевые (file://host/share) — только общий ресурс
    /// известного дистрибутива WSL; Linux-пути клиента в WSL (file:///home/u/proj, file:///mnt/c/x) — через <see cref="WslPaths"/>.
    /// </summary>
    internal static string? UriToLocalPath(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        try
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.IsFile)
            {
                if (u.IsUnc) return AllowedUncOrNull(Path.GetFullPath(WslPaths.NormalizeHost(u.LocalPath)));
                var abs = Uri.UnescapeDataString(u.AbsolutePath);
                var a = abs.TrimStart('/');
                var drive = a.Length >= 2 && char.IsAsciiLetter(a[0]) && a[1] == ':';
                if (WslPaths.IsActive && !drive) return FromLinux(abs);
                return Path.GetFullPath(u.LocalPath);
            }
            if (uri.Length >= 3 && char.IsAsciiLetter(uri[0]) && uri[1] == ':') return Path.GetFullPath(uri);
            if (uri[0] == '/') return FromLinux(uri);
        }
        catch
        {
            // Неразборчивый URI.
        }
        return null;
    }

    /// <summary>Linux-путь клиента в WSL → Windows-путь (null — WSL не активен или путь не переводится).</summary>
    private static string? FromLinux(string linuxPath) =>
        WslPaths.LinuxToWindows(linuxPath) is { } w ? AllowedUncOrNull(Path.GetFullPath(w)) : null;

    /// <summary>/mnt/&lt;буква&gt; внутри WSL → диск Windows; иной сетевой путь, кроме разрешённого WSL, → null.</summary>
    private static string? AllowedUncOrNull(string full)
    {
        full = WslPaths.ToDrivePath(full) ?? full;
        return IsNetworkPath(full) && !WslPaths.IsAllowedUnc(full) ? null : full;
    }

    /// <summary>Корень дистрибутива WSL, /home, домашняя папка Linux целиком, /root, /tmp, /mnt — не папка проекта.</summary>
    private static bool IsUnsafeWslRoot(string root)
    {
        var d = WslPaths.Distro;
        if (d is null) return true;
        var rel = root.Length > WslPaths.UncHost.Length + d.Length ? root[(WslPaths.UncHost.Length + d.Length)..].Trim('\\') : "";
        var parts = rel.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;
        if (parts.Length == 1) return parts[0] is "home" or "root" or "tmp" or "mnt" or "var" or "usr" or "etc" or "opt";
        return parts.Length == 2 && parts[0] == "home";
    }

    private static bool IsNetworkPath(string p) => p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal);

    private static string SafeCanonical(string path)
    {
        try { return PathGuard.Canonicalize(Path.GetFullPath(path)); }
        catch { return PathGuard.TrimTrailingSeparator(Path.GetFullPath(path)); }
    }

    /// <summary>
    /// Корень, в который нельзя писать: корень диска, профиль пользователя целиком, Windows, Program Files, AppData.
    /// Так бывает, когда IDE запускает сервер не из папки проекта (например, Claude Desktop из System32).
    /// </summary>
    public static bool IsUnsafeWriteRoot(string root)
    {
        var r = PathGuard.TrimTrailingSeparator(root);
        if (Path.GetPathRoot(r) is { } drive && PathGuard.TrimTrailingSeparator(drive).Equals(r, StringComparison.OrdinalIgnoreCase)) return true;
        if (IsNetworkPath(r)) return !WslPaths.IsAllowedUnc(r) || IsUnsafeWslRoot(r);
        foreach (var exact in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     Path.GetTempPath(),
                 })
        {
            if (!string.IsNullOrEmpty(exact) && PathGuard.TrimTrailingSeparator(exact).Equals(r, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return IsForbiddenWriteLocation(r);
    }

    /// <summary>Системные места, куда инструменты не пишут никогда (автозагрузка, Roaming, Windows, Program Files, установка Offload).</summary>
    public static bool IsForbiddenWriteLocation(string fullPath)
    {
        foreach (var dir in ForbiddenWriteDirs.Value)
        {
            if (PathGuard.IsInside(fullPath, dir)) return true;
        }
        return false;
    }

    private static readonly Lazy<string[]> ForbiddenWriteDirs = new(() =>
    {
        var list = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try { list.Add(PathGuard.TrimTrailingSeparator(Path.GetFullPath(p))); } catch { }
        }
        Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.Startup));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
        {
            Add(Path.Combine(local, "Microsoft"));
            Add(Path.Combine(local, "Packages"));
            Add(Path.Combine(local, "Programs"));
        }
        Add(AppContext.BaseDirectory);
        return [.. list];
    });

    /// <summary>Корни, пригодные для записи. Пусто → понятная ошибка.</summary>
    public static IReadOnlyList<string> WriteRoots(IReadOnlyList<string> roots)
    {
        var ok = roots.Where(r => !IsUnsafeWriteRoot(r) && !PathGuard.IsOffloadData(r)).ToList();
        if (ok.Count == 0)
            throw new ToolException(
                $"No project folder is known for writing: the workspace root is '{(roots.Count > 0 ? roots[0] : "?")}', " +
                "which is not a project directory. Start the IDE/agent in the project folder (or pass paths inside it).");
        return ok;
    }

    /// <summary>Папка данных Offload (для справки в сообщениях).</summary>
    public static string DataDir => AppPaths.DataDir;
}
