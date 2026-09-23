using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Offload.Llama.Tests")]

namespace Offload.Llama;

/// <summary>Ошибка обращения к API llama-server. Message — на русском, для пользователя.</summary>
public sealed class LlamaApiException(string message, int? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>HTTP-код ответа (null — нет соединения).</summary>
    public int? StatusCode { get; } = statusCode;
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
