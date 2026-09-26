using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Offload.Core.Logging;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Уведомления notifications/progress: значение строго растёт, отправка последовательная (в порядке вызовов).
/// total — только когда число шагов известно (<see cref="Step"/>: части map-reduce, этапы); иначе без total.
/// Каждое уведомление сбрасывает сторожевой таймер простоя IDE. Предупреждения — notifications/message (<see cref="Warn"/>).
/// </summary>
internal sealed class ProgressReporter
{
    private readonly McpServer? _server;
    private readonly ProgressToken? _token;
    private readonly object _lock = new();
    private Task _chain = Task.CompletedTask;
    private float _value;
    private string? _last;
    private DateTime _lastSentUtc = DateTime.MinValue;

    /// <summary>Единиц прогресса на шаг: промежуточные сообщения (пульс) внутри шага прибавляют по одной, не выходя за шаг.</summary>
    internal const int UnitsPerStep = 1000;

    private int? _total;
    private int _step;
    private int _sub;

    public ProgressReporter(McpServer? server, ProgressToken? token)
    {
        _server = server;
        _token = token;
    }

    /// <summary>Для тестов: все отправленные сообщения.</summary>
    public List<string> History { get; } = [];

    public bool Enabled => _server is not null && _token is not null;

    /// <summary>Последнее сообщение (для состояния фоновых задач).</summary>
    public string? LastMessage
    {
        get
        {
            lock (_lock) return History.Count > 0 ? History[^1] : null;
        }
    }

    public void Report(string message) => Send(message, step: null, total: null);

    /// <summary>
    /// Шаг с известным общим числом: done из total выполнено, начинается следующий. Следующие Report (пульс модели)
    /// остаются внутри шага. Значение по-прежнему строго растёт (на шкале total×<see cref="UnitsPerStep"/>).
    /// </summary>
    public void Step(int done, int total, string message)
    {
        if (total <= 0) Report(message);
        else Send(message, Math.Clamp(done, 0, total), total);
    }

    /// <summary>Шаги закончились — дальше снова без total.</summary>
    public void EndSteps()
    {
        lock (_lock) _total = null;
    }

    private void Send(string message, int? step, int? total)
    {
        lock (_lock)
        {
            History.Add(message);
            if (History.Count > 200) History.RemoveAt(0);
            if (step is int st && total is int tt)
            {
                _total = tt;
                _step = st;
                _sub = 0;
            }
            if (!Enabled) return;
            // Одинаковые сообщения чаще раза в 2 с не шлём (кроме пульса — он всегда с новым текстом).
            if (step is null && message == _last && DateTime.UtcNow - _lastSentUtc < TimeSpan.FromSeconds(2)) return;
            float? totalUnits = null;
            if (_total is int t)
            {
                // Внутри шага — не дальше его конца; значение не уменьшается, даже если шаги пришли не по порядку.
                if (step is null && _sub < UnitsPerStep - 1) _sub++;
                var target = (float)_step * UnitsPerStep + _sub;
                _value = Math.Max(_value + 1, target);
                totalUnits = Math.Max((float)t * UnitsPerStep, _value);
            }
            else
            {
                _value += 1;
            }
            _last = message;
            _lastSentUtc = DateTime.UtcNow;
            var value = new ProgressNotificationValue { Progress = _value, Total = totalUnits, Message = message };
            var token = _token!.Value;
            Enqueue((server, ct) => server.NotifyProgressAsync(token, value, cancellationToken: ct), "progress");
        }
    }

    /// <summary>Ревизия протокола, в которой logging (notifications/message) объявлен устаревшим (SEP-2577).</summary>
    internal const string LoggingDeprecatedSince = "2026-07-28";

    /// <summary>
    /// Предупреждение пользователю IDE (notifications/message, уровень warning): модель выгружена, сервер перезапускается и т.п.
    /// Не зависит от progressToken. Не отправляется, если клиент задал уровень журнала выше warning (logging/setLevel)
    /// или договорился о ревизии протокола 2026-07-28+, где logging устарел (там остаётся только прогресс).
    /// </summary>
    public void Warn(string message)
    {
#pragma warning disable MCP9005 // logging устарел в 2026-07-28; для более ранних ревизий (Claude Code, VS Code) ещё действует
        lock (_lock)
        {
            if (_server is null) return;
            if (_server.NegotiatedProtocolVersion is { } v && string.CompareOrdinal(v, LoggingDeprecatedSince) >= 0) return;
            if (_server.LoggingLevel is { } min && LoggingLevel.Warning < min) return;
            var p = new LoggingMessageNotificationParams
            {
                Level = LoggingLevel.Warning,
                Logger = "offload",
                Data = JsonSerializer.SerializeToElement(message, McpJsonUtilities.DefaultOptions),
            };
            Enqueue((server, ct) => server.SendNotificationAsync(NotificationMethods.LoggingMessageNotification, p, McpJsonUtilities.DefaultOptions, ct),
                "message");
        }
#pragma warning restore MCP9005
    }

    /// <summary>Отправка по цепочке — строго в порядке вызовов, с таймаутом; ошибки только в журнал.</summary>
    private void Enqueue(Func<McpServer, CancellationToken, Task> send, string what)
    {
        var server = _server!;
        _chain = _chain.ContinueWith(
            async _ =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await send(server, cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Debug("mcp", $"{what} не отправлен: {ex.Message}");
                }
            },
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    /// <summary>Дождаться отправки всех уведомлений (перед возвратом результата — чтобы прогресс не пришёл после ответа).</summary>
    public async Task FlushAsync()
    {
        Task chain;
        lock (_lock) chain = _chain;
        try { await chain.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch { }
    }

    /// <summary>
    /// Периодический «пульс» (каждые interval) пока идёт долгая операция. Текст берётся из messageFactory.
    /// </summary>
    public IAsyncDisposable StartHeartbeat(Func<string> messageFactory, TimeSpan? interval = null)
    {
        var cts = new CancellationTokenSource();
        var period = interval ?? TimeSpan.FromSeconds(5);
        var task = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(period, cts.Token).ConfigureAwait(false);
                    string msg;
                    try { msg = messageFactory(); }
                    catch { continue; }
                    Report(msg);
                }
            }
            catch (OperationCanceledException)
            {
                // Остановлен.
            }
        });
        return new Heartbeat(cts, task);
    }

    private sealed class Heartbeat(CancellationTokenSource cts, Task task) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            cts.Cancel();
            try { await task.ConfigureAwait(false); } catch { }
            cts.Dispose();
        }
    }
}
