using System.Net;
using System.Net.Http.Headers;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Core.Security;

namespace Offload.Core.Net;

/// <summary>
/// Действующие сетевые настройки (ROADMAP §9.3): адрес Hugging Face и токен к нему, зеркала GitHub, прокси.
/// Источники по убыванию важности: явное переопределение в процессе → переменные окружения (HF_ENDPOINT, HF_TOKEN)
/// → раздел <see cref="AppConfig.Network"/>. Используются общими клиентами <see cref="Http"/>: зеркала и токен
/// применяются к каждому запросу, поэтому вызывающему коду ничего передавать не нужно.
/// </summary>
public static class NetworkOptions
{
    public const string DefaultHfEndpoint = "https://huggingface.co";
    public const string HfEndpointEnvVar = "HF_ENDPOINT";
    public const string HfTokenEnvVar = "HF_TOKEN";

    public const string GitHubApiHost = "api.github.com";
    public const string GitHubHost = "github.com";

    public const string ProxySystem = "system";
    public const string ProxyNone = "none";
    public const string ProxyCustom = "custom";

    /// <summary>Явный адрес хаба в пределах процесса (тесты, встраивание); важнее переменной окружения и настроек.</summary>
    public static string? HfEndpointOverride { get; set; }

    /// <summary>Сетевые настройки из config.json (пустые, если раздела нет).</summary>
    internal static NetworkSettings Settings => ConfigStore.Current.Network ?? new NetworkSettings();

    /// <summary>Адрес Hugging Face (без «/» в конце): переопределение → HF_ENDPOINT → зеркало из настроек → huggingface.co.</summary>
    public static string HfEndpoint
    {
        get
        {
            if (NormalizeBase(HfEndpointOverride) is { } o) return o;
            if (NormalizeBase(Environment.GetEnvironmentVariable(HfEndpointEnvVar)) is { } e) return e;
            return NormalizeBase(Settings.HfMirror) ?? DefaultHfEndpoint;
        }
    }

    /// <summary>Токен задан переменной окружения HF_TOKEN (она важнее сохранённого в настройках).</summary>
    public static bool HfTokenFromEnvironment => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HfTokenEnvVar));

    /// <summary>Токен Hugging Face: HF_TOKEN или расшифрованный из настроек; null — не задан.</summary>
    public static string? HfToken =>
        Environment.GetEnvironmentVariable(HfTokenEnvVar) is { } env && !string.IsNullOrWhiteSpace(env)
            ? env.Trim()
            : Dpapi.Unprotect(Settings.HfTokenProtected)?.Trim() is { Length: > 0 } t ? t : null;

    /// <summary>В настройках сохранён токен Hugging Face.</summary>
    public static bool HasStoredHfToken => !string.IsNullOrWhiteSpace(Settings.HfTokenProtected);

    /// <summary>
    /// Заголовок Authorization для запроса: токен Hugging Face — только хабу (тот же протокол, хост и порт),
    /// только по HTTPS (или на локальный адрес) и зеркалу — лишь с явного разрешения. Иначе null.
    /// Перенаправления (resolve → CDN) токен не получают: HttpClient снимает Authorization при переходе.
    /// </summary>
    public static AuthenticationHeaderValue? AuthorizationFor(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri) return null;
        var explicitEndpoint = NormalizeBase(HfEndpointOverride) ?? NormalizeBase(Environment.GetEnvironmentVariable(HfEndpointEnvVar));
        // Локальные адреса (llama-server, тестовые серверы) — не хаб, если хаб не задан явно: настройки даже не читаются.
        if (uri.IsLoopback && explicitEndpoint is null) return null;

        if (!Uri.TryCreate(explicitEndpoint ?? HfEndpoint, UriKind.Absolute, out var hub) || !SameOrigin(uri, hub)) return null;
        if (hub.Scheme != Uri.UriSchemeHttps && !hub.IsLoopback) return null;
        if (!IsOfficialHub(hub) && explicitEndpoint is null && !Settings.SendHfTokenToMirror) return null;
        return HfToken is { } token ? new AuthenticationHeaderValue("Bearer", token) : null;
    }

    /// <summary>Адрес ведёт на хаб Hugging Face (действующий адрес или зеркало).</summary>
    public static bool IsHfUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && Uri.TryCreate(HfEndpoint, UriKind.Absolute, out var hub)
        && SameOrigin(uri, hub);

    /// <summary>
    /// Адрес с учётом зеркал GitHub: https://api.github.com/… → зеркало API, https://github.com/… → зеркало github.com
    /// (к адресу зеркала дописываются путь и запрос). Остальные адреса не меняются.
    /// Адрес зеркала проверяется здесь же (<see cref="ValidateMirror"/>): config.json можно поправить и вручную,
    /// поэтому неподходящее зеркало (http:// на внешний адрес, логин в адресе, «?»/«#») не используется — запрос идёт
    /// на исходный адрес, а в журнал пишется предупреждение (один раз на значение).
    /// </summary>
    public static Uri RewriteForMirror(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort) return uri;
        string? mirror;
        if (uri.Host.Equals(GitHubApiHost, StringComparison.OrdinalIgnoreCase)) mirror = Settings.GitHubApiMirror;
        else if (uri.Host.Equals(GitHubHost, StringComparison.OrdinalIgnoreCase)) mirror = Settings.GitHubMirror;
        else return uri;
        if (NormalizeBase(mirror) is not { } b) return uri;
        if (ValidateMirror(b) is { } error)
        {
            WarnInvalidMirror(b, error);
            return uri;
        }
        return Uri.TryCreate(b + uri.PathAndQuery, UriKind.Absolute, out var rewritten) ? rewritten : uri;
    }

    private static string? _lastInvalidMirror;

    private static void WarnInvalidMirror(string mirror, string error)
    {
        if (string.Equals(Interlocked.Exchange(ref _lastInvalidMirror, mirror), mirror, StringComparison.Ordinal)) return;
        // Адрес может содержать логин и пароль — в журнал только схема и хост.
        var shown = Uri.TryCreate(mirror, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.IdnHost}" : "?";
        Log.Warn("network", $"Зеркало GitHub {shown} из настроек не используется, запрос идёт напрямую: {error.Replace(mirror, shown, StringComparison.Ordinal)}");
    }

    /// <summary>
    /// Адрес файла релиза из ответа GitHub API, которому можно доверить загрузку: только
    /// https://github.com/{repo}/releases/download/… (зеркало загрузок подставит <see cref="RewriteForMirror"/>),
    /// без логина, запроса, фрагмента и закодированных «.», «/», «\» в пути. Зеркало API тем самым не может
    /// подсунуть файл с чужого хоста или из чужого репозитория. <paramref name="webBase"/> — подменённый адрес
    /// github.com (тестовый сервер); https://github.com принимается всегда.
    /// </summary>
    public static bool IsReleaseAssetUrl(string? url, string repo, string? webBase = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        if (uri.OriginalString.Contains("%2e", StringComparison.OrdinalIgnoreCase)
            || uri.OriginalString.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || uri.OriginalString.Contains("%5c", StringComparison.OrdinalIgnoreCase)
            || uri.OriginalString.Contains('\\', StringComparison.Ordinal)) return false;
        var suffix = "/" + repo.Trim('/') + "/releases/download/";
        foreach (var b in new[] { "https://" + GitHubHost, NormalizeBase(webBase) })
        {
            if (b is null || !Uri.TryCreate(b, UriKind.Absolute, out var baseUri)) continue;
            if (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback) continue;
            if (!SameOrigin(uri, baseUri)) continue;
            var prefix = baseUri.AbsolutePath.TrimEnd('/') + suffix;
            if (uri.AbsolutePath.Length > prefix.Length && uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Адрес без пробелов и «/» в конце; null — пусто.</summary>
    public static string? NormalizeBase(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/') is { Length: > 0 } t ? t : null;

    /// <summary>Проверка адреса зеркала: пусто или абсолютный https:// (http:// — только локальный), без запроса и фрагмента.</summary>
    /// <returns>Текст ошибки для пользователя или null, если адрес подходит.</returns>
    public static string? ValidateMirror(string? url)
    {
        if (NormalizeBase(url) is not { } u) return null;
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return L.F("«{0}» — не адрес http(s)://.", u);
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            return L.F("«{0}»: зеркало должно работать по HTTPS (http:// допустим только для локального адреса).", u);
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            return L.F("«{0}»: адрес зеркала не должен содержать логин, «?» или «#».", u);
        return null;
    }

    /// <summary>Проверка адреса прокси: http://, https://, socks4://, socks4a:// или socks5:// с хостом.</summary>
    public static string? ValidateProxy(string? url)
    {
        if (NormalizeBase(url) is not { } u) return L.T("Укажите адрес прокси, например http://proxy.local:3128.");
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) ||
            uri.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5"))
            return L.F("«{0}» — не адрес прокси (http://, https://, socks5://).", u);
        return null;
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    private static bool IsOfficialHub(Uri hub) =>
        hub.Scheme == Uri.UriSchemeHttps && hub.IsDefaultPort
        && hub.IdnHost.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Прокси по настройкам, читаемым при каждом запросе: смена режима применяется без перезапуска.
/// Локальные адреса всегда идут напрямую (и не читают настройки).
/// </summary>
internal sealed class ConfiguredProxy : IWebProxy
{
    public static ConfiguredProxy Instance { get; } = new();

    private ConfiguredProxy()
    {
    }

    /// <summary>Учётные данные подбираются при каждом запросе (логин из адреса своего прокси или учётная запись Windows).</summary>
    public ICredentials? Credentials
    {
        get => DynamicCredentials.Instance;
        set { }
    }

    public Uri? GetProxy(Uri destination)
    {
        if (destination.IsLoopback) return null;
        return Current() switch
        {
            (NetworkOptions.ProxyNone, _) => null,
            (NetworkOptions.ProxyCustom, { } custom) => StripUserInfo(custom),
            _ => HttpClient.DefaultProxy.GetProxy(destination),
        };
    }

    public bool IsBypassed(Uri host)
    {
        if (host.IsLoopback) return true;
        return Current() switch
        {
            (NetworkOptions.ProxyNone, _) => true,
            (NetworkOptions.ProxyCustom, not null) => false,
            _ => HttpClient.DefaultProxy.IsBypassed(host),
        };
    }

    /// <summary>Режим и адрес своего прокси; неверный адрес своего прокси — как «системный».</summary>
    internal static (string Mode, Uri? Custom) Current()
    {
        NetworkSettings s;
        try
        {
            s = NetworkOptions.Settings;
        }
        catch (Exception)
        {
            return (NetworkOptions.ProxySystem, null);
        }
        var mode = (s.ProxyMode ?? NetworkOptions.ProxySystem).Trim().ToLowerInvariant();
        if (mode == NetworkOptions.ProxyCustom)
        {
            return NetworkOptions.ValidateProxy(s.ProxyUrl) is null && Uri.TryCreate(s.ProxyUrl!.Trim(), UriKind.Absolute, out var u)
                ? (mode, u)
                : (NetworkOptions.ProxySystem, null);
        }
        return (mode == NetworkOptions.ProxyNone ? mode : NetworkOptions.ProxySystem, null);
    }

    private static Uri StripUserInfo(Uri u) =>
        string.IsNullOrEmpty(u.UserInfo) ? u : new UriBuilder(u) { UserName = "", Password = "" }.Uri;

    /// <summary>Логин и пароль из адреса своего прокси (user:pass@host), иначе — учётная запись Windows.</summary>
    private sealed class DynamicCredentials : ICredentials
    {
        public static DynamicCredentials Instance { get; } = new();

        public NetworkCredential? GetCredential(Uri uri, string authType)
        {
            var (mode, custom) = Current();
            if (mode == NetworkOptions.ProxyCustom && custom is { UserInfo.Length: > 0 })
            {
                var parts = custom.UserInfo.Split(':', 2);
                return new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
            }
            return CredentialCache.DefaultNetworkCredentials;
        }
    }
}
