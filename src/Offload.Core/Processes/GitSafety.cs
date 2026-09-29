using System.Text.RegularExpressions;

namespace Offload.Core.Processes;

/// <summary>
/// Защита от внешних команд, которые git запускает по конфигурации репозитория. Конфигурация недоверенная: папку с .git
/// могли прислать архивом. Сами по себе опасны fsmonitor и хуки (гасятся всегда — <see cref="SafeConfig"/>), а также
/// драйверы, подключаемые через .gitattributes: фильтры clean/smudge/process (status, diff с рабочим деревом, add, checkout),
/// textconv и внешний diff, драйверы слияния (merge-tree). Их имена заранее неизвестны, поэтому они сначала читаются
/// из «git config --list --show-scope --name-only» (<see cref="ParseCommandKeys"/>), а затем гасятся пустыми значениями
/// (<see cref="TryBuildNeutralizers"/>): пустую команду git не запускает (проверено на git 2.55).
/// </summary>
public static partial class GitSafety
{
    /// <summary>Переопределения для каждого вызова git: без fsmonitor, хуков, внешнего diff, проверки подписей и пейджера.</summary>
    public static readonly IReadOnlyList<string> SafeConfig =
    [
        "-c", "core.fsmonitor=false", "-c", "core.hooksPath=NUL", "-c", "diff.external=", "-c", "log.showSignature=false",
        "-c", "core.pager=cat",
    ];

    /// <summary>Аргументы git, которые выводят список ключей конфигурации с областями.</summary>
    public static readonly IReadOnlyList<string> ListConfigArgs = ["config", "--list", "--show-scope", "--name-only"];

    /// <summary>
    /// Ключи-команды из вывода <see cref="ListConfigArgs"/> — только области самого репозитория (local, worktree и их
    /// include-файлы). Глобальные и системные (например, фильтр Git LFS) — выбор пользователя, их не трогаем.
    /// </summary>
    public static List<string> ParseCommandKeys(string configList)
    {
        ArgumentNullException.ThrowIfNull(configList);
        var keys = new List<string>();
        foreach (var line in configList.Split('\n'))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            var scope = line[..tab].Trim();
            var key = line[(tab + 1)..].TrimEnd('\r');
            if (scope is "system" or "global" or "command") continue;
            if (CommandConfigKey().IsMatch(key) && !keys.Contains(key, StringComparer.OrdinalIgnoreCase)) keys.Add(key);
        }
        return keys;
    }

    /// <summary>
    /// «-c ключ=» для каждого найденного драйвера. false — имя драйвера с «=» или переводом строки в «-c» не выразить
    /// (git делит по первому «=»): git в таком репозитории запускать нельзя; <paramref name="badKey"/> — этот ключ.
    /// </summary>
    public static bool TryBuildNeutralizers(IEnumerable<string> keys, out string[] args, out string? badKey)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        args = [];
        badKey = null;
        foreach (var key in keys)
        {
            var m = CommandConfigParts().Match(key);
            if (!m.Success) continue;
            var (section, name) = (m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value);
            if (name.Contains('=') || name.Contains('\n') || name.Contains('\r'))
            {
                badKey = key;
                return false;
            }
            if (!seen.Add(section + "\n" + name)) continue;
            string[] props = section switch
            {
                "filter" => ["clean=", "smudge=", "process=", "required=false"],
                "diff" => ["command=", "textconv="],
                _ => ["driver="],
            };
            foreach (var p in props) list.AddRange(["-c", $"{section}.{name}.{p}"]);
        }
        args = [.. list];
        return true;
    }

    [GeneratedRegex(@"^(?:merge\..+\.driver|filter\..+\.(?:clean|smudge|process)|diff\..+\.(?:command|textconv))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandConfigKey();

    /// <summary>Раздел и имя драйвера (имя может содержать точки) из ключа-команды.</summary>
    [GeneratedRegex(@"^(merge|filter|diff)\.(.+)\.(?:driver|clean|smudge|process|command|textconv)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandConfigParts();
}
