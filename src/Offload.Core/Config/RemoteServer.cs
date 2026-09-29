using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Offload.Core.Security;

namespace Offload.Core.Config;

/// <summary>
/// Клиентский режим (ROADMAP §9.2): основная модель работает не в своём llama-server, а на другом компьютере — обычно
/// на мощном ПК с Offload в режиме «Доступ из сети» (<see cref="ServerSettings.LanAccess"/>) или на любом
/// OpenAI-совместимом сервере. Трей тогда не запускает основной llama-server, а MCP и OpenCode обращаются по <see cref="Url"/>.
/// Вспомогательные роли (fast/embed/rerank) по-прежнему локальные.
/// </summary>
public sealed class RemoteServerSettings
{
    /// <summary>Использовать удалённый сервер вместо своего основного llama-server.</summary>
    public bool Enabled { get; set; }

    /// <summary>Адрес сервера «http(s)://хост:порт» (без пути, логина и параметров; «/v1» в конце допускается и отбрасывается).</summary>
    public string Url { get; set; } = "";

    /// <summary>Ключ API удалённого сервера, зашифрованный DPAPI для текущего пользователя Windows (base64).</summary>
    public string? ApiKeyProtected { get; set; }

    /// <summary>
    /// Идентификатор модели из /v1/models удалённого сервера (последняя успешная проверка трея). Пусто — псевдоним
    /// <see cref="RemoteServer.DefaultModel"/>; MCP всё равно сверяет его со списком сервера перед каждым обращением.
    /// </summary>
    public string? ModelId { get; set; }

    /// <summary>Контекст на запрос по /props удалённого сервера (последняя проверка; 0 — неизвестен). Только запасное значение.</summary>
    public int ContextSize { get; set; }
}

/// <summary>Куда обращаться за основной моделью: свой llama-server или удалённый сервер.</summary>
/// <param name="BaseUrl">«http(s)://хост:порт» без завершающей «/».</param>
/// <param name="ApiKey">Ключ Bearer (пусто — не удалось расшифровать ключ удалённого сервера).</param>
/// <param name="Model">Имя модели в запросах (псевдоним локального сервера или id удалённой модели).</param>
/// <param name="IsRemote">Удалённый сервер (трей его не запускает и не перезапускает).</param>
public sealed record MainEndpoint(string BaseUrl, string ApiKey, string Model, bool IsRemote)
{
    public string OpenAiBaseUrl => BaseUrl + "/v1";

    /// <summary>«хост:порт» для показа пользователю и журнала (ключ в адресе не бывает — это проверяет валидация).</summary>
    public string Host => RemoteServer.DisplayHost(BaseUrl);
}

/// <summary>Клиентский режим: проверка адреса, ключ, адрес основной модели для всех потребителей (MCP, OpenCode, трей).</summary>
public static partial class RemoteServer
{
    /// <summary>Псевдоним модели по умолчанию (= --alias основного llama-server Offload, LlamaServerArgs.DefaultAlias).</summary>
    public const string DefaultModel = "offload";

    /// <summary>Наибольшая длина адреса и ключа: защита от вставки мусора в поле.</summary>
    public const int MaxUrlLength = 512;

    public const int MaxKeyLength = 512;

    /// <summary>
    /// id модели, пришедший от удалённого сервера (или из config.json), можно передавать дальше: в конфиг OpenCode, журнал,
    /// ответы MCP. Только буквы, цифры и «. _ : / \ @ + - ( ) [ ]» и пробел — без «{}» (OpenCode подставляет в конфиг
    /// {env:…}/{file:…}: враждебный сервер вытянул бы файлы и переменные окружения), кавычек, «$» и управляющих символов.
    /// </summary>
    public static bool IsSafeModelId(string? id) => id is { Length: > 0 and <= 256 } && SafeModelIdRegex().IsMatch(id);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._:/\\@+()\[\]-]*$")]
    private static partial Regex SafeModelIdRegex();

    /// <summary>Включён клиентский режим с корректным адресом. Неверный адрес в config.json (ручная правка) — режим не действует.</summary>
    public static bool IsRemote(this AppConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        return cfg.Remote is { Enabled: true } r && TryNormalizeUrl(r.Url, out _, out _);
    }

    /// <summary>Расшифрованный ключ удалённого сервера; null — не задан или зашифрован другим пользователем Windows.</summary>
    public static string? RemoteApiKey(this AppConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        return Dpapi.Unprotect(cfg.Remote?.ApiKeyProtected)?.Trim() is { Length: > 0 } k ? k : null;
    }

    /// <summary>
    /// Адрес, ключ и имя модели основного сервера. Единственное место выбора «свой сервер / удалённый»: им пользуются
    /// LlamaClient.FromConfig, OpenCode, local_status и страницы трея.
    /// </summary>
    public static MainEndpoint MainEndpoint(this AppConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (cfg.Remote is { Enabled: true } r && TryNormalizeUrl(r.Url, out var url, out _))
        {
            var model = IsSafeModelId(r.ModelId?.Trim()) ? r.ModelId!.Trim() : DefaultModel;
            return new MainEndpoint(url, cfg.RemoteApiKey() ?? "", model, IsRemote: true);
        }
        var s = cfg.Server ?? new ServerSettings();
        return new MainEndpoint(s.BaseUrl, s.ApiKey ?? "", DefaultModel, IsRemote: false);
    }

    /// <summary>
    /// Проверить и привести адрес удалённого сервера к виду «scheme://хост[:порт]». Допускаются только абсолютные http/https
    /// без логина/пароля, параметров, фрагмента и пути (кроме «/v1», который отбрасывается). <paramref name="error"/> — текст
    /// для пользователя (null при успехе).
    /// </summary>
    public static bool TryNormalizeUrl(string? input, out string normalized, out string? error)
    {
        normalized = "";
        var text = input?.Trim() ?? "";
        if (text.Length == 0)
        {
            error = L.T("Укажите адрес удалённого сервера, например http://192.168.1.10:8765.");
            return false;
        }
        if (text.Length > MaxUrlLength || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || text.Contains('\\'))
        {
            error = L.T("Адрес удалённого сервера содержит недопустимые символы.");
            return false;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = L.T("Адрес удалённого сервера должен начинаться с http:// или https://.");
            return false;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo) || text.Contains('@'))
        {
            error = L.T("Логин и пароль в адресе не допускаются — ключ API вводится в отдельном поле.");
            return false;
        }
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || text.Contains('?') || text.Contains('#'))
        {
            error = L.T("Адрес удалённого сервера не должен содержать параметров («?», «#»).");
            return false;
        }
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length != 0 && !path.Equals("/v1", StringComparison.OrdinalIgnoreCase))
        {
            error = L.T("Укажите только адрес сервера без пути: http://хост:порт (допускается «/v1» в конце).");
            return false;
        }
        if (string.IsNullOrEmpty(uri.Host) || uri.Host is "0.0.0.0" or "[::]" or "::")
        {
            error = L.T("В адресе удалённого сервера нет имени или IP-адреса компьютера.");
            return false;
        }
        normalized = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        error = null;
        return true;
    }

    /// <summary>Проверка ключа API удалённого сервера: без пробелов и управляющих символов (уходит в заголовок Authorization).</summary>
    public static string? ValidateApiKey(string? key)
    {
        var k = key?.Trim() ?? "";
        if (k.Length == 0) return L.T("Укажите ключ API удалённого сервера — без ключа сервер Offload не принимает запросы из сети.");
        if (k.Length > MaxKeyLength || k.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c > '~'))
            return L.T("Ключ API содержит недопустимые символы (пробелы, переводы строк, не-ASCII).");
        return null;
    }

    /// <summary>«хост:порт» из адреса (для показа и журнала).</summary>
    public static string DisplayHost(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) ? u.Authority : baseUrl;
}

/// <summary>
/// Режим «Доступ из сети» (сторона мощного ПК): основной llama-server слушает адрес локальной сети, а не только 127.0.0.1.
/// Включается только с отдельным сетевым ключом (<see cref="ServerSettings.LanApiKeyProtected"/>): llama-server получает оба
/// ключа (локальный и сетевой) через LLAMA_API_KEY списком через запятую. Вспомогательные серверы ролей всегда на 127.0.0.1.
/// </summary>
public static class LanServer
{
    /// <summary>Все адреса IPv4.</summary>
    public const string AnyAddress = "0.0.0.0";

    /// <summary>
    /// Режим действует: включён, адрес прослушивания допустим и сетевой ключ задан (локальный ключ ConfigStore создаёт всегда).
    /// Без ключа сервер остаётся на петлевом адресе — открыть llama-server в сеть без ключа нельзя.
    /// </summary>
    public static bool IsActive(this ServerSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.LanAccess && IsValidBindAddress(s.LanBindAddress)
               && !string.IsNullOrWhiteSpace(s.LanApiKeyProtected) && !string.IsNullOrWhiteSpace(s.ApiKey);
    }

    /// <summary>
    /// Адрес для --host основного llama-server: адрес сети в режиме «Доступ из сети», иначе <see cref="LoopbackHost"/>.
    /// Непетлевой <see cref="ServerSettings.Host"/> (ручная правка config.json) без действующего режима с сетевым ключом не действует.
    /// </summary>
    public static string ListenHost(this ServerSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.IsActive()) return s.LanBindAddress.Trim();
        return s.LoopbackHost();
    }

    /// <summary>
    /// Петлевой адрес для --host: <see cref="ServerSettings.Host"/>, если это петлевой адрес (127.x.x.x, ::1, localhost), иначе 127.0.0.1.
    /// Так слушают вспомогательные серверы ролей (в том числе автодополнение) и основной сервер без «Доступа из сети».
    /// </summary>
    public static string LoopbackHost(this ServerSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var h = s.Host?.Trim() ?? "";
        return IsLoopbackHost(h) ? h : "127.0.0.1";
    }

    /// <summary>Петлевой адрес: localhost, 127.x.x.x или ::1 (в том числе в скобках).</summary>
    public static bool IsLoopbackHost(string? host)
    {
        var h = host?.Trim() ?? "";
        if (h.Length == 0) return false;
        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (h.StartsWith('[') && h.EndsWith(']')) h = h[1..^1];
        return IPAddress.TryParse(h, out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// Запущенный процесс слушает сеть (<paramref name="runningHost"/> — его --host) не так, как велят настройки: режим уже выключен,
    /// сетевого ключа больше нет или выбран другой адрес. Тогда состояние «выключено» показывать нельзя — нужен перезапуск.
    /// </summary>
    public static bool ListenerOutdated(string? runningHost, ServerSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (string.IsNullOrWhiteSpace(runningHost) || IsLoopbackHost(runningHost)) return false;
        return !s.IsActive() || !string.Equals(runningHost.Trim(), s.ListenHost(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Режим «Доступ из сети» привязан к конкретному адресу, которого у компьютера больше нет (сменился адрес DHCP, отключён VPN):
    /// текст ошибки для пользователя; null — адрес на месте, выбран 0.0.0.0 или режим не действует.
    /// </summary>
    /// <param name="localAddresses">Адреса интерфейсов (null — <see cref="LocalIPv4Addresses"/>).</param>
    public static string? StaleBindAddressError(ServerSettings s, IReadOnlyList<string>? localAddresses = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!s.IsActive()) return null;
        var bind = s.LanBindAddress.Trim();
        if (bind == AnyAddress) return null;
        var local = localAddresses ?? LocalIPv4Addresses();
        if (local.Contains(bind)) return null;
        return L.F("Адрес {0} больше не принадлежит этому компьютеру (сменился адрес в сети или отключён VPN) — выберите адрес в «Настройки» → «Доступ из сети».", bind);
    }

    /// <summary>
    /// Допустимый адрес прослушивания: 0.0.0.0 (все интерфейсы) или IPv4-адрес интерфейса (не петлевой, не групповой,
    /// не широковещательный). Имена хостов не принимаются — адрес должен быть однозначным.
    /// </summary>
    public static bool IsValidBindAddress(string? address)
    {
        var a = address?.Trim();
        if (string.IsNullOrEmpty(a)) return false;
        if (a == AnyAddress) return true;
        if (!IPAddress.TryParse(a, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        // IPAddress.TryParse принимает и «1», и «010.1»: требуем канонический вид a.b.c.d.
        if (ip.ToString() != a) return false;
        var b = ip.GetAddressBytes();
        if (b[0] is 0 or 127 || b[0] >= 224) return false;
        return true;
    }

    /// <summary>Расшифрованный сетевой ключ; null — не задан или не расшифровывается (другой пользователь Windows).</summary>
    public static string? LanApiKey(this ServerSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Dpapi.Unprotect(s.LanApiKeyProtected)?.Trim() is { Length: > 0 } k ? k : null;
    }

    /// <summary>Новый сетевой ключ (256 бит). Только [a-z0-9-]: без запятых — ключи передаются llama-server списком через запятую.</summary>
    public static string NewLanApiKey() => "olan-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>
    /// Значение LLAMA_API_KEY основного сервера: локальный ключ и, в режиме «Доступ из сети», сетевой — через запятую.
    /// Режим включён, но сетевой ключ не расшифровывается — InvalidOperationException: сервер не должен слушать сеть
    /// с ключом, которого пользователь не знает, и молча вернуться на петлевой адрес тоже не должен.
    /// </summary>
    public static string? ApiKeysForServer(ServerSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var local = s.ApiKey?.Trim();
        if (!s.IsActive()) return string.IsNullOrEmpty(local) ? null : local;
        var lan = s.LanApiKey() ?? throw new InvalidOperationException(
            L.T("Не удалось расшифровать ключ доступа из сети (он создан другим пользователем Windows). Создайте новый ключ в настройках «Доступ из сети» или выключите этот режим."));
        if (string.IsNullOrEmpty(local) || local.Contains(',') || lan.Contains(','))
            throw new InvalidOperationException(L.T("Ключи API сервера не должны быть пустыми или содержать запятые."));
        return local + "," + lan;
    }

    /// <summary>IPv4-адреса работающих сетевых интерфейсов (без петлевого и APIPA 169.254.*), для списка выбора и подсказки адресов.</summary>
    public static IReadOnlyList<string> LocalIPv4Addresses()
    {
        var result = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var text = ua.Address.ToString();
                    if (text.StartsWith("169.254.", StringComparison.Ordinal) || !IsValidBindAddress(text)) continue;
                    if (!result.Contains(text)) result.Add(text);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Нет сведений об интерфейсах — пользователь может ввести адрес вручную.
        }
        return result;
    }

    /// <summary>
    /// Адреса для подключения с другого компьютера: при 0.0.0.0 — по одному на каждый адрес интерфейса, иначе выбранный адрес.
    /// </summary>
    public static IReadOnlyList<string> ConnectUrls(ServerSettings s, IReadOnlyList<string>? localAddresses = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        var bind = s.LanBindAddress?.Trim() ?? AnyAddress;
        var hosts = bind == AnyAddress ? localAddresses ?? LocalIPv4Addresses() : [bind];
        return hosts.Select(h => $"http://{h}:{s.Port}").ToList();
    }

    /// <summary>
    /// Адрес для клиентов на этом же компьютере: 0.0.0.0/«*» → 127.0.0.1, «::» → [::1], IPv6 — в скобках.
    /// При прослушивании конкретного адреса сети петлевой адрес не отвечает — клиенты обращаются по нему.
    /// </summary>
    public static string ClientBaseUrl(string host, int port)
    {
        var h = (host ?? "").Trim();
        if (h is "0.0.0.0" or "*" or "+" || h.Length == 0) h = "127.0.0.1";
        else if (h is "::" or "[::]") h = "[::1]";
        else if (h.Contains(':') && !h.StartsWith('[')) h = $"[{h}]";
        return $"http://{h}:{port}";
    }
}
