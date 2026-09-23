using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Offload.Core.Logging;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Уведомления notifications/progress: значение строго растёт, отправка последовательная (в порядке вызовов),
/// без total (длительность заранее неизвестна). Каждое уведомление сбрасывает сторожевой таймер простоя IDE.
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

    public void Report(string message)
    {
        lock (_lock)
        {
            History.Add(message);
            if (History.Count > 200) History.RemoveAt(0);
            if (!Enabled) return;
            // Одинаковые сообщения чаще раза в 2 с не шлём (кроме пульса — он всегда с новым текстом).
            if (message == _last && DateTime.UtcNow - _lastSentUtc < TimeSpan.FromSeconds(2)) return;
            _value += 1;
            _last = message;
            _lastSentUtc = DateTime.UtcNow;
            var value = new ProgressNotificationValue { Progress = _value, Message = message };
            var server = _server!;
            var token = _token!.Value;
            _chain = _chain.ContinueWith(
                async _ =>
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await server.NotifyProgressAsync(token, value, cancellationToken: cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("mcp", $"progress не отправлен: {ex.Message}");
                    }
                },
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
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
