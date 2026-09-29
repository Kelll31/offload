using System.Diagnostics;
using Offload.Core.Logging;
using Offload.Integrations.Clients;
using Offload.Integrations.Editing;

namespace Offload.Integrations;

/// <summary>Итог сквозной проверки подключения: запись прочитана так, как её читает IDE, и сервер по ней запущен.</summary>
/// <param name="Ok">Запись наша и совпадает с ожидаемой, сервер ответил на initialize и tools/list, в stdout нет постороннего текста.</param>
/// <param name="Kind"><see cref="FailureKind.None"/> при успехе, иначе причина.</param>
/// <param name="Message">Подробности сбоя (пусто при успехе).</param>
/// <param name="ToolCount">Сколько инструментов вернул сервер.</param>
/// <param name="Elapsed">Время последней (успешной) попытки рукопожатия.</param>
/// <param name="Attempts">Сколько раз запускали сервер (с учётом повторов).</param>
public sealed record VerifyResult(bool Ok, FailureKind Kind, string Message, int ToolCount, TimeSpan Elapsed, int Attempts = 0)
{
    /// <summary>Что делать пользователю (пусто при успехе).</summary>
    public string Hint => FailureText.Hint(Kind);

    /// <summary>Одна строка для интерфейса: «проверено (N инструментов, 1,2 с)» либо причина.</summary>
    public string Summary => Ok
        ? L.F("проверено ({0}, {1:0.0} с)", L.Plural(ToolCount, "инструмент", "инструмента", "инструментов"), Elapsed.TotalSeconds)
        : Message.Length > 0 ? Message : FailureText.Reason(Kind);
}

/// <summary>
/// Проверка «подключение действительно работает»: перечитывает конфиг IDE тем же способом, что и сама IDE
/// (<see cref="IntegrationBase.ProbeEntries"/>), убеждается, что запись наша, и запускает <b>именно команду из записи</b>
/// (initialize + tools/list). Запуск разрешается только для точного пути текущего или установленной копии Offload с
/// аргументами [«--mcp»] (<see cref="IntegrationDoctor.MayLaunch"/>); сетевые пути не запускаются.
/// Неудачное рукопожатие повторяется (первый запуск single-file exe и антивирус) с паузами 2/5/10 с; ошибки, которые
/// повтором не лечатся (нет файла, не наш конфиг, мусор в stdout), сразу возвращаются.
/// Проверка ничего не пишет в конфиги и запись не откатывает.
/// </summary>
public static class IntegrationVerifier
{
    /// <summary>Паузы перед повторами рукопожатия.</summary>
    internal static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    internal delegate Task<HandshakeResult> Handshaker(string command, IReadOnlyList<string> args, CancellationToken ct);

    public static Task<VerifyResult> VerifyAsync(
        IIdeIntegration integration, McpServerSpec spec, IReadOnlyCollection<string>? installedCopies = null, CancellationToken ct = default) =>
        VerifyAsync(integration, spec, installedCopies,
            (command, args, token) => McpHandshake.RunProcessAsync(command, args, IntegrationDoctor.HandshakeTimeout, token),
            Task.Delay, RetryDelays, ct);

    internal static async Task<VerifyResult> VerifyAsync(
        IIdeIntegration integration,
        McpServerSpec spec,
        IReadOnlyCollection<string>? installedCopies,
        Handshaker handshake,
        Func<TimeSpan, CancellationToken, Task> delay,
        IReadOnlyList<TimeSpan> retryDelays,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(integration);
        ArgumentNullException.ThrowIfNull(spec);
        var sw = Stopwatch.StartNew();
        VerifyResult Fail(FailureKind kind, string message, int attempts = 0, HandshakeResult? h = null) =>
            new(false, kind, message, h?.ToolCount ?? 0, sw.Elapsed, attempts);

        try
        {
            if (!integration.IsClientInstalled()) return Fail(FailureKind.ClientNotFound, FailureText.Reason(FailureKind.ClientNotFound));
            // Claude Code в WSL запускает exe через interop (/mnt/c/…, лишние аргументы) — изнутри Windows это не воспроизвести.
            if (integration is not IntegrationBase b || integration is WslClaudeCodeIntegration)
                return Fail(FailureKind.NotVerifiable, FailureText.Reason(FailureKind.NotVerifiable));
            var probes = b.ProbeEntries(spec);
            if (probes.Count == 0) return Fail(FailureKind.NotVerifiable, FailureText.Reason(FailureKind.NotVerifiable));

            // IDE читает один из файлов — достаточно одной нашей записи; иначе сообщаем о первой проблеме (ошибка чтения важнее).
            var ours = probes.FirstOrDefault(p => p.State == ProbeState.Ours);
            if (ours is null) return ProbeFailure(probes, spec, Fail);
            var entry = ours.Entry!;
            var command = entry.Command ?? "";

            if (CommandPath.IsUnc(command))
                return Fail(FailureKind.ExeBlocked, L.F("запись указывает на сетевой путь ({0}) — он не запускается", command));
            if (!File.Exists(command))
                return Fail(FailureKind.ExeMissing, L.F("Offload.exe по пути из записи не найден: {0}", command));
            if (!IntegrationDoctor.MayLaunch(entry, spec, installedCopies))
                return Fail(FailureKind.NotVerifiable,
                    L.F("запись указывает на другую копию Offload или с другими аргументами ({0}) — запуск для проверки не выполнялся", command));

            HandshakeResult? last = null;
            var kind = FailureKind.StartFailed;
            var attempts = 0;
            for (var attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                attempts++;
                try
                {
                    last = await handshake(command, entry.Args, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warn("Integrations", $"{integration.Id}: проверка запуска: {ex.Message}");
                    last = new HandshakeResult(false, null, null, null, 0, TimeSpan.Zero, ex.Message, 0, FailureKind.StartFailed);
                }

                kind = Classify(last);
                if (kind == FailureKind.None) break;
                if (attempt >= retryDelays.Count || !Retryable(kind)) break;
                Log.Debug("Integrations", $"{integration.Id}: проверка запуска не удалась ({kind}), повтор через {retryDelays[attempt].TotalSeconds:0} с");
                await delay(retryDelays[attempt], ct);
            }

            if (kind == FailureKind.None)
                return new VerifyResult(true, FailureKind.None, "", last!.ToolCount, last.Elapsed, attempts);
            var reason = FailureText.Reason(kind);
            var detail = last!.Error is { Length: > 0 } e ? e : kind == FailureKind.ProtocolNoise
                ? L.F("строк не в формате JSON-RPC: {0}", last.NonJsonLines)
                : "";
            return Fail(kind, detail.Length > 0 && kind != FailureKind.ProtocolNoise ? $"{reason}: {detail}" : detail.Length > 0 ? $"{reason} ({detail})" : reason,
                attempts, last);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Log.Warn("Integrations", $"{integration.Id}: проверка подключения: {ex.Message}");
            return Fail(FailureKind.ConfigLocked, ex.Message);
        }
    }

    /// <summary>Итог рукопожатия → причина (<see cref="FailureKind.None"/> — всё хорошо).</summary>
    public static FailureKind Classify(HandshakeResult h)
    {
        if (h.Ok)
        {
            if (h.NonJsonLines > 0) return FailureKind.ProtocolNoise;
            return h.ToolCount > 0 ? FailureKind.None : FailureKind.StartFailed;
        }
        // Запуск запрещён/файла нет — посторонний текст ни при чём; иначе он — вероятная причина сбоя.
        if (h.Kind is FailureKind.ExeBlocked or FailureKind.ExeMissing) return h.Kind;
        if (h.NonJsonLines > 0) return FailureKind.ProtocolNoise;
        return h.Kind == FailureKind.None ? FailureKind.StartFailed : h.Kind;
    }

    /// <summary>Повтор помогает от медленного первого запуска и антивируса; мусор в stdout и отсутствие файла сами не пройдут.</summary>
    private static bool Retryable(FailureKind kind) => kind is FailureKind.StartFailed or FailureKind.Timeout or FailureKind.ExeBlocked;

    private static VerifyResult ProbeFailure(
        IReadOnlyList<FileProbe> probes, McpServerSpec spec, Func<FailureKind, string, int, HandshakeResult?, VerifyResult> fail)
    {
        if (probes.FirstOrDefault(p => p.State == ProbeState.Error) is { } bad)
            return fail(bad.ErrorKind, bad.Error ?? FailureText.Reason(bad.ErrorKind), 0, null);
        if (probes.FirstOrDefault(p => p.State == ProbeState.Foreign) is { } foreign)
            return fail(FailureKind.ConfigMissing,
                L.F("в файле {0} запись «{1}» принадлежит другой программе ({2})", foreign.Path, spec.Name, foreign.Entry?.Command ?? "—"), 0, null);
        if (probes.All(p => p.State == ProbeState.FileMissing))
            return fail(FailureKind.ConfigMissing, L.F("файл конфигурации не найден: {0}", probes[0].Path), 0, null);
        return fail(FailureKind.ConfigMissing, L.F("в файле {0} записи «{1}» нет", probes[0].Path, spec.Name), 0, null);
    }
}
