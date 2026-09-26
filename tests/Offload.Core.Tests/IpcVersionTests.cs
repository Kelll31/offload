using System.IO.Pipes;
using System.Text.Json;
using Offload.Core.Ipc;
using Offload.Core.Util;

namespace Offload.Core.Tests;

/// <summary>Версия протокола IPC: клиент отправляет «v», запрос без «v» — v1, неизвестная мажорная версия отклоняется.</summary>
[Collection("AppPaths")]
public class IpcVersionTests
{
    [Fact]
    public void Request_WithoutV_IsVersion1()
    {
        var req = JsonSerializer.Deserialize<IpcRequest>("{\"command\":\"ping\"}", Json.Compact)!;

        Assert.Null(req.V);
        Assert.Equal(1, IpcProtocol.VersionOf(req));
        Assert.Null(IpcProtocol.CheckVersion(req));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public void CheckVersion_UnknownMajor_Rejected(int v)
    {
        var resp = IpcProtocol.CheckVersion(new IpcRequest(IpcCommands.Status, null, v));

        Assert.NotNull(resp);
        Assert.False(resp!.Ok);
        Assert.Equal("unsupported-version", resp.Data!["error"]);
        Assert.Equal(IpcProtocol.Version, resp.V);
        Assert.Contains(v.ToString(System.Globalization.CultureInfo.InvariantCulture), resp.Message);
    }

    [Fact]
    public void Serialize_UsesShortFieldV()
    {
        var json = JsonSerializer.Serialize(new IpcRequest(IpcCommands.Ping, null, 1), Json.Compact);

        Assert.Contains("\"v\":1", json);
    }

    [Fact]
    public async Task Client_SendsCurrentVersion_ServerAnswersWithVersion()
    {
        using var home = new TempHome();
        IpcRequest? seen = null;
        using var server = new IpcServer(req =>
        {
            seen = req;
            return Task.FromResult(new IpcResponse(true, "pong"));
        });
        server.Start();

        var resp = await SendWithRetryAsync(new IpcRequest(IpcCommands.Ping));

        Assert.NotNull(resp);
        Assert.True(resp!.Ok);
        Assert.Equal(IpcProtocol.Version, resp.V);
        Assert.Equal(IpcProtocol.Version, seen?.V);
    }

    [Fact]
    public async Task Server_OldClientWithoutV_Handled_UnknownVersion_RejectedBeforeHandler()
    {
        using var home = new TempHome();
        var handled = new List<string>();
        using var server = new IpcServer(req =>
        {
            lock (handled) handled.Add(req.Command);
            return Task.FromResult(new IpcResponse(true));
        });
        server.Start();

        var old = await RawAsync("{\"command\":\"ping\"}");
        var future = await RawAsync("{\"command\":\"stop-server\",\"v\":7}");

        Assert.True(old!.Ok);
        Assert.False(future!.Ok);
        Assert.Equal("unsupported-version", future.Data!["error"]);
        Assert.Equal(["ping"], handled);
    }

    private static async Task<IpcResponse?> SendWithRetryAsync(IpcRequest req)
    {
        IpcResponse? resp = null;
        for (var i = 0; i < 20 && resp is null; i++)
        {
            resp = await IpcClient.SendAsync(req, TimeSpan.FromSeconds(2));
            if (resp is null) await Task.Delay(100);
        }
        return resp;
    }

    /// <summary>Клиент «как старая версия»: пишет строку JSON как есть, без добавления «v».</summary>
    private static async Task<IpcResponse?> RawAsync(string line)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", IpcNames.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(2000, TestContext.Current.CancellationToken);
                using var reader = new StreamReader(pipe, FileUtil.Utf8NoBom, false, 4096, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, FileUtil.Utf8NoBom, 4096, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(line);
                var answer = await reader.ReadLineAsync(TestContext.Current.CancellationToken);
                return answer is null ? null : JsonSerializer.Deserialize<IpcResponse>(answer, Json.Compact);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }
        return null;
    }
}
