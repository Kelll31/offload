using Offload.App.Services;
using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Integrations;
using Offload.Llama;
using Offload.Models;

namespace Offload.App.Tests;

/// <summary>Сервер автодополнения (роль fim): чистая логика сверки с настройками, оценка памяти, адрес для IDE.</summary>
public sealed class AutocompletePolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
    private const string FimId = "qwen2.5-coder-1.5b-fim-q8";

    private static AppConfig Config(bool enabled = true, bool cpuOnly = false, bool assigned = true, string main = "qwen3.8-27b-q4")
    {
        var cfg = new AppConfig();
        foreach (var (id, kind) in new[] { (main, ModelKind.Chat), (FimId, ModelKind.Fim) })
        {
            var c = ModelCatalog.Find(id)!;
            cfg.Models.Installed.Add(new InstalledModel
            {
                Id = id, DisplayName = id, FilePath = id + ".gguf", SizeBytes = c.ApproxSizeBytes, Kind = kind,
                NativeContext = c.NativeContext, RecommendedContext = c.DefaultContext, Architecture = c.Architecture, IsMoe = c.IsMoe,
            });
        }
        cfg.Models.ActiveModelId = main;
        if (assigned) cfg.Models.Roles.Fim = FimId;
        cfg.Autocomplete.Enabled = enabled;
        cfg.Autocomplete.CpuOnly = cpuOnly;
        cfg.Server.ApiKey = "pc-secret";
        cfg.Autocomplete.ApiKey = "fim-secret";
        return cfg;
    }

    private static HardwareInfo Hw(int vramGb, int ramGb = 64) => new(
        vramGb > 0 ? [new GpuInfo("GPU", GpuVendor.Nvidia, (long)vramGb << 30, false)] : [],
        (long)ramGb << 30, (long)ramGb << 29, "CPU", 16, true, false);

    [Fact]
    public void LaunchKey_NullWhenDisabledOrUnassigned_IncludesCpuMode()
    {
        Assert.Null(AutocompletePolicy.LaunchKey(Config(enabled: false)));
        Assert.Null(AutocompletePolicy.LaunchKey(Config(assigned: false)));
        Assert.Equal(FimId, AutocompletePolicy.LaunchKey(Config()));
        Assert.Equal(FimId + "|cpu", AutocompletePolicy.LaunchKey(Config(cpuOnly: true)));
    }

    [Fact]
    public void Decide_StartStopRestart()
    {
        Assert.Equal(AutocompleteAction.Start, AutocompletePolicy.Decide("m", null, ServerState.Stopped, null, null, Now, deferStart: false));
        Assert.Equal(AutocompleteAction.Start, AutocompletePolicy.Decide("m", null, ServerState.Failed, null, null, Now, deferStart: false));
        Assert.Equal(AutocompleteAction.None, AutocompletePolicy.Decide("m", "M", ServerState.Running, null, null, Now, deferStart: false));
        Assert.Equal(AutocompleteAction.None, AutocompletePolicy.Decide("m", "m", ServerState.Starting, null, null, Now, deferStart: false));
        // Сменилась модель или режим «только процессор» — перезапуск.
        Assert.Equal(AutocompleteAction.Restart, AutocompletePolicy.Decide("m|cpu", "m", ServerState.Running, null, null, Now, deferStart: false));
        // Выключено — остановить работающий; остановленный не трогать.
        Assert.Equal(AutocompleteAction.Stop, AutocompletePolicy.Decide(null, "m", ServerState.Running, null, null, Now, deferStart: false));
        Assert.Equal(AutocompleteAction.Stop, AutocompletePolicy.Decide(null, "m", ServerState.Starting, null, null, Now, deferStart: false));
        Assert.Equal(AutocompleteAction.None, AutocompletePolicy.Decide(null, null, ServerState.Stopped, null, null, Now, deferStart: false));
        Assert.Equal(AutocompleteAction.None, AutocompletePolicy.Decide(null, null, ServerState.Failed, null, null, Now, deferStart: false));
    }

    [Fact]
    public void Decide_CooldownAfterFailure_DeferWhileMainBusy()
    {
        var failed = Now.AddSeconds(-30);
        Assert.Equal(AutocompleteAction.CoolingDown, AutocompletePolicy.Decide("m", null, ServerState.Failed, "m", failed, Now, deferStart: false));
        // Другая конфигурация или пауза прошла — пробуем снова.
        Assert.Equal(AutocompleteAction.Start, AutocompletePolicy.Decide("m|cpu", null, ServerState.Failed, "m", failed, Now, deferStart: false));
        Assert.Equal(AutocompleteAction.Start,
            AutocompletePolicy.Decide("m", null, ServerState.Failed, "m", Now - AuxServerPolicy.FailureCooldown - TimeSpan.FromSeconds(1), Now, deferStart: false));
        // Основной сервер запускается — видеопамять распределяется по очереди.
        Assert.Equal(AutocompleteAction.Deferred, AutocompletePolicy.Decide("m", null, ServerState.Stopped, null, null, Now, deferStart: true));
        Assert.Equal(AutocompleteAction.None, AutocompletePolicy.Decide("m", "m", ServerState.Running, null, null, Now, deferStart: true));
    }

    [Fact]
    public void Endpoint_DefaultPortOrRunningAddress_KeyAndModel()
    {
        var cfg = Config();
        var e = AutocompletePolicy.Endpoint(cfg, null)!;
        // Ключ — свой ключ автодополнения: основной ключ в файлы IDE не попадает.
        Assert.Equal(("http://127.0.0.1:8012", "fim-secret", FimId), (e.BaseUrl, e.ApiKey, e.ModelName));
        Assert.Equal("http://127.0.0.1:8013", AutocompletePolicy.Endpoint(cfg, "http://127.0.0.1:8013/")!.BaseUrl);
        cfg.Server.AuxPorts["fim"] = 8020;
        Assert.Equal("http://127.0.0.1:8020", AutocompletePolicy.Endpoint(cfg, null)!.BaseUrl);
        Assert.Null(AutocompletePolicy.Endpoint(Config(assigned: false), null));
    }

    [Fact]
    public void DecideContinue_WritesOnlyRunningServer_WithFimKey()
    {
        var cfg = Config();
        FimEndpoint? asked = null;
        AutocompleteTargetState Status(FimEndpoint? e, AutocompleteTargetState s)
        {
            asked = e;
            return s;
        }

        // Сервер не запущен (отложен, упал) — порт по умолчанию мог занять чужой процесс: блок не пишется.
        Assert.Equal(ContinueAction.None, AutocompletePolicy.DecideContinue(cfg, null, e => Status(e, AutocompleteTargetState.NotConfigured), out var want));
        Assert.Null(want);
        Assert.Equal(ContinueAction.None, AutocompletePolicy.DecideContinue(cfg, null, e => Status(e, AutocompleteTargetState.Outdated), out _));

        // Работает — адрес процесса и ключ автодополнения.
        Assert.Equal(ContinueAction.Apply, AutocompletePolicy.DecideContinue(cfg, "http://127.0.0.1:8013", e => Status(e, AutocompleteTargetState.NotConfigured), out want));
        Assert.Equal(("http://127.0.0.1:8013", "fim-secret"), (want!.BaseUrl, want.ApiKey));
        Assert.Equal(want, asked);
        Assert.Equal(ContinueAction.None, AutocompletePolicy.DecideContinue(cfg, "http://127.0.0.1:8013", e => Status(e, AutocompleteTargetState.Configured), out _));

        // Выключено — наш блок убирается.
        Assert.Equal(ContinueAction.Remove, AutocompletePolicy.DecideContinue(Config(enabled: false), null, e => Status(e, AutocompleteTargetState.Configured), out _));
        Assert.Equal(ContinueAction.None, AutocompletePolicy.DecideContinue(Config(enabled: false), null, e => Status(e, AutocompleteTargetState.Foreign), out _));
    }

    [Fact]
    public void AssessFit_GpuCpuAndNotBesideMain()
    {
        RoleBudgetResult Budget(AppConfig cfg, HardwareInfo hw) => RoleBudget.Evaluate(cfg, hw, ModelCatalog.All, includeFim: true);

        Assert.Equal(FimFit.Gpu, AutocompletePolicy.AssessFit(Budget(Config(), Hw(48)), cpuOnly: false));
        // 20 ГБ: основная 27B в режиме «Авто» занимает почти всю карту — модель автодополнения рядом не помещается.
        Assert.Equal(FimFit.NotBesideMain, AutocompletePolicy.AssessFit(Budget(Config(), Hw(20)), cpuOnly: false));
        Assert.Equal(FimFit.Cpu, AutocompletePolicy.AssessFit(Budget(Config(cpuOnly: true), Hw(20)), cpuOnly: true));
        Assert.Equal(FimFit.Cpu, AutocompletePolicy.AssessFit(Budget(Config(), Hw(0)), cpuOnly: false));
        Assert.Equal(FimFit.Unknown, AutocompletePolicy.AssessFit(Budget(Config(assigned: false), Hw(48)), cpuOnly: false));
        Assert.Equal(FimFit.Unknown, AutocompletePolicy.AssessFit(null, cpuOnly: false));
    }

    [Fact]
    public void Suggested_FirstCatalogFimModel_OnlyWhenNoneInstalled()
    {
        var none = Config(assigned: false);
        none.Models.Installed.RemoveAll(m => m.Kind == ModelKind.Fim);
        var s = AutocompletePolicy.Suggested(ModelCatalog.All, none);
        Assert.Equal(FimId, s?.Id); // 1.5B (Apache-2.0) — первая по приоритету
        Assert.Null(AutocompletePolicy.Suggested(ModelCatalog.All, Config()));
    }
}
