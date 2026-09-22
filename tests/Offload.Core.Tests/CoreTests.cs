using System.Net;
using System.Net.Sockets;
using System.Text;
using Offload.Core.Config;
using Offload.Core.Ipc;
using Offload.Core.Net;
using Offload.Core.Usage;
using Offload.Core.Util;

namespace Offload.Core.Tests;

[Collection("AppPaths")]
public class ConfigTests
{
    [Fact]
    public void Reload_CreatesDefaultsWithApiKey()
    {
        using var home = new TempHome();
        var cfg = ConfigStore.Reload();
        Assert.StartsWith("pc-", cfg.Server.ApiKey);
        Assert.Equal(8765, cfg.Server.Port);
        Assert.True(File.Exists(AppPaths.ConfigFile));
        Assert.Equal(AppPaths.DefaultModelsDir, cfg.Models.ModelsDir);
    }

    [Fact]
    public void Save_RoundTripsAndKeepsCyrillic()
    {
        using var home = new TempHome();
        ConfigStore.Reload();
        ConfigStore.Update(c =>
        {
            c.Mcp.ExtraSystemPrompt = "Пиши на Delphi";
            c.Llama.InstalledBackend = LlamaBackend.Cuda13;
        });
        var text = File.ReadAllText(AppPaths.ConfigFile);
        Assert.Contains("Пиши на Delphi", text);
        Assert.Contains("\"cuda13\"", text);
        var again = ConfigStore.Reload();
        Assert.Equal(LlamaBackend.Cuda13, again.Llama.InstalledBackend);
    }

    [Fact]
    public void CorruptConfig_FallsBackToDefaults()
    {
        using var home = new TempHome();
        File.WriteAllText(AppPaths.ConfigFile, "{ this is not json");
        var cfg = ConfigStore.Reload();
        Assert.False(cfg.SetupCompleted);
        Assert.NotEmpty(cfg.Server.ApiKey);
    }
}

public class DownloaderTests
{
    private static (HttpListener Listener, string Url) Serve(byte[] data, bool supportRange, int failAfterBytes = -1)
    {
        var port = FreePort();
        var listener = new HttpListener();
        var url = $"http://127.0.0.1:{port}/file.bin";
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var served = 0;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                var start = 0;
                var range = ctx.Request.Headers["Range"];
                if (supportRange && range is not null && range.StartsWith("bytes="))
                {
                    start = int.Parse(range[6..].TrimEnd('-'));
                    ctx.Response.StatusCode = 206;
                    ctx.Response.AddHeader("Content-Range", $"bytes {start}-{data.Length - 1}/{data.Length}");
                }
                ctx.Response.ContentLength64 = data.Length - start;
                try
                {
                    var toSend = data.Length - start;
                    if (failAfterBytes > 0 && served++ == 0) toSend = Math.Min(toSend, failAfterBytes);
                    await ctx.Response.OutputStream.WriteAsync(data.AsMemory(start, toSend));
                    if (toSend < data.Length - start)
                    {
                        ctx.Response.Abort();
                        continue;
                    }
                    ctx.Response.Close();
                }
                catch
                {
                    // Клиент оборвал соединение.
                }
            }
        });
        return (listener, url);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "pc-dl-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Download_VerifiesSha()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Привет, llama! ", 50000)));
        var (listener, url) = Serve(data, supportRange: true);
        using var _ = listener;
        var dir = TempDir();
        var dest = Path.Combine(dir, "f.bin");
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
        await HttpDownloader.DownloadFileAsync(url, dest, data.Length, sha);
        Assert.Equal(data, await File.ReadAllBytesAsync(dest));
        Assert.False(File.Exists(dest + ".part"));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Download_ResumesAfterConnectionDrop()
    {
        var data = new byte[3 * 1024 * 1024];
        new Random(42).NextBytes(data);
        var (listener, url) = Serve(data, supportRange: true, failAfterBytes: 1024 * 1024);
        using var _ = listener;
        var dir = TempDir();
        var dest = Path.Combine(dir, "f.bin");
        await HttpDownloader.DownloadFileAsync(url, dest, data.Length);
        Assert.Equal(data, await File.ReadAllBytesAsync(dest));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Download_WrongSha_Throws()
    {
        var data = new byte[1000];
        var (listener, url) = Serve(data, supportRange: false);
        using var _ = listener;
        var dir = TempDir();
        await Assert.ThrowsAsync<DownloadException>(() =>
            HttpDownloader.DownloadFileAsync(url, Path.Combine(dir, "f.bin"), 1000, new string('0', 64)));
        Directory.Delete(dir, true);
    }
}

[Collection("AppPaths")]
public class IpcAndUsageTests
{
    [Fact]
    public async Task Ipc_RoundTrip()
    {
        using var home = new TempHome();
        using var server = new IpcServer(req => Task.FromResult(new IpcResponse(true, "ответ:" + req.Command)));
        server.Start();
        IpcResponse? resp = null;
        for (var i = 0; i < 20 && resp is null; i++)
        {
            resp = await IpcClient.SendAsync(new IpcRequest(IpcCommands.Ping), TimeSpan.FromSeconds(2));
            if (resp is null) await Task.Delay(100);
        }
        Assert.NotNull(resp);
        Assert.True(resp!.Ok);
        Assert.Equal("ответ:ping", resp.Message);
    }

    [Fact]
    public void Usage_AppendAndSummarize()
    {
        using var home = new TempHome();
        UsageLog.Append(new UsageRecord(DateTime.UtcNow, "local_ask", "claude-code", 1000, 200, 1500, true, EstimatedSavedTokens: 900));
        UsageLog.Append(new UsageRecord(DateTime.UtcNow, "local_summarize", "cursor", 5000, 300, 3000, false));
        File.AppendAllText(AppPaths.UsageFile, "{битая строка\n");
        var all = UsageLog.ReadAll();
        Assert.Equal(2, all.Count);
        var s = UsageLog.Summarize(all);
        Assert.Equal(6000, s.PromptTokens);
        Assert.Equal(1, s.Failed);
        Assert.Equal(900, s.EstimatedSavedTokens);
    }

    [Fact]
    public void AtomicWrite_Overwrites()
    {
        using var home = new TempHome();
        var p = Path.Combine(home.Path, "a", "b.json");
        FileUtil.WriteAllTextAtomic(p, "1");
        FileUtil.WriteAllTextAtomic(p, "2");
        Assert.Equal("2", File.ReadAllText(p));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(p)!));
    }
}

[CollectionDefinition("AppPaths", DisableParallelization = true)]
public class AppPathsCollection;
