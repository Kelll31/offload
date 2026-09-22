using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Offload.Core;
using Offload.Core.Logging;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Корни рабочей области: roots/list клиента (если он их объявил), затем CLAUDE_PROJECT_DIR, затем текущая папка.
/// Все корни — в канонической форме (без ссылок и 8.3-имён).
/// </summary>
internal static class Workspace
{
    public const string ClaudeProjectDirEnv = "CLAUDE_PROJECT_DIR";

    public static async Task<IReadOnlyList<string>> GetRootsAsync(McpServer? server, SessionState state, CancellationToken ct)
    {
        if (state.TryGetCachedRoots(out var cached, out var unavailable) && cached is { Count: > 0 }) return cached;
        if (!unavailable && server is not null && CanAskRoots(server))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
#pragma warning disable MCP9005 // roots/list устарел в 2026-07-28, но Claude Code его поддерживает
                var result = await server.RequestRootsAsync(new ListRootsRequestParams(), cts.Token).ConfigureAwait(false);
#pragma warning restore MCP9005
                var list = new List<string>();
                foreach (var r in result.Roots ?? [])
                {
                    var p = UriToLocalPath(r.Uri);
                    if (p is null || !Directory.Exists(p)) continue;
                    var c = SafeCanonical(p);
                    if (!list.Contains(c, StringComparer.OrdinalIgnoreCase)) list.Add(c);
                }
                state.SetClientRoots(list);
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
                state.SetClientRoots(null);
            }
        }
        return [Fallback()];
    }

    private static bool CanAskRoots(McpServer server)
    {
#pragma warning disable MCP9005
        if (server.ClientCapabilities?.Roots is null) return false;
#pragma warning restore MCP9005
        // В ревизии 2026-07-28 roots/list идёт через MRTR — не усложняем, берём переменные окружения.
        var v = server.NegotiatedProtocolVersion;
        return v is null || string.CompareOrdinal(v, "2026-07-28") < 0;
    }

    public static string Fallback()
    {
        var env = Environment.GetEnvironmentVariable(ClaudeProjectDirEnv);
        if (!string.IsNullOrWhiteSpace(env))
        {
            try
            {
                var full = Path.GetFullPath(env.Trim().Trim('"'));
                if (Directory.Exists(full)) return SafeCanonical(full);
            }
            catch
            {
                // Некорректное значение — игнорируем.
            }
        }
        return SafeCanonical(Environment.CurrentDirectory);
    }

    internal static string? UriToLocalPath(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        try
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.IsFile && !u.IsUnc) return Path.GetFullPath(u.LocalPath);
            if (uri.Length >= 3 && char.IsAsciiLetter(uri[0]) && uri[1] == ':') return Path.GetFullPath(uri);
        }
        catch
        {
            // Неразборчивый URI.
        }
        return null;
    }

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
