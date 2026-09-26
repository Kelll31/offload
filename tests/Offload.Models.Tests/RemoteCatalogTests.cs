using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Offload.Core.Config;
using Offload.Core.Net;
using Offload.Core.Security;

namespace Offload.Models.Tests;

/// <summary>
/// Удалённый каталог: подпись Ed25519 (свой тестовый ключ), запрет отката версии, кэш с повторной проверкой,
/// раз в сутки, работа без сети. Подставляемый каталог — встроенный с другой версией, поэтому параллельные тесты,
/// читающие ModelCatalog.All, видят те же модели.
/// </summary>
[Collection("AppPaths")]
public sealed class RemoteCatalogTests
{
    private static readonly byte[] Seed = SHA256.HashData("offload-test-catalog-key"u8.ToArray());
    private static readonly byte[] Key = TestEd25519.PublicKey(Seed);

    private static string CatalogJson(long version) =>
        Regex.Replace(ModelCatalog.EmbeddedJson(), @"""version""\s*:\s*\d+", $"\"version\": {version}");

    private static string Sign(byte[] json) => Convert.ToBase64String(TestEd25519.Sign(Seed, json));

    /// <summary>Тестовое окружение: дом, ключ, адрес каталога; в конце — сброс состояния каталога.</summary>
    private sealed class Env : IDisposable
    {
        private readonly TempHome _home = new();

        public Env(string url, bool withKey = true)
        {
            RemoteCatalog.PublicKeyOverride = withKey ? Key : null;
            ModelCatalog.ResetRemote();
            ConfigStore.Update(c => (c.Network ??= new NetworkSettings()).CatalogUrl = url);
        }

        public void Dispose()
        {
            RemoteCatalog.PublicKeyOverride = null;
            ModelCatalog.ResetRemote();
            _home.Dispose();
        }
    }

    [Fact]
    public async Task Refresh_NewerSignedCatalog_AppliedCachedAndCheckedDaily()
    {
        using var server = new CatalogServer();
        using var env = new Env(server.CatalogUrl);
        var version = ModelCatalog.EmbeddedVersion + 1;
        server.Publish(Encoding.UTF8.GetBytes(CatalogJson(version)), Sign);
        var ct = TestContext.Current.CancellationToken;

        var result = await RemoteCatalog.RefreshIfDueAsync(ct);

        Assert.Equal(CatalogUpdateStatus.Updated, result.Status);
        Assert.Equal(version, result.Version);
        Assert.True(ModelCatalog.IsRemote);
        Assert.Equal(version, ModelCatalog.Current.Version);
        Assert.Equal(ModelCatalog.Parse(ModelCatalog.EmbeddedJson()).Count, ModelCatalog.All.Count);
        Assert.True(File.Exists(RemoteCatalog.CacheFile), "кэш — один файл с подписью и каталогом");
        Assert.False(File.Exists(RemoteCatalog.LegacyCacheFile) || File.Exists(RemoteCatalog.LegacySignatureFile));
        Assert.Equal(version, RemoteCatalog.MaxAcceptedVersion);
        Assert.Null(RemoteCatalog.LastCheck!.LastError);

        // Второй раз за сутки — без запросов.
        var requests = server.Requests.Count;
        Assert.Equal(CatalogUpdateStatus.NotDue, (await RemoteCatalog.RefreshIfDueAsync(ct)).Status);
        Assert.Equal(requests, server.Requests.Count);

        // «Новый запуск»: каталог берётся из кэша и снова проверяется по подписи.
        ModelCatalog.ResetRemote();
        Assert.Equal(version, ModelCatalog.Current.Version);
        Assert.True(ModelCatalog.IsRemote);
    }

    [Fact]
    public async Task Refresh_BadSignature_FailsAndKeepsEmbedded()
    {
        using var server = new CatalogServer();
        using var env = new Env(server.CatalogUrl);
        var json = Encoding.UTF8.GetBytes(CatalogJson(ModelCatalog.EmbeddedVersion + 5));
        var foreign = TestEd25519.Sign(SHA256.HashData("чужой ключ"u8.ToArray()), json);
        server.Publish(json, _ => Convert.ToBase64String(foreign));

        var result = await RemoteCatalog.RefreshAsync(force: true, TestContext.Current.CancellationToken);

        Assert.Equal(CatalogUpdateStatus.Failed, result.Status);
        Assert.Contains("Подпись", result.Error);
        Assert.False(ModelCatalog.IsRemote);
        Assert.Equal(ModelCatalog.EmbeddedVersion, ModelCatalog.Current.Version);
        Assert.False(File.Exists(RemoteCatalog.CacheFile));
        Assert.NotNull(RemoteCatalog.LastCheck?.LastError);
    }

    [Fact]
    public async Task Refresh_ModifiedAfterSigning_Fails()
    {
        using var server = new CatalogServer();
        using var env = new Env(server.CatalogUrl);
        var json = Encoding.UTF8.GetBytes(CatalogJson(ModelCatalog.EmbeddedVersion + 1));
        var signature = Sign(json);
        json[^3] ^= 0x20; // файл изменён после подписи
        server.Publish(json, _ => signature);

        Assert.Equal(CatalogUpdateStatus.Failed, (await RemoteCatalog.RefreshAsync(true, TestContext.Current.CancellationToken)).Status);
        Assert.False(ModelCatalog.IsRemote);
    }

    [Fact]
    public async Task Refresh_SameOrOlderVersion_NotApplied()
    {
        using var server = new CatalogServer();
        using var env = new Env(server.CatalogUrl);
        var ct = TestContext.Current.CancellationToken;

        server.Publish(Encoding.UTF8.GetBytes(CatalogJson(ModelCatalog.EmbeddedVersion)), Sign);
        Assert.Equal(CatalogUpdateStatus.UpToDate, (await RemoteCatalog.RefreshAsync(true, ct)).Status);
        server.Publish(Encoding.UTF8.GetBytes(CatalogJson(ModelCatalog.EmbeddedVersion - 1)), Sign);
        Assert.Equal(CatalogUpdateStatus.UpToDate, (await RemoteCatalog.RefreshAsync(true, ct)).Status);
        Assert.False(ModelCatalog.IsRemote);
        Assert.False(File.Exists(RemoteCatalog.CacheFile));

        // После обновления откат на промежуточную версию тоже не принимается.
        var newer = ModelCatalog.EmbeddedVersion + 10;
        server.Publish(Encoding.UTF8.GetBytes(CatalogJson(newer)), Sign);
        Assert.Equal(CatalogUpdateStatus.Updated, (await RemoteCatalog.RefreshAsync(true, ct)).Status);
        server.Publish(Encoding.UTF8.GetBytes(CatalogJson(newer - 5)), Sign);
        var result = await RemoteCatalog.RefreshAsync(true, ct);
        Assert.Equal(CatalogUpdateStatus.UpToDate, result.Status);
        Assert.Equal(newer, result.Version);
        Assert.Equal(newer, ModelCatalog.Current.Version);
        ModelCatalog.ResetRemote();
        Assert.Equal(newer, ModelCatalog.Current.Version); // в кэше осталась новая версия
    }

    [Fact]
    public async Task Refresh_SignedButInvalidCatalog_Fails()
    {
        using var server = new CatalogServer();
        using var env = new Env(server.CatalogUrl);
        server.Publish(Encoding.UTF8.GetBytes($$"""{ "version": {{ModelCatalog.EmbeddedVersion + 1}}, "models": [] }"""), Sign);

        var result = await RemoteCatalog.RefreshAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(CatalogUpdateStatus.Failed, result.Status);
        Assert.False(ModelCatalog.IsRemote);
    }

    [Fact]
    public async Task Refresh_UnknownFields_Tolerated()
    {
        using var server = new CatalogServer();
        using var env = new Env(server.CatalogUrl);
        var version = ModelCatalog.EmbeddedVersion + 1;
        var json = CatalogJson(version);
        // Поля из будущих версий каталога («role» уже известен — он проверяется, поэтому здесь другое имя).
        json = new Regex(@"""id""\s*:").Replace(json, "\"futureRole\": \"coder\", \"futureField\": { \"x\": [1, 2] }, \"id\":", 1);
        json = json.Replace("\"models\":", "\"minOffloadVersion\": \"9.9.9\", \"models\":");
        server.Publish(Encoding.UTF8.GetBytes(json), Sign);

        var result = await RemoteCatalog.RefreshAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(CatalogUpdateStatus.Updated, result.Status);
        Assert.Equal(version, ModelCatalog.Current.Version);
    }

    [Fact]
    public async Task Refresh_Offline_FailsWithoutThrowing()
    {
        using var env = new Env($"http://127.0.0.1:{FreePort()}/catalog.json");
        var result = await RemoteCatalog.RefreshIfDueAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CatalogUpdateStatus.Failed, result.Status);
        Assert.Equal(ModelCatalog.EmbeddedVersion, result.Version);
        // Следующая попытка — не раньше чем через сутки.
        Assert.Equal(CatalogUpdateStatus.NotDue, (await RemoteCatalog.RefreshIfDueAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Refresh_DisabledOrNoKey_MakesNoRequests()
    {
        using var server = new CatalogServer();
        server.Publish(Encoding.UTF8.GetBytes(CatalogJson(ModelCatalog.EmbeddedVersion + 1)), Sign);
        var ct = TestContext.Current.CancellationToken;

        using (new Env(server.CatalogUrl, withKey: false))
        {
            Assert.False(RemoteCatalog.IsAvailable);
            Assert.Equal(CatalogUpdateStatus.Disabled, (await RemoteCatalog.RefreshAsync(true, ct)).Status);
        }
        using (new Env(server.CatalogUrl))
        {
            ConfigStore.Update(c => c.Network.RemoteCatalog = false);
            Assert.Equal(CatalogUpdateStatus.Disabled, (await RemoteCatalog.RefreshAsync(true, ct)).Status);
        }
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task LoadCached_TamperedOrDisabled_FallsBackToEmbedded()
    {
        using var server = new CatalogServer();
        using var env = new Env(server.CatalogUrl);
        server.Publish(Encoding.UTF8.GetBytes(CatalogJson(ModelCatalog.EmbeddedVersion + 1)), Sign);
        Assert.Equal(CatalogUpdateStatus.Updated, (await RemoteCatalog.RefreshAsync(true, TestContext.Current.CancellationToken)).Status);

        // Выключили удалённый каталог — кэш не применяется.
        ConfigStore.Update(c => c.Network.RemoteCatalog = false);
        ModelCatalog.ResetRemote();
        Assert.False(ModelCatalog.IsRemote);
        ConfigStore.Update(c => c.Network.RemoteCatalog = true);

        // Кэш испорчен на диске — подпись не сходится, действует встроенный.
        var (json, signature) = RemoteCatalog.ReadEnvelope(await File.ReadAllBytesAsync(RemoteCatalog.CacheFile, TestContext.Current.CancellationToken));
        json[10] ^= 0x01;
        WriteEnvelope(json, signature);
        ModelCatalog.ResetRemote();
        Assert.False(ModelCatalog.IsRemote);
        Assert.Equal(ModelCatalog.EmbeddedVersion, ModelCatalog.Current.Version);
    }

    [Fact]
    public void LoadCached_GarbageEnvelope_FallsBackToEmbedded()
    {
        using var env = new Env("http://127.0.0.1:1/catalog.json");
        Directory.CreateDirectory(RemoteCatalog.CacheDir);
        File.WriteAllText(RemoteCatalog.CacheFile, """{"format":1,"signature":"x","catalog":"не base64"}""");

        Assert.Null(RemoteCatalog.LoadCached());
        Assert.False(ModelCatalog.IsRemote);
    }

    [Fact]
    public void LoadCached_LegacyPairOfFiles_StillReadAndSeedsMaxVersion()
    {
        using var env = new Env("http://127.0.0.1:1/catalog.json");
        var version = ModelCatalog.EmbeddedVersion + 3;
        var json = Encoding.UTF8.GetBytes(CatalogJson(version));
        Directory.CreateDirectory(RemoteCatalog.CacheDir);
        File.WriteAllBytes(RemoteCatalog.LegacyCacheFile, json);
        File.WriteAllText(RemoteCatalog.LegacySignatureFile, Sign(json));

        Assert.Equal(version, ModelCatalog.Current.Version);
        Assert.True(ModelCatalog.IsRemote);
        Assert.Equal(version, RemoteCatalog.MaxAcceptedVersion);
    }

    [Fact]
    public async Task Refresh_WritesSingleEnvelope_ReplacesLegacyPair()
    {
        using var server = new CatalogServer();
        using var env = new Env(server.CatalogUrl);
        // Старая пара файлов от прежней версии — после обновления её нет, читается только новый файл.
        var old = Encoding.UTF8.GetBytes(CatalogJson(ModelCatalog.EmbeddedVersion + 1));
        Directory.CreateDirectory(RemoteCatalog.CacheDir);
        File.WriteAllBytes(RemoteCatalog.LegacyCacheFile, old);
        File.WriteAllText(RemoteCatalog.LegacySignatureFile, Sign(old));
        var version = ModelCatalog.EmbeddedVersion + 2;
        var published = Encoding.UTF8.GetBytes(CatalogJson(version));
        server.Publish(published, Sign);

        Assert.Equal(CatalogUpdateStatus.Updated, (await RemoteCatalog.RefreshAsync(true, TestContext.Current.CancellationToken)).Status);

        Assert.False(File.Exists(RemoteCatalog.LegacyCacheFile));
        Assert.False(File.Exists(RemoteCatalog.LegacySignatureFile));
        var (json, signature) = RemoteCatalog.ReadEnvelope(File.ReadAllBytes(RemoteCatalog.CacheFile));
        Assert.Equal(published, json);
        Assert.Equal(Sign(published), signature);
        Assert.Empty(Directory.GetFiles(RemoteCatalog.CacheDir, "*.tmp"));
        ModelCatalog.ResetRemote();
        Assert.Equal(version, ModelCatalog.Current.Version);
    }

    [Fact]
    public async Task Refresh_OlderSignedCatalogAfterCacheLoss_RollbackRejected()
    {
        using var server = new CatalogServer();
        using var env = new Env(server.CatalogUrl);
        var ct = TestContext.Current.CancellationToken;
        var newer = ModelCatalog.EmbeddedVersion + 10;
        server.Publish(Encoding.UTF8.GetBytes(CatalogJson(newer)), Sign);
        Assert.Equal(CatalogUpdateStatus.Updated, (await RemoteCatalog.RefreshAsync(true, ct)).Status);

        // Кэш удалён (или испорчен), а сервер или подменённый адрес отдаёт старый, но подписанный каталог.
        File.Delete(RemoteCatalog.CacheFile);
        ModelCatalog.ResetRemote();
        Assert.False(ModelCatalog.IsRemote);
        server.Publish(Encoding.UTF8.GetBytes(CatalogJson(newer - 5)), Sign);

        var result = await RemoteCatalog.RefreshAsync(true, ct);

        Assert.Equal(CatalogUpdateStatus.Failed, result.Status);
        Assert.Contains("откат", result.Error, StringComparison.Ordinal);
        Assert.False(ModelCatalog.IsRemote);
        Assert.Equal(newer, RemoteCatalog.MaxAcceptedVersion);

        // Та же версия, что уже принималась, — не откат: применяется.
        server.Publish(Encoding.UTF8.GetBytes(CatalogJson(newer)), Sign);
        Assert.Equal(CatalogUpdateStatus.Updated, (await RemoteCatalog.RefreshAsync(true, ct)).Status);
        Assert.Equal(newer, ModelCatalog.Current.Version);
    }

    [Fact]
    public void LoadCached_OlderThanMaxAccepted_Rejected()
    {
        using var env = new Env("http://127.0.0.1:1/catalog.json");
        var version = ModelCatalog.EmbeddedVersion + 2;
        var json = Encoding.UTF8.GetBytes(CatalogJson(version));
        Directory.CreateDirectory(RemoteCatalog.CacheDir);
        File.WriteAllText(RemoteCatalog.MaxVersionFile, (version + 1).ToString(CultureInfo.InvariantCulture));
        WriteEnvelope(json, Sign(json));

        Assert.Null(RemoteCatalog.LoadCached());
        Assert.False(ModelCatalog.IsRemote);
    }

    [Fact]
    public async Task Refresh_ExpiredCatalog_Rejected_FutureExpiryAccepted()
    {
        using var server = new CatalogServer();
        using var env = new Env(server.CatalogUrl);
        var ct = TestContext.Current.CancellationToken;
        var version = ModelCatalog.EmbeddedVersion + 1;

        server.Publish(Encoding.UTF8.GetBytes(WithExpires(CatalogJson(version), DateTime.UtcNow.AddDays(-1))), Sign);
        var expired = await RemoteCatalog.RefreshAsync(true, ct);
        Assert.Equal(CatalogUpdateStatus.Failed, expired.Status);
        Assert.Contains("истёк", expired.Error, StringComparison.Ordinal);
        Assert.False(ModelCatalog.IsRemote);

        server.Publish(Encoding.UTF8.GetBytes(WithExpires(CatalogJson(version), DateTime.UtcNow.AddDays(30))), Sign);
        Assert.Equal(CatalogUpdateStatus.Updated, (await RemoteCatalog.RefreshAsync(true, ct)).Status);
        Assert.NotNull(ModelCatalog.Current.Expires);
    }

    [Fact]
    public void LoadCached_ExpiredSignedCatalog_FallsBackToEmbedded()
    {
        using var env = new Env("http://127.0.0.1:1/catalog.json");
        var json = Encoding.UTF8.GetBytes(WithExpires(CatalogJson(ModelCatalog.EmbeddedVersion + 1), DateTime.UtcNow.AddMinutes(-1)));
        Directory.CreateDirectory(RemoteCatalog.CacheDir);
        WriteEnvelope(json, Sign(json));

        Assert.Null(RemoteCatalog.LoadCached());
        Assert.Equal(ModelCatalog.EmbeddedVersion, ModelCatalog.Current.Version);
    }

    private static string WithExpires(string json, DateTime expiresUtc) =>
        new Regex(@"""version""\s*:").Replace(json,
            $"\"expires\": \"{expiresUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}\", \"version\":", 1);

    private static void WriteEnvelope(byte[] json, string signature) =>
        File.WriteAllText(RemoteCatalog.CacheFile,
            $$"""{"format":1,"signature":"{{signature}}","catalog":"{{Convert.ToBase64String(json)}}"}""");

    [Fact]
    public void EmbeddedCatalog_HasVersion()
    {
        Assert.True(ModelCatalog.EmbeddedVersion >= 2026092400, "у встроенного каталога должна быть версия (поле version)");
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Отдаёт /catalog.json и /catalog.json.sig.</summary>
    private sealed class CatalogServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private volatile byte[] _json = [];
        private volatile byte[] _sig = [];

        public CatalogServer()
        {
            BaseUrl = $"http://127.0.0.1:{FreePort()}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public string BaseUrl { get; }
        public string CatalogUrl => BaseUrl + "/catalog.json";
        public ConcurrentQueue<string> Requests { get; } = new();

        public void Publish(byte[] json, Func<byte[], string> sign)
        {
            _json = json;
            _sig = Encoding.ASCII.GetBytes(sign(json) + "\n");
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                var path = ctx.Request.Url!.AbsolutePath;
                Requests.Enqueue(path);
                var body = path switch { "/catalog.json" => _json, "/catalog.json.sig" => _sig, _ => null };
                try
                {
                    if (body is null) ctx.Response.StatusCode = 404;
                    else
                    {
                        ctx.Response.ContentLength64 = body.Length;
                        await ctx.Response.OutputStream.WriteAsync(body);
                    }
                }
                catch
                {
                    // Клиент отключился.
                }
                finally
                {
                    try { ctx.Response.Close(); } catch { }
                }
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            _listener.Close();
        }
    }
}

/// <summary>Токен Hugging Face из настроек: уходит хабу (tree API и resolve), но не на CDN после перенаправления.</summary>
[Collection("AppPaths")]
public sealed class HfTokenTests
{
    [Fact]
    public async Task StoredToken_SentToHubOnly()
    {
        Assert.SkipUnless(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(NetworkOptions.HfTokenEnvVar)), "задан HF_TOKEN");
        using var home = new TempHome();
        using var hub = new LocalHub();
        var data = new byte[64 * 1024];
        new Random(5).NextBytes(data);
        var sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        hub.AddFile("org/private-GGUF", "m-Q4_K_M.gguf", data, sha);
        ConfigStore.Update(c => (c.Network ??= new NetworkSettings()).HfTokenProtected = Dpapi.Protect("hf_secret"));
        HfClient.Endpoint = hub.BaseUrl;
        var dir = Path.Combine(home.Path, "dl");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var files = await HfClient.ListFilesAsync("org/private-GGUF", ct);
            var file = Assert.Single(files);
            await HttpDownloader.DownloadFileAsync(HfClient.DownloadUrl("org/private-GGUF", file.Path), Path.Combine(dir, file.Path),
                file.Size, file.Sha256, null, ct);

            var auth = hub.Auth.ToList();
            Assert.Equal("Bearer hf_secret", auth.Single(a => a.Path.Contains("/tree/")).Authorization);
            Assert.Equal("Bearer hf_secret", auth.Single(a => a.Path.Contains("/resolve/")).Authorization);
            Assert.Null(auth.Single(a => a.Path.StartsWith("/cdn/", StringComparison.Ordinal)).Authorization);
        }
        finally
        {
            NetworkOptions.HfEndpointOverride = null;
        }
    }
}
