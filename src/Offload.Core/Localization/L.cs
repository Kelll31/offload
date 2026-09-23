using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Offload.Core.Localization;

/// <summary>
/// Локализация интерфейса. Исходный текст в коде — русский и служит ключом: <c>L.T("Сохранить")</c>, <c>L.F("Модель {0} загружена", name)</c>.
/// Переводы на английский — встроенные ресурсы <c>Localization/en/*.json</c> («русский текст» → «English text»);
/// нет перевода — показывается русский. Журналы (Log.*) не переводятся.
/// </summary>
public static class L
{
    public const string Russian = "ru";
    public const string English = "en";
    public const string System = "system";

    private static Dictionary<string, string>? _english;
    private static readonly object Gate = new();
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Действующий язык интерфейса: ru или en.</summary>
    public static string Language { get; private set; } = Russian;

    public static bool IsEnglish => Language == English;

    /// <summary>Культура для чисел и дат в интерфейсе.</summary>
    public static CultureInfo Culture => IsEnglish ? En : Ru;

    /// <summary>Применить настройку языка (ru / en / system — по языку Windows).</summary>
    public static void Initialize(string? setting) => Language = Resolve(setting);

    public static string Resolve(string? setting) => setting?.Trim().ToLowerInvariant() switch
    {
        English => English,
        Russian => Russian,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? Russian : English,
    };

    /// <summary>Перевод строки (русский текст — ключ).</summary>
    public static string T(string ru)
    {
        if (!IsEnglish || string.IsNullOrEmpty(ru)) return ru;
        return EnglishTable().TryGetValue(ru, out var en) ? en : ru;
    }

    /// <summary>Перевод строки формата с {0}, {1:0.0}… и подстановка аргументов в культуре интерфейса.</summary>
    public static string F(string ru, params object?[] args)
    {
        var format = T(ru);
        try
        {
            return string.Format(Culture, format, args);
        }
        catch (FormatException)
        {
            // Испорченный перевод не должен ронять интерфейс.
            return string.Format(Culture, ru, args);
        }
    }

    /// <summary>
    /// Число со словом в нужной форме: Plural(2, "строка", "строки", "строк") → «2 строки» / «2 lines».
    /// Для английского перевод ищется по ключу «одна|несколько|много» и должен иметь вид «line|lines».
    /// </summary>
    public static string Plural(long n, string one, string few, string many)
    {
        var number = n.ToString("N0", Culture);
        if (IsEnglish)
        {
            var forms = T(one + "|" + few + "|" + many).Split('|');
            if (forms.Length >= 2) return $"{number} {(Math.Abs(n) == 1 ? forms[0] : forms[1])}";
        }
        return $"{number} {RussianForm(n, one, few, many)}";
    }

    /// <summary>Только слово (без числа) в нужной форме.</summary>
    public static string PluralWord(long n, string one, string few, string many)
    {
        if (IsEnglish)
        {
            var forms = T(one + "|" + few + "|" + many).Split('|');
            if (forms.Length >= 2) return Math.Abs(n) == 1 ? forms[0] : forms[1];
        }
        return RussianForm(n, one, few, many);
    }

    private static string RussianForm(long n, string one, string few, string many)
    {
        var a = Math.Abs(n) % 100;
        return a is >= 11 and <= 14 ? many : (a % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }

    /// <summary>Есть ли английский перевод (для тестов полноты).</summary>
    public static bool HasEnglish(string ru) => EnglishTable().ContainsKey(ru);

    /// <summary>Все ключи английского словаря (для тестов).</summary>
    public static IReadOnlyCollection<string> EnglishKeys => EnglishTable().Keys;

    private static Dictionary<string, string> EnglishTable()
    {
        if (_english is { } ready) return ready;
        lock (Gate)
        {
            if (_english is not null) return _english;
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            var asm = typeof(L).Assembly;
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("Offload.Core.Localization.en.", StringComparison.Ordinal))
                         .OrderBy(n => n, StringComparer.Ordinal))
            {
                using var stream = asm.GetManifestResourceStream(name);
                if (stream is null) continue;
                try
                {
                    var part = JsonSerializer.Deserialize<Dictionary<string, string>>(stream,
                        new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    if (part is null) continue;
                    foreach (var (k, v) in part)
                        if (!string.IsNullOrEmpty(v)) table[k] = v;
                }
                catch (JsonException)
                {
                    // Повреждённый файл перевода — просто без него.
                }
            }
            _english = table;
            return table;
        }
    }
}
