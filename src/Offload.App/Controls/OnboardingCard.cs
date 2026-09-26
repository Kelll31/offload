using Offload.App.Services;
using Offload.App.Util;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Usage;
using Offload.Llama;

namespace Offload.App.Controls;

/// <summary>
/// Карточка «первого запуска» (после мастера и на «Состоянии», пока статистика пуста): ожидание первого вызова из IDE —
/// по первой записи статистики от MCP-процесса (IPC record-usage) превращается в «Claude Code подключён ✓»;
/// кнопка «Отправить тестовый запрос» один раз обращается к локальной модели и показывает ответ и скорость.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Controls belong to Card and are disposed with it")]
internal sealed class OnboardingCard
{
    private readonly IAppShell _shell;
    private readonly StatusDot _dot = new(12);
    private readonly Label _state = Kit.Label("", Theme.Semibold(10.5f));
    private readonly Label _hint = Kit.Wrap("", color: Theme.TextMuted);
    private readonly Button _test;
    private readonly Label _result = Kit.Wrap("", color: Theme.TextMuted);
    private bool _testing;

    public OnboardingCard(IAppShell shell)
    {
        _shell = shell;
        Card = new CardPanel { ColumnCount = 1, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(20, 14, 20, 14) };
        Card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Card.AccessibleRole = AccessibleRole.Grouping;
        Card.AccessibleName = L.T("Проверка подключения IDE");

        var head = Kit.Flow(_dot, _state);
        head.WrapContents = false;
        head.Margin = new Padding(0);
        _dot.Margin = new Padding(0, 6, 8, 2);
        Card.AddRow(head);
        _hint.Margin = new Padding(20, 2, 0, 6);
        Card.AddRow(_hint);

        _test = Kit.Button(L.T("Отправить тестовый запрос"), async (_, _) => await TestAsync(), 200);
        var actions = Kit.Flow(_test, Kit.Button(L.T("Подключение к IDE…"), (_, _) => _shell.ShowMainWindow(Tabs.Integrations), 170));
        actions.Margin = new Padding(20, 0, 0, 0);
        Card.AddRow(actions);
        _result.Margin = new Padding(20, 6, 0, 0);
        _result.Visible = false;
        Card.AddRow(_result);

        _shell.UsageRecorded += OnUsageRecorded;
        Card.Disposed += (_, _) => _shell.UsageRecorded -= OnUsageRecorded;
        SetWaiting();
    }

    public CardPanel Card { get; }

    /// <summary>Первый вызов из IDE получен.</summary>
    public bool Connected { get; private set; }

    /// <summary>Поднимается при первом вызове из IDE (в потоке интерфейса).</summary>
    public event EventHandler? ConnectedChanged;

    /// <summary>Есть ли хоть одна запись статистики (архивы или текущий файл). Быстрая проверка по файлам, без чтения.</summary>
    public static bool HasUsageHistory()
    {
        try
        {
            if (UsageLog.ArchiveFiles().Count > 0) return true;
            var f = new FileInfo(AppPaths.UsageFile);
            return f.Exists && f.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Имя клиента MCP для показа: claude-code → Claude Code; неизвестный — «IDE».</summary>
    internal static string DisplayClient(string? client)
    {
        if (string.IsNullOrWhiteSpace(client) || client == UsageLog.UnknownClient) return "IDE";
        var c = client.Trim();
        if (c.Contains("claude-code", StringComparison.OrdinalIgnoreCase) || c.Equals("claude code", StringComparison.OrdinalIgnoreCase)) return "Claude Code";
        if (c.Contains("claude-ai", StringComparison.OrdinalIgnoreCase)) return "Claude Desktop";
        if (c.Contains("cursor", StringComparison.OrdinalIgnoreCase)) return "Cursor";
        if (c.Contains("visual studio code", StringComparison.OrdinalIgnoreCase)) return "VS Code";
        return c;
    }

    private void SetWaiting()
    {
        _dot.DotColor = Theme.Amber;
        _dot.AccessibleName = L.T("Ожидание");
        _state.Text = L.T("Ожидание первого вызова из IDE…");
        _state.ForeColor = Theme.TextPrimary;
        _hint.Text = L.T("Перезапустите Claude Code (или другую подключённую IDE) и попросите: «используй offload, чтобы …». Как только IDE обратится к Offload, здесь появится отметка. Проверить саму модель можно тестовым запросом.");
    }

    private void OnUsageRecorded(object? sender, UsageRecord r)
    {
        if (Connected || Card.IsDisposed) return;
        Connected = true;
        _dot.DotColor = Theme.Green;
        _dot.AccessibleName = L.T("Подключено");
        _state.Text = L.F("{0} подключён ✓", DisplayClient(r.Client));
        _state.ForeColor = Theme.OkText;
        _hint.Text = L.F("Первый вызов: {0} в {1:HH:mm:ss}. Статистика появится в разделе «Состояние».",
            Texts.ToolName(r.Tool), r.TimestampUtc.ToLocalTime());
        ConnectedChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task TestAsync()
    {
        if (_testing) return;
        _testing = true;
        _test.Enabled = false;
        _result.Visible = true;
        _result.ForeColor = Theme.TextMuted;
        var owner = Card.FindForm();
        try
        {
            await Ui.RunSafeAsync(owner, async () =>
            {
                var server = _shell.Server;
                if (server.State != ServerState.Running)
                {
                    _result.Text = L.T("Запускаю сервер…");
                    if (!await server.StartAsync())
                    {
                        _result.ForeColor = Theme.ErrorText;
                        _result.Text = server.LastError ?? L.T("Сервер llama.cpp не запустился");
                        return;
                    }
                }
                _result.Text = L.T("Отправляю тестовый запрос модели…");
                server.MarkActivity();
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                try
                {
                    var result = await ModelCheck.RunAsync(ConfigStore.Current, cts.Token);
                    if (Card.IsDisposed) return;
                    _result.ForeColor = Theme.OkText;
                    _result.Text = ModelCheck.Describe(result);
                }
                catch (OperationCanceledException)
                {
                    _result.ForeColor = Theme.ErrorText;
                    _result.Text = L.T("Модель не ответила за 3 минуты.");
                }
            }, L.T("Не удалось проверить модель"));
        }
        finally
        {
            _testing = false;
            if (!_test.IsDisposed) _test.Enabled = true;
        }
    }
}
