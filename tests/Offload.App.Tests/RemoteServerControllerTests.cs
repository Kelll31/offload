using Offload.App.Services;
using Offload.Core.Config;
using Offload.Core.Security;
using Offload.Llama;

namespace Offload.App.Tests;

/// <summary>Клиентский режим в трее: сторож удалённого сервера (без процесса), готовность конфигурации, маскирование ключей.</summary>
public sealed class RemoteServerControllerTests
{
    private static ServerController.RemoteVerdict V(ServerState s, HealthState h, ref int failures)
    {
        (var verdict, failures) = ServerController.DecideRemote(s, h, failures);
        return verdict;
    }

    [Fact]
    public void Watchdog_RunningRemoteLost_FailsAfterThreshold_ThenRecovers()
    {
        var f = 0;
        Assert.Equal(ServerController.RemoteVerdict.None, V(ServerState.Running, HealthState.Ready, ref f));
        Assert.Equal(ServerController.RemoteVerdict.None, V(ServerState.Running, HealthState.Down, ref f));
        Assert.Equal(1, f);
        Assert.Equal(ServerController.RemoteVerdict.Down, V(ServerState.Running, HealthState.Down, ref f));
        Assert.Equal(0, f);
        // Уже Failed — повторные неответы без новых уведомлений.
        Assert.Equal(ServerController.RemoteVerdict.None, V(ServerState.Failed, HealthState.Down, ref f));
        Assert.Equal(ServerController.RemoteVerdict.Up, V(ServerState.Failed, HealthState.Ready, ref f));
        Assert.Equal(0, f);
    }

    [Fact]
    public void Watchdog_SingleMiss_DoesNotFail()
    {
        var f = 0;
        V(ServerState.Running, HealthState.Down, ref f);
        Assert.Equal(ServerController.RemoteVerdict.None, V(ServerState.Running, HealthState.Ready, ref f));
        Assert.Equal(0, f);
        Assert.Equal(ServerController.RemoteVerdict.None, V(ServerState.Running, HealthState.Down, ref f));
    }

    [Fact]
    public void Watchdog_LoadingRemote_StartingThenUp()
    {
        var f = 0;
        Assert.Equal(ServerController.RemoteVerdict.Loading, V(ServerState.Failed, HealthState.Loading, ref f));
        Assert.Equal(ServerController.RemoteVerdict.None, V(ServerState.Starting, HealthState.Loading, ref f));
        Assert.Equal(ServerController.RemoteVerdict.Up, V(ServerState.Starting, HealthState.Ready, ref f));
        // Сон модели на удалённом сервере (503 после готовности) — не сбой.
        Assert.Equal(ServerController.RemoteVerdict.None, V(ServerState.Running, HealthState.Loading, ref f));
    }

    [Fact]
    public void Watchdog_RemoteBackButKeyRejected_StaysFailed()
    {
        // /health отвечает без ключа, /props — 401: «снова доступен» не объявляется.
        Assert.Equal(ServerController.RemoteVerdict.KeyRejected, ServerController.ConfirmKey(ServerController.RemoteVerdict.Up, false));
        Assert.Equal(ServerController.RemoteVerdict.Up, ServerController.ConfirmKey(ServerController.RemoteVerdict.Up, true));
        // Сторонний сервер без /props — ключ не опровергнут.
        Assert.Equal(ServerController.RemoteVerdict.Up, ServerController.ConfirmKey(ServerController.RemoteVerdict.Up, null));
        Assert.Equal(ServerController.RemoteVerdict.None, ServerController.ConfirmKey(ServerController.RemoteVerdict.None, false));
    }

    [Fact]
    public void TunedProfile_KeptAfterSingleFailure_DroppedOnSecondOrOutOfMemory()
    {
        var t = new TunedFailureTracker();
        Assert.False(t.Failed("m", outOfMemory: false)); // разовый сбой — только обойти профиль
        Assert.True(t.Failed("m", outOfMemory: false));  // второй подряд — сбросить
        Assert.False(t.Failed("m", outOfMemory: false));
        t.Succeeded("m");                                 // запуск с профилем удался — счёт заново
        Assert.False(t.Failed("m", outOfMemory: false));
        Assert.False(t.Failed("other", outOfMemory: false)); // другая модель — свой счёт
        Assert.True(t.Failed("other", outOfMemory: true));   // явная нехватка памяти — сразу
    }

    [Fact]
    public void IsBusy_IncludesAutoTune()
    {
        // Автоподбор перезапускает процесс пробами, State при этом не меняется: автодополнение должно ждать.
        Assert.True(ServerController.BusyFor(ServerState.Running, tuning: true));
        Assert.True(ServerController.BusyFor(ServerState.Stopped, tuning: true));
        Assert.True(ServerController.BusyFor(ServerState.Starting, tuning: false));
        Assert.False(ServerController.BusyFor(ServerState.Running, tuning: false));
    }

    [Fact]
    public void CheckConfigured_Remote_NeedsOnlyKey()
    {
        var cfg = new AppConfig();
        cfg.Remote.Enabled = true;
        cfg.Remote.Url = "http://192.168.1.10:8765";
        Assert.NotNull(ServerController.CheckConfigured(cfg)); // ключа нет
        cfg.Remote.ApiKeyProtected = Dpapi.Protect("olan-key");
        // Ни llama.cpp, ни модели на этом компьютере не нужно.
        Assert.Null(ServerController.CheckConfigured(cfg));
    }

    [Fact]
    public void DiagnosticsSecrets_IncludeLanAndRemoteKeys()
    {
        var cfg = new AppConfig();
        cfg.Server.ApiKey = "pc-local";
        cfg.Autocomplete.ApiKey = "fim-local";
        cfg.Server.LanApiKeyProtected = Dpapi.Protect("olan-host-key");
        cfg.Remote.ApiKeyProtected = Dpapi.Protect("olan-remote-key");
        var secrets = DiagnosticsBundle.SecretsOf(cfg);
        Assert.Contains("olan-host-key", secrets);
        Assert.Contains("olan-remote-key", secrets);
        Assert.Contains("pc-local", secrets);
        Assert.Contains("fim-local", secrets);
    }
}
