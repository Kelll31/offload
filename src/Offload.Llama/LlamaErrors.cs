using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Offload.Llama.Tests")]

namespace Offload.Llama;

/// <summary>Вид ошибки llama-server — устойчивый признак для классификации (не зависит от текста и языка сообщения).</summary>
public enum LlamaErrorKind
{
    /// <summary>Не распознано (прочие HTTP-ошибки без известного типа).</summary>
    Other,
    /// <summary>Сервер не слушает порт (соединение отвергнуто).</summary>
    NotRunning,
    /// <summary>Соединение не установлено по другой причине (сеть, сброс, таймаут подключения).</summary>
    ConnectionFailed,
    /// <summary>Соединение прервалось во время генерации или поток ответа оборвался до конца.</summary>
    ConnectionLost,
    /// <summary>401: неверный ключ API.</summary>
    Unauthorized,
    /// <summary>503: модель ещё загружается.</summary>
    Loading,
    /// <summary>Запрос не помещается в контекст (type = exceed_context_size_error).</summary>
    ContextExceeded,
    /// <summary>HTTP 5xx: внутренняя ошибка сервера.</summary>
    ServerError,
    /// <summary>HTTP 4xx: сервер отклонил запрос.</summary>
    Rejected,
}

/// <summary>
/// Ошибка обращения к API llama-server. Message — на русском (L.T), для пользователя;
/// Kind и Detail — устойчивые данные для классификации и английских сообщений (MCP).
/// </summary>
public sealed class LlamaApiException(string message, int? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>HTTP-код ответа (null — нет соединения).</summary>
    public int? StatusCode { get; } = statusCode;

    /// <summary>Вид ошибки. По умолчанию выводится из кода: null → ConnectionFailed, 401/503/5xx/4xx — соответствующий вид.</summary>
    public LlamaErrorKind Kind { get; init; } = statusCode switch
    {
        null => LlamaErrorKind.ConnectionFailed,
        401 => LlamaErrorKind.Unauthorized,
        503 => LlamaErrorKind.Loading,
        >= 500 => LlamaErrorKind.ServerError,
        >= 400 => LlamaErrorKind.Rejected,
        _ => LlamaErrorKind.Other,
    };

    /// <summary>
    /// Подробность на английском без локализации (например, текст ошибки из JSON llama-server или «connection reset»).
    /// null — подробностей нет. Никогда не содержит текстов L.T и сообщений ОС.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>Для ContextExceeded: токенов в запросе и размер контекста слота (если сервер их сообщил).</summary>
    public int? PromptTokens { get; init; }

    public int? ContextSize { get; init; }
}

/// <summary>Сервер не удалось запустить (Message совпадает с LlamaServerProcess.LastError).</summary>
public sealed class LlamaServerException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Не установлен (или устарел) Visual C++ Redistributable — llama-server не может загрузить MSVCP140.dll/VCRUNTIME140.dll.
/// Мастер ловит это исключение и предлагает VcRuntime.InstallAsync.
/// </summary>
public sealed class LlamaVcRuntimeMissingException(string message) : Exception(message);

/// <summary>Коды завершения Windows (NTSTATUS), по которым распознаются проблемы запуска.</summary>
internal static class NtStatus
{
    public const int DllNotFound = unchecked((int)0xC0000135);
    public const int EntryPointNotFound = unchecked((int)0xC0000139);
    public const int InvalidImageFormat = unchecked((int)0xC000007B);
    public const int AccessViolation = unchecked((int)0xC0000005);
    public const int StackBufferOverrun = unchecked((int)0xC0000409);

    public static string Format(int code) => code < 0 ? $"0x{code:X8}" : code.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string VcMissingMessage =>
        L.T("Не удалось запустить llama-server: в системе нет библиотек Microsoft Visual C++ (MSVCP140.dll, VCRUNTIME140.dll, VCRUNTIME140_1.dll). Установите Microsoft Visual C++ 2015–2022 Redistributable (x64) и повторите попытку.");

    public static string VcOutdatedMessage =>
        L.T("Не удалось запустить llama-server: установленная версия Microsoft Visual C++ Redistributable устарела. Установите последнюю версию Visual C++ 2015–2022 Redistributable (x64) и повторите попытку.");

    /// <summary>Исключение для кода завершения, указывающего на отсутствие runtime-библиотек (или null).</summary>
    public static Exception? StartupFailure(int exitCode) => exitCode switch
    {
        DllNotFound => new LlamaVcRuntimeMissingException(VcMissingMessage),
        EntryPointNotFound => new LlamaVcRuntimeMissingException(VcOutdatedMessage),
        InvalidImageFormat => new InvalidOperationException(
            L.T("Сборка llama.cpp не подходит для этой системы (неверная архитектура процессора или повреждённые файлы). Переустановите llama.cpp.")),
        _ => null,
    };
}
