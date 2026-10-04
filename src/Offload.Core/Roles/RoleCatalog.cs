using System.Text;
using System.Text.RegularExpressions;

namespace Offload.Core.Roles;

/// <summary>Роль локальной модели: именованная персона (ревьюер, тестировщик…) с промптом; может наследоваться от другой роли.</summary>
/// <param name="Extends">Имя родительской роли; null — корневая.</param>
/// <param name="Presets">Идентификаторы пресетов правил (<see cref="PresetLibrary"/>), которые добавляются к промпту роли.</param>
/// <param name="Source">Откуда роль: «builtin» (встроена), «user» (папка данных) или «project» (.offload/roles проекта).</param>
public sealed record AgentRole(string Name, string Description, string? Extends, IReadOnlyList<string> Presets, string Prompt, string Source);

/// <summary>Роль с развёрнутым наследованием: итоговый промпт (родители первыми, затем пресеты, затем сама роль).</summary>
/// <param name="Chain">Имена от корневого родителя до самой роли.</param>
public sealed record ResolvedRole(string Name, string Description, string Prompt, IReadOnlyList<string> Chain, string Source);

/// <summary>Ошибка работы с ролями (неизвестная роль, цикл, неверное имя). Текст — по-английски: его читает модель.</summary>
public sealed class RoleException(string message) : Exception(message);

/// <summary>Пресеты правил — встроенные ресурсы сборки (Offload.Core.Presets.*.md).</summary>
public static class PresetLibrary
{
    private const string Prefix = "Offload.Core.Presets.";

    /// <summary>Идентификаторы пресетов по возрастанию.</summary>
    public static IReadOnlyList<string> Ids() =>
        typeof(AppPaths).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Select(n => n[Prefix.Length..^3])
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>Текст пресета (LF, без краевых пробелов) или null, если такого нет.</summary>
    public static string? Get(string id)
    {
        using var s = typeof(AppPaths).Assembly.GetManifestResourceStream(Prefix + id + ".md");
        if (s is null) return null;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd().Replace("\r\n", "\n").Trim();
    }
}

/// <summary>
/// Каталог ролей: встроенные + пользовательские (<c>%OFFLOAD_HOME%\roles</c>) + проектные (<c>&lt;проект&gt;\.offload\roles</c>).
/// Пользовательская роль заменяет встроенную; проектные роли (файлы из репозитория — недоверенный ввод) только добавляют новые имена
/// и никогда не подменяют встроенные и пользовательские. Файл роли — markdown с блоком
/// <c>---</c> (name, description, extends, presets: [a, b]); остальное — промпт.
/// </summary>
public static partial class RoleCatalog
{
    public const int MaxDepth = 5;
    public const int MaxPromptChars = 4000;
    public const int MaxResolvedChars = 7000;
    public const int MaxDescriptionChars = 200;

    /// <summary>Файл роли больше этого размера пропускается (защита от гигантских файлов в чужом репозитории).</summary>
    public const int MaxFileBytes = 32 * 1024;

    private const string BuiltinPrefix = "Offload.Core.Roles.";

    /// <summary>Подпапка проекта с его ролями.</summary>
    public static string ProjectRolesDir(string workspaceRoot) => Path.Combine(workspaceRoot, ".offload", "roles");

    /// <summary>Папка пользовательских ролей.</summary>
    public static string UserRolesDir => Path.Combine(AppPaths.DataDir, "roles");

    /// <summary>Все роли с учётом приоритета источников, по имени.</summary>
    public static IReadOnlyList<AgentRole> All(string? workspaceRoot)
    {
        var byName = new Dictionary<string, AgentRole>(StringComparer.Ordinal);
        foreach (var r in Builtin()) byName[r.Name] = r;
        foreach (var r in FromDir(UserRolesDir, "user")) byName[r.Name] = r;
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            // Проектные роли — файлы репозитория: только новые имена, подмена встроенной или пользовательской роли невозможна.
            foreach (var r in FromDir(ProjectRolesDir(workspaceRoot), "project"))
                byName.TryAdd(r.Name, r);
        }
        return byName.Values.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
    }

    public static AgentRole? Find(string name, string? workspaceRoot)
    {
        var n = (name ?? "").Trim().ToLowerInvariant();
        return All(workspaceRoot).FirstOrDefault(r => r.Name == n);
    }

    /// <summary>Роль с развёрнутым наследованием. Неизвестная роль/родитель/пресет, цикл, глубина &gt; 5, слишком длинный итог — <see cref="RoleException"/>.</summary>
    public static ResolvedRole Resolve(string name, string? workspaceRoot)
    {
        var roles = All(workspaceRoot).ToDictionary(r => r.Name, StringComparer.Ordinal);
        var key = (name ?? "").Trim().ToLowerInvariant();
        if (!roles.TryGetValue(key, out var role))
            throw new RoleException($"Unknown role '{name}'. Available: {string.Join(", ", roles.Keys)}.");
        return ResolveCore(roles, role);
    }

    /// <summary>Развёртывание наследования по заданному набору ролей (общая часть <see cref="Resolve"/> и проверки перед <see cref="Save"/>).</summary>
    private static ResolvedRole ResolveCore(Dictionary<string, AgentRole> roles, AgentRole role)
    {
        var chain = new List<AgentRole>();
        for (var cur = role; ; )
        {
            if (chain.Any(c => c.Name == cur.Name)) throw new RoleException($"Role inheritance cycle: {string.Join(" -> ", chain.Select(c => c.Name).Append(cur.Name))}.");
            chain.Add(cur);
            if (chain.Count > MaxDepth + 1) throw new RoleException($"Role '{role.Name}' inherits too deep (max {MaxDepth} levels).");
            if (cur.Extends is null) break;
            if (!roles.TryGetValue(cur.Extends, out var parent))
                throw new RoleException($"Role '{cur.Name}' extends unknown role '{cur.Extends}'.");
            cur = parent;
        }
        chain.Reverse();

        var parts = new List<string>();
        foreach (var r in chain)
        {
            if (r.Prompt.Length > 0) parts.Add(r.Prompt);
            foreach (var preset in r.Presets)
            {
                var text = PresetLibrary.Get(preset) ?? throw new RoleException($"Role '{r.Name}' uses unknown preset '{preset}'.");
                parts.Add($"Rules ({preset}):\n{text}");
            }
        }
        var prompt = string.Join("\n\n", parts);
        if (prompt.Length > MaxResolvedChars)
            throw new RoleException($"Role '{role.Name}' resolves to {prompt.Length} characters (max {MaxResolvedChars}); shorten its prompt or presets.");
        return new ResolvedRole(role.Name, role.Description, prompt, chain.Select(c => c.Name).ToList(), role.Source);
    }

    /// <summary>Разбор файла роли; без блока свойств весь текст — промпт, имя берётся из имени файла.</summary>
    public static AgentRole Parse(string markdown, string source, string fallbackName)
    {
        var text = (markdown ?? "").Replace("\r\n", "\n").TrimStart('﻿');
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body = text;
        if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end > 0)
            {
                foreach (var line in (end >= 4 ? text[4..end] : "").Split('\n'))
                {
                    var i = line.IndexOf(':');
                    if (i > 0) meta[line[..i].Trim()] = line[(i + 1)..].Trim();
                }
                var rest = text[(end + 4)..];
                body = rest.StartsWith('\n') ? rest[1..] : rest;
            }
        }
        var name = meta.TryGetValue("name", out var n) && n.Length > 0 ? n : fallbackName;
        var extends = meta.TryGetValue("extends", out var e) && e.Length > 0 ? e.ToLowerInvariant() : null;
        var presets = meta.TryGetValue("presets", out var p) ? ParseList(p) : [];
        var description = meta.GetValueOrDefault("description", "").Replace('\n', ' ').Trim();
        if (description.Length > MaxDescriptionChars) description = description[..MaxDescriptionChars];
        return new AgentRole(name.Trim().ToLowerInvariant(), description, extends, presets, body.Trim(), source);
    }

    private static List<string> ParseList(string value) =>
        value.Trim().Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim('"', '\'')).Where(x => x.Length > 0).ToList();

    /// <summary>Допустимое имя роли: строчные латиница/цифры/дефис, 2–40 символов (без «..», разделителей путей).</summary>
    public static string ValidateName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length is < 2 or > 40 || !NameRegex().IsMatch(n))
            throw new RoleException("Role name must be 2-40 characters: lowercase letters, digits and single '-' (e.g. 'api-reviewer').");
        return n;
    }

    /// <summary>
    /// Создать или заменить проектную роль <c>.offload/roles/&lt;имя&gt;.md</c>. Родитель и пресеты должны существовать,
    /// наследование не должно образовать цикл. Возвращает путь файла.
    /// </summary>
    public static string Save(string workspaceRoot, string name, string description, string? extends, IReadOnlyList<string>? presets, string prompt,
        bool overwrite = false)
    {
        var n = ValidateName(name);
        if (Builtin().Any(r => r.Name == n) || FromDir(UserRolesDir, "user").Any(r => r.Name == n))
            throw new RoleException($"'{n}' is a built-in or user role and cannot be replaced by a project role. Pick another name, e.g. '{n}-project', or use extends={n}.");
        var body = (prompt ?? "").Replace("\r\n", "\n").Trim();
        if (body.Length is 0 or > MaxPromptChars) throw new RoleException($"Role prompt must be 1-{MaxPromptChars} characters.");
        var desc = (description ?? "").Replace('\n', ' ').Trim();
        if (desc.Length > 200) throw new RoleException("Role description must be at most 200 characters.");
        var parent = string.IsNullOrWhiteSpace(extends) ? null : extends.Trim().ToLowerInvariant();
        var known = PresetLibrary.Ids();
        var list = (presets ?? []).Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        foreach (var p in list)
            if (!known.Contains(p)) throw new RoleException($"Unknown preset '{p}'. Available: {string.Join(", ", known)}.");

        // Цепочка родителей: нет такой роли — ошибка, встретили саму себя — цикл.
        var roles = All(workspaceRoot).ToDictionary(r => r.Name, StringComparer.Ordinal);
        var seen = new HashSet<string> { n };
        for (var cur = parent; cur is not null; )
        {
            if (!roles.TryGetValue(cur, out var r)) throw new RoleException($"Parent role '{cur}' does not exist.");
            if (!seen.Add(cur)) throw new RoleException($"Role inheritance cycle through '{cur}'.");
            cur = r.Extends;
        }

        // Длина итогового промпта, глубина и цикл — до записи: файл, ломающий каждый вызов с этой ролью, не должен появиться.
        var candidate = new AgentRole(n, desc, parent, list, body, "project");
        ResolveCore(new Dictionary<string, AgentRole>(roles, StringComparer.Ordinal) { [n] = candidate }, candidate);

        var sb = new StringBuilder("---\n");
        sb.Append("name: ").Append(n).Append('\n');
        if (desc.Length > 0) sb.Append("description: ").Append(desc).Append('\n');
        if (parent is not null) sb.Append("extends: ").Append(parent).Append('\n');
        if (list.Count > 0) sb.Append("presets: [").Append(string.Join(", ", list)).Append("]\n");
        sb.Append("---\n").Append(body).Append('\n');

        var dir = ProjectRolesDir(workspaceRoot);
        RejectLinks(workspaceRoot, dir);
        Directory.CreateDirectory(dir);
        RejectLinks(workspaceRoot, dir);
        var path = Path.Combine(dir, n + ".md");
        if (IsLink(path)) throw new RoleException($"'{n}.md' is a symbolic link; refusing to write through it.");
        if (File.Exists(path) && !overwrite) throw new RoleException($"Project role '{n}' already exists; pass overwrite=true to replace it.");

        // Временный файл со случайным именем и CreateNew: ссылка, подложенная под предсказуемое имя, не сработает.
        var tmp = Path.Combine(dir, $".{n}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
                fs.Write(bytes, 0, bytes.Length);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
        return path;
    }

    /// <summary>Удалить проектную роль (встроенные и пользовательские не затрагиваются). false — такой проектной роли нет.</summary>
    public static bool Delete(string workspaceRoot, string name)
    {
        var n = ValidateName(name);
        var dir = ProjectRolesDir(workspaceRoot);
        if (!Directory.Exists(dir)) return false;
        RejectLinks(workspaceRoot, dir);
        foreach (var file in Directory.EnumerateFiles(dir, "*.md").OrderBy(f => f, StringComparer.Ordinal))
        {
            // Роль определяется полем name, а не только именем файла: «bar» в foo.md тоже удаляется. Ссылки не трогаем.
            if (IsLink(file)) continue;
            var stem = Path.GetFileNameWithoutExtension(file);
            var matches = stem == n;
            if (!matches && new FileInfo(file).Length <= MaxFileBytes)
            {
                try { matches = Parse(File.ReadAllText(file), "project", stem).Name == n; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            }
            if (!matches) continue;
            File.Delete(file);
            return true;
        }
        return false;
    }

    private static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Папки <c>.offload</c> и <c>.offload/roles</c> не должны быть ссылками (junction/symlink): иначе запись уйдёт за пределы проекта.</summary>
    private static void RejectLinks(string workspaceRoot, string rolesDir)
    {
        foreach (var d in new[] { Path.Combine(workspaceRoot, ".offload"), rolesDir })
            if (Directory.Exists(d) && IsLink(d))
                throw new RoleException($"'{Path.GetFileName(d)}' is a symbolic link or junction; refusing to write roles through it.");
    }

    private static IEnumerable<AgentRole> Builtin()
    {
        var asm = typeof(AppPaths).Assembly;
        foreach (var res in asm.GetManifestResourceNames()
                     .Where(n => n.StartsWith(BuiltinPrefix, StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var s = asm.GetManifestResourceStream(res);
            if (s is null) continue;
            using var r = new StreamReader(s, Encoding.UTF8);
            yield return Parse(r.ReadToEnd(), "builtin", res[BuiltinPrefix.Length..^3]);
        }
    }

    private static IEnumerable<AgentRole> FromDir(string dir, string source)
    {
        if (!Directory.Exists(dir) || IsLink(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.md").OrderBy(f => f, StringComparer.Ordinal))
        {
            AgentRole role;
            try
            {
                // Ссылки (могут вести на чужие файлы) и гигантские файлы пропускаются.
                if (IsLink(file) || new FileInfo(file).Length > MaxFileBytes) continue;
                role = Parse(File.ReadAllText(file), source, Path.GetFileNameWithoutExtension(file));
                ValidateName(role.Name);
                if (role.Extends is not null) ValidateName(role.Extends);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RoleException)
            {
                continue; // повреждённый или чужой файл роли не ломает каталог
            }
            yield return role;
        }
    }

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();
}
