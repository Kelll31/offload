using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tests;

/// <summary>
/// Корни рабочей области в обеих ревизиях протокола: до 2026-07-28 — roots/list с кэшем на сессию; с 2026-07-28 — через MRTR
/// в каждом вызове (возможности клиента — из _meta запроса), без переноса между запросами.
/// </summary>
[Collection("AppPaths")]
public class RootsProtocolTests
{
    private const string ProbeTool = "roots_probe";

    /// <summary>Сервер Offload в процессе + тестовый инструмент, возвращающий корни (Workspace.GetRootsAsync) через «|».</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly Pipe _c2s = new();
        private readonly Pipe _s2c = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly ServiceProvider _sp;
        private readonly Task _serverTask;

        public McpClient Client { get; private set; } = null!;
        public SessionState State { get; } = new() { TrayLauncherOverride = () => false };
        public int RootsRequests;

        private Harness()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var state = State;
            McpEntry.AddOffloadServer(services, state)
                .WithTools([
                    McpServerTool.Create(async (McpServer server, CancellationToken ct) =>
                        string.Join("|", await Workspace.GetRootsAsync(server, state, ct)),
                        new McpServerToolCreateOptions { Name = ProbeTool }),
                ])
                .WithStreamServerTransport(_c2s.Reader.AsStream(), _s2c.Writer.AsStream());
            _sp = services.BuildServiceProvider();
            _serverTask = _sp.GetRequiredService<McpServer>().RunAsync(_cts.Token);
        }

        public static async Task<Harness> StartAsync(string protocol, Func<IReadOnlyList<string>>? roots)
        {
            var h = new Harness();
            var options = new McpClientOptions
            {
                ProtocolVersion = protocol,
                ClientInfo = new Implementation { Name = "roots-harness", Version = "1.0" },
            };
            if (roots is not null)
            {
                options.Capabilities = new ClientCapabilities { Roots = new RootsCapability { ListChanged = true } };
                options.Handlers.RootsHandler = (_, _) =>
                {
                    Interlocked.Increment(ref h.RootsRequests);
                    return ValueTask.FromResult(new ListRootsResult
                    {
                        Roots = [.. roots().Select((p, i) => new Root { Uri = new Uri(p + "\\").AbsoluteUri, Name = "r" + i })],
                    });
                };
            }
            h.Client = await McpClient.CreateAsync(new StreamClientTransport(h._c2s.Writer.AsStream(), h._s2c.Reader.AsStream()), options,
                cancellationToken: TestContext.Current.CancellationToken);
            return h;
        }

        public async Task<string[]> RootsAsync()
        {
            var res = await Client.CallToolAsync(ProbeTool, new Dictionary<string, object?>(), cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotEqual(true, res.IsError);
            var text = string.Join("", res.Content.OfType<TextContentBlock>().Select(t => t.Text));
            return text.Split('|', StringSplitOptions.RemoveEmptyEntries);
        }

        public async ValueTask DisposeAsync()
        {
            try { await Client.DisposeAsync(); } catch { }
            _cts.Cancel();
            _c2s.Writer.Complete();
            _s2c.Writer.Complete();
            try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            await _sp.DisposeAsync();
        }
    }

    private static string NewDir(string name)
    {
        var d = Path.Combine(Path.GetTempPath(), "pc-root-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return PathGuard.Canonicalize(d);
    }

    [Fact]
    public async Task LegacyProtocol_MultipleRoots_RequestedOnceAndCached()
    {
        using var env = new TestEnv();
        var second = NewDir("second");
        try
        {
            string[] current = [env.Workspace, second];
            await using var h = await Harness.StartAsync("2025-11-25", () => current);
            Assert.Equal([env.Workspace, second], await h.RootsAsync());
            current = [second];
            // До notifications/roots/list_changed корни берутся из кэша сессии.
            Assert.Equal([env.Workspace, second], await h.RootsAsync());
            Assert.Equal(1, h.RootsRequests);
        }
        finally
        {
            try { Directory.Delete(second, true); } catch { }
        }
    }

    [Fact]
    public async Task July2026Protocol_MultipleRoots_ViaMrtrOnEveryCall()
    {
        using var env = new TestEnv();
        var second = NewDir("second");
        var third = NewDir("third");
        try
        {
            string[] current = [env.Workspace, second];
            await using var h = await Harness.StartAsync("2026-07-28", () => current);
            Assert.Equal("2026-07-28", h.Client.NegotiatedProtocolVersion);
            Assert.Equal([env.Workspace, second], await h.RootsAsync());
            // В ревизии 2026-07-28 сведения о клиенте не переносятся между запросами: новые корни видны сразу.
            current = [third, env.Workspace];
            Assert.Equal([third, env.Workspace], await h.RootsAsync());
            Assert.Equal(2, h.RootsRequests);
            Assert.Null(h.State.TryGetCachedRoots(out var cached, out _) ? cached : null);
        }
        finally
        {
            try { Directory.Delete(second, true); } catch { }
            try { Directory.Delete(third, true); } catch { }
        }
    }

    [Theory]
    [InlineData("2025-11-25")]
    [InlineData("2026-07-28")]
    public async Task ClientWithoutRoots_FallsBackToProjectDir(string protocol)
    {
        using var env = new TestEnv();
        var previous = Environment.GetEnvironmentVariable(Workspace.ClaudeProjectDirEnv);
        Environment.SetEnvironmentVariable(Workspace.ClaudeProjectDirEnv, env.Workspace);
        try
        {
            await using var h = await Harness.StartAsync(protocol, roots: null);
            Assert.Equal([env.Workspace], await h.RootsAsync());
            Assert.Equal(0, h.RootsRequests);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Workspace.ClaudeProjectDirEnv, previous);
        }
    }

    [Fact]
    public async Task July2026Protocol_MissingAndDuplicateRootsAreDropped()
    {
        using var env = new TestEnv();
        var missing = Path.Combine(Path.GetTempPath(), "pc-root-missing-" + Guid.NewGuid().ToString("N"));
        await using var h = await Harness.StartAsync("2026-07-28", () => [missing, env.Workspace, env.Workspace.ToUpperInvariant()]);
        Assert.Equal([env.Workspace], await h.RootsAsync());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("2025-06-18", false)]
    [InlineData("2025-11-25", false)]
    [InlineData("2026-07-28", true)]
    [InlineData("2027-01-01", true)]
    public void PerRequestProtocol_ByRevisionDate(string? version, bool expected) =>
        Assert.Equal(expected, Workspace.IsPerRequestProtocol(version));
}
