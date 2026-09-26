using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Offload.Core;

namespace Offload.Integrations.Editing;

/// <summary>
/// Метка управляемого текста с версией и хэшем: «x-offload: managed v=&lt;версия Offload&gt; h=&lt;16 hex&gt;».
/// Хэш считается по всему тексту, в котором метка приведена к базовой форме «x-offload: managed», а концы строк — к \n.
/// Так по самому файлу видно, менял ли его пользователь (хэш не сходится) и устарел ли он (хэш не равен хэшу текущего шаблона).
/// Метка без хэша (файлы Offload 1.0.0 и раньше) не доказывает, что текст не правили: такой текст считается нашим только
/// при точном совпадении с шаблоном этой версии или с одним из известных шаблонов прежних версий (их хэши зашиты в код),
/// иначе — изменённым пользователем, и автоматически не перезаписывается.
/// </summary>
internal static partial class ManagedContent
{
    public const string Marker = "x-offload: managed";

    [GeneratedRegex(@"x-offload: managed(?: v=(?<v>[^\s:;]+))?(?: h=(?<h>[0-9a-f]{16}))?", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerRegex();

    /// <summary>Хэш текста (16 hex) без учёта версии/хэша в метке и стиля концов строк.</summary>
    public static string Hash(string text)
    {
        var normalized = MarkerRegex().Replace(text.ReplaceLineEndings("\n"), Marker);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    /// <summary>Шаблон с меткой → текст для записи: в первую метку вписываются версия Offload и хэш.</summary>
    public static string Stamp(string template, string? version = null)
    {
        var stamp = $"{Marker} v={version ?? AppInfo.Version} h={Hash(template)}";
        return MarkerRegex().Replace(template, stamp.Replace("$", "$$", StringComparison.Ordinal), 1);
    }

    /// <summary>Версия и хэш из первой метки; null — метки нет.</summary>
    public static (string? Version, string? Hash)? ReadMarker(string text)
    {
        var m = MarkerRegex().Match(text);
        if (!m.Success) return null;
        return (m.Groups["v"].Success ? m.Groups["v"].Value : null, m.Groups["h"].Success ? m.Groups["h"].Value : null);
    }

    /// <summary>
    /// Сравнить установленный текст (null — его нет) с шаблоном текущей версии. <paramref name="shippedHashes"/> — хэши
    /// (<see cref="Hash"/>) шаблонов, которые прежние версии Offload записывали с меткой без хэша.
    /// </summary>
    public static ManagedState Inspect(string? installed, string template, IReadOnlyCollection<string>? shippedHashes = null)
    {
        if (installed is null) return ManagedState.Missing;
        var marker = ReadMarker(installed);
        if (marker is null) return ManagedState.Foreign;
        var actual = Hash(installed);
        var current = Hash(template);
        // Метка без хэша (прежние версии Offload): правки пользователя не отличить по метке — «нашим устаревшим» считается
        // только текст, в точности совпадающий с известным шаблоном, всё остальное — изменённым (не перезаписываем молча).
        if (marker.Value.Hash is null)
        {
            if (actual == current) return ManagedState.Current;
            return shippedHashes is not null && shippedHashes.Contains(actual, StringComparer.Ordinal) ? ManagedState.Outdated : ManagedState.Modified;
        }
        if (!string.Equals(actual, marker.Value.Hash, StringComparison.Ordinal)) return ManagedState.Modified;
        return actual == current ? ManagedState.Current : ManagedState.Outdated;
    }
}
