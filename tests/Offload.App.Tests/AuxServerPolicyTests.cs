using Offload.App.Services;
using Offload.Core.Config;
using Offload.Llama;

namespace Offload.App.Tests;

/// <summary>Жизненный цикл вспомогательных серверов ролей (ROADMAP §5.2): чистая логика решений.</summary>
public sealed class AuxServerPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Decide_NotAssigned()
    {
        Assert.Equal(AuxAction.NotAssigned, AuxServerPolicy.Decide(null, "m", ServerState.Running, null, null, Now));
        Assert.Equal(AuxAction.NotAssigned, AuxServerPolicy.Decide(" ", null, ServerState.Stopped, null, null, Now));
    }

    [Fact]
    public void Decide_RunningSameModel_Reuse_OtherModel_Restart()
    {
        Assert.Equal(AuxAction.UseRunning, AuxServerPolicy.Decide("m", "M", ServerState.Running, null, null, Now));
        Assert.Equal(AuxAction.Restart, AuxServerPolicy.Decide("m2", "m", ServerState.Running, null, null, Now));
        Assert.Equal(AuxAction.Restart, AuxServerPolicy.Decide("m2", "m", ServerState.Starting, null, null, Now));
    }

    [Theory]
    [InlineData(ServerState.Stopped)]
    [InlineData(ServerState.Failed)]
    [InlineData(ServerState.NotConfigured)]
    public void Decide_NotRunning_Start(ServerState state)
    {
        Assert.Equal(AuxAction.Start, AuxServerPolicy.Decide("m", null, state, null, null, Now));
    }

    [Fact]
    public void Decide_RecentFailureOfSameModel_CoolsDown_ThenRetries()
    {
        var justNow = Now - TimeSpan.FromSeconds(10);
        Assert.Equal(AuxAction.CoolingDown, AuxServerPolicy.Decide("m", null, ServerState.Stopped, "m", justNow, Now));
        // Назначена другая модель — пауза прежней её не касается.
        Assert.Equal(AuxAction.Start, AuxServerPolicy.Decide("m2", null, ServerState.Stopped, "m", justNow, Now));
        // Пауза прошла.
        var old = Now - AuxServerPolicy.FailureCooldown - TimeSpan.FromSeconds(1);
        Assert.Equal(AuxAction.Start, AuxServerPolicy.Decide("m", null, ServerState.Stopped, "m", old, Now));
    }

    [Fact]
    public void ToStop_RolesWhoseModelChangedOrWasUnassigned()
    {
        var cfg = new AppConfig();
        cfg.Models.Installed.AddRange([
            new InstalledModel { Id = "chat" },
            new InstalledModel { Id = "small" },
            new InstalledModel { Id = "small2" },
            new InstalledModel { Id = "emb", Kind = ModelKind.Embed },
        ]);
        cfg.Models.ActiveModelId = "chat";
        cfg.Models.Roles = new ModelRoles { Fast = "small2", Embed = "emb" };

        var running = new Dictionary<ModelRole, string?>
        {
            [ModelRole.Fast] = "small",   // назначение сменилось
            [ModelRole.Embed] = "EMB",    // то же (без учёта регистра)
            [ModelRole.Rerank] = "old",   // роль снята
        };

        Assert.Equal([ModelRole.Fast, ModelRole.Rerank], AuxServerPolicy.ToStop(cfg, running));
        Assert.Empty(AuxServerPolicy.ToStop(cfg, new Dictionary<ModelRole, string?> { [ModelRole.Fast] = null }));
    }
}
