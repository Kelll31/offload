using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Offload.Core;

namespace Offload.Llama;

/// <summary>Разбор завершения llama-server: понятное сообщение, подсказка и важные строки журнала.</summary>
internal static class ServerExitDiagnostics
{
    private static readonly Regex LogPrefix = new(@"^\d+\.\d+\.\d+\.\d+\s+[DIWE]\s+", RegexOptions.CultureInvariant);

    /// <summary>Понятное описание завершения с подсказкой и последними важными строками журнала.</summary>
    internal static string DescribeExit(IReadOnlyList<string> tail, int code, bool whileStarting, int port)
    {
        if (NtStatus.StartupFailure(code) is { } known) return known.Message;

        var sb = new StringBuilder(whileStarting
            ? L.F("llama-server не запустился (код {0}).", NtStatus.Format(code))
            : L.F("llama-server неожиданно завершился (код {0}).", NtStatus.Format(code)));
        if (Hint(tail, code, port) is { } hint) sb.Append(' ').Append(hint);
        var lines = ImportantLines(tail);
        if (lines.Count > 0) sb.Append(' ').Append(L.F("Журнал: {0}", string.Join(" | ", lines)));
        return sb.ToString();
    }

    /// <summary>Порт из аргументов запуска (--port N); 0 — не указан.</summary>
    internal static int PortFromArguments(IReadOnlyList<string> args)
    {
        var port = 0;
        for (var i = 0; i + 1 < args.Count; i++)
            if (args[i] == "--port") int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out port);
        return port;
    }

    /// <summary>Подсказка по последним строкам журнала и коду завершения; null — причина не распознана.</summary>
    internal static string? Hint(IReadOnlyList<string> tail, int code, int port)
    {
        var text = string.Join("\n", tail).ToLowerInvariant();
        bool Has(params string[] keys) => keys.Any(text.Contains);
        if (Has("out of memory", "cudamalloc failed", "failed to allocate", "outofdevicememory", "unable to allocate", "not enough memory",
                "failed to create context", "error_out_of"))
            return L.T("Не хватило памяти (видеопамяти или ОЗУ): уменьшите размер контекста или число параллельных слотов либо выберите модель меньше.");
        if (Has("failed to load model", "error loading model", "invalid magic", "gguf_init_from_file", "failed to read magic", "unknown model architecture"))
            return L.T("Не удалось загрузить модель: файл повреждён или не докачан, либо его формат не поддерживается этой версией llama.cpp (обновите llama.cpp).");
        if (Has("couldn't bind", "could not bind", "failed to bind", "address already in use", "only one usage of each socket address"))
            return port > 0 ? L.F("Порт {0} занят другой программой.", port) : L.T("Порт занят другой программой.");
        if (Has("invalid argument", "unknown argument", "unknown value", "error while handling argument", "invalid value"))
            return L.T("llama-server не принял параметры запуска — проверьте поле «Дополнительные аргументы» в настройках сервера.");
        if (Has("cuda driver version is insufficient", "no cuda-capable device", "ggml_cuda_init: failed", "cuda error", "vk::", "vulkan error"))
            return L.T("Сборка llama.cpp не смогла использовать видеокарту: обновите драйвер или выберите другую сборку (например, Vulkan).");
        if (code is NtStatus.AccessViolation or NtStatus.StackBufferOverrun)
            return L.T("Процесс аварийно завершился (сбой в llama.cpp или в драйвере видеокарты).");
        return null;
    }

    /// <summary>
    /// Виновата ли сама сборка llama.cpp в том, что сервер не запустился: да — аргументы не приняты, сбой GPU-бэкенда,
    /// аварийное завершение или неизвестная причина; нет — нехватка памяти, битый файл модели, занятый порт, нет VC++ Runtime.
    /// По этому признаку откатывается только что установленная сборка (откат при OOM ничего бы не исправил).
    /// </summary>
    internal static bool BlamesBuild(IReadOnlyList<string> tail, int code)
    {
        if (NtStatus.StartupFailure(code) is LlamaVcRuntimeMissingException) return false;
        var text = string.Join('\n', tail).ToLowerInvariant();
        bool Has(params string[] keys) => keys.Any(text.Contains);
        if (Has("out of memory", "cudamalloc failed", "failed to allocate", "outofdevicememory", "unable to allocate", "not enough memory",
                "failed to create context", "error_out_of")) return false;
        if (Has("invalid magic", "gguf_init_from_file", "failed to read magic")) return false;
        if (Has("couldn't bind", "could not bind", "failed to bind", "address already in use", "only one usage of each socket address")) return false;
        return true;
    }

    /// <summary>До 4 строк с ошибками (без префикса времени), иначе 3 последние строки; каждая не длиннее 200 символов.</summary>
    internal static List<string> ImportantLines(IReadOnlyList<string> tail)
    {
        var cleaned = tail.Select(l => LogPrefix.Replace(l.Trim(), "")).Where(l => l.Length > 0).ToList();
        var important = tail
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && (Regex.IsMatch(l, @"^\d+\.\d+\.\d+\.\d+\s+E\s") || Regex.IsMatch(l, @"\b(error|failed|exception|out of memory|unable|abort)", RegexOptions.IgnoreCase)))
            .Select(l => LogPrefix.Replace(l, ""))
            .ToList();
        var pick = important.Count > 0 ? important.TakeLast(4) : cleaned.TakeLast(3);
        return pick.Select(l => l.Length > 200 ? l[..200] + "…" : l).ToList();
    }
}
