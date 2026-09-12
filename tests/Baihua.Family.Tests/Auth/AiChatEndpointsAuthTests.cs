using Baihua.Core.Models;
using Baihua.Core.Services;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Baihua.Core.Security;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Modules.Family.Services;
using Baihua.Family.Tests.TestDoubles;
using Xunit;

namespace Baihua.Family.Tests.Auth;

/// <summary>
/// AI-01 验收标准：/api/ai/chat/completion + /api/ai/chat/stream 纳入 HMAC 设备鉴权域。
///
/// 红测试（TDD）：以下用例在实现前必须失败——
///   已配对设备（有效 HMAC 签名 + X-Device-Id）→ 200
///   未配对 / 无签名 / 坏签名 → 401
///   回归：/mg/* 鉴权行为不变
///
/// 当前（未实现）行为：/api/ai/chat/* 不在 Family 鉴权域，TestServer(loopback) 下直接 404，
/// 与验收标准不符 → 红。实现后应变为 200/401 语义。
/// </summary>
[CollectionDefinition("AiChatEndpoints", DisableParallelization = true)]
public class AiChatEndpointsCollection { }

[Collection("AiChatEndpoints")]
public class AiChatEndpointsAuthTests : IClassFixture<AiChatEndpointsAuthFixture>
{
    private readonly AiChatEndpointsAuthFixture _fx;

    public AiChatEndpointsAuthTests(AiChatEndpointsAuthFixture fx) => _fx = fx;

    private const string ChatBody = "{\"message\":\"hello\"}";
    private const string PairedDeviceId = "paired-device-001";
    private const string UnknownDeviceId = "never-paired-device-999";

    // ---------- 验收标准：未配对 / 无签名 / 坏签名 → 401 ----------

    [Fact]
    public async Task Completion_NoSignature_Returns401()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/ai/chat/completion")
        {
            Content = new StringContent(ChatBody, Encoding.UTF8, "application/json")
        };
        var resp = await _fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Completion_InvalidSignature_Returns401()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/ai/chat/completion")
        {
            Content = new StringContent(ChatBody, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Mobile-Signature", "1234567890:bad-signature-base64");
        var resp = await _fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Completion_ValidSignature_ButDeviceNotPaired_Returns401()
    {
        var sig = _fx.Sign("POST", "/api/ai/chat/completion", ChatBody);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/ai/chat/completion")
        {
            Content = new StringContent(ChatBody, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Mobile-Signature", sig);
        req.Headers.Add("X-Device-Id", UnknownDeviceId);
        var resp = await _fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ---------- 验收标准：已配对设备 → 200 ----------

    [Fact]
    public async Task Completion_ValidSignature_PairedDevice_Returns200()
    {
        var sig = _fx.Sign("POST", "/api/ai/chat/completion", ChatBody);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/ai/chat/completion")
        {
            Content = new StringContent(ChatBody, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Mobile-Signature", sig);
        req.Headers.Add("X-Device-Id", PairedDeviceId);
        var resp = await _fx.Client.SendAsync(req);

        // 合并后 /api/ai/chat 由本进程的 AI 模块直服（不再转发到 8791）：
        // 鉴权通过即 200；模型回复取决于本机是否配置了 AI 提供方，测试环境无提供方 → success=false
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("success", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stream_ValidSignature_PairedDevice_Returns200_EventStream()
    {
        var sig = _fx.Sign("POST", "/api/ai/chat/stream", ChatBody);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/ai/chat/stream")
        {
            Content = new StringContent(ChatBody, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Mobile-Signature", sig);
        req.Headers.Add("X-Device-Id", PairedDeviceId);
        var resp = await _fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/event-stream", resp.Content.Headers.ContentType?.ToString());
    }

    // ---------- 回归：/mg/* 鉴权行为不变 ----------

    [Fact]
    public async Task Regression_MgManifest_NoSignature_Still401()
    {
        var resp = await _fx.Client.GetAsync($"/mg/manifest?vaultId={_fx.VaultId}&since=0");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Regression_MgManifest_ValidSignature_PairedDevice_Still200()
    {
        // 知识库同步路径的授权由知识库模块的 ISyncAuthorizationStrategy 判定；
        // 合并前靠 family 转发时附加 Bearer，现由宿主中间件进程内注入 —— 行为必须等价（200）
        var path = $"/mg/manifest?vaultId={_fx.VaultId}&since=0";
        var sig = _fx.Sign("GET", path, null);
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("X-Mobile-Signature", sig);
        req.Headers.Add("X-Device-Id", PairedDeviceId);
        var resp = await _fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}

/// <summary>
/// 测试夹具：进程环境变量隔离（BAIHUA_HOME → 临时目录）+ 真实百花宿主（TestServer）
/// + 一个真实知识库 + 一台已配对设备（写入 SQLite）。
///
/// 合并为单进程后不再需要 AI/Vault 的 HTTP stub：/api/ai/chat/* 与 /mg/* 都由同一进程直服，
/// 本夹具要验证的正是"合并后鉴权语义不变"。
/// </summary>
public sealed class AiChatEndpointsAuthFixture : IDisposable
{
    public const string TestSecret = "ai-01-test-shared-secret";

    private readonly string _oldBaihuaHome;
    private readonly string _tempHome;
    private readonly WebApplicationFactory<Program> _factory;

    public HttpClient Client { get; }
    public RequestSignatureService Signer { get; }

    /// <summary>夹具创建的真实知识库 id（/mg/manifest 用）</summary>
    public string VaultId { get; }

    /// <summary>该知识库的磁盘目录（存在且为空 → 清单为空但 200）</summary>
    public string VaultPath { get; }

    public AiChatEndpointsAuthFixture()
    {
        // ---- 环境变量隔离：不污染真实数据目录 ----
        _oldBaihuaHome = Environment.GetEnvironmentVariable("BAIHUA_HOME") ?? "";

        _tempHome = Path.Combine(Path.GetTempPath(), "baihua-ai01-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempHome);
        Environment.SetEnvironmentVariable("BAIHUA_HOME", _tempHome);
        Baihua.Contracts.BaihuaPaths.Reset();

        VaultPath = Path.Combine(_tempHome, "vaults", "测试知识库");
        Directory.CreateDirectory(Path.Combine(VaultPath, "notes"));

        // ---- 预建库表（family / vault 同库，AI 表由 host 的 DatabaseInitializer 建） ----
        var familyDbPath = Path.Combine(_tempHome, "baihua.db");
        using (var preCtx = new FamilyDbContext(TestSqliteDb.FamilyOptions(familyDbPath)))
        {
            preCtx.Database.EnsureCreated();
        }
        using (var preVault = new VaultDbContext(TestSqliteDb.VaultOptions(familyDbPath)))
        {
            preVault.Database.EnsureCreated();
        }

        // ---- 真实百花宿主（TestServer，loopback；单库 SQLite）----
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("MobileAuth:SharedSecret", TestSecret);
                builder.UseSetting("BAIHUA_SKIP_MUTEX", "true");
                builder.ConfigureServices(services =>
                    TestSqliteDb.ConfigureSqlite(services, familyDbPath, familyDbPath));
            });
        Client = _factory.CreateClient();

        // ---- 签名器（与 host 内 RequestSignatureService 同 secret）----
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "MobileAuth:SharedSecret", TestSecret }
            })
            .Build();
        var sasMock = new Moq.Mock<ServerAddressService>(
            new Moq.Mock<IDbContextFactory<FamilyDbContext>>().Object,
            NullLogger<ServerAddressService>.Instance,
            config);
        Signer = new RequestSignatureService(sasMock.Object, config, NullLogger<RequestSignatureService>.Instance);

        // ---- 写入一台已配对设备 ----
        using (var ctx = _factory.Services.GetRequiredService<IDbContextFactory<FamilyDbContext>>().CreateDbContext())
        {
            ctx.AuthorizedDevices.Add(new AuthorizedDevice
            {
                DeviceId = "paired-device-001",
                DeviceName = "AI-01 测试机",
                AccessToken = "test-access-token",
                Status = "Authorized",
                AuthorizedTime = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            ctx.SaveChanges();
        }

        // ---- 登记一个真实知识库（/mg/manifest 走真实磁盘扫描）----
        // 宿主启动时会按 vaults 根目录自动登记含 notes/ 的目录，这里直接取回；
        // 若未自动登记（不同启动路径）则显式创建。
        var vaultSettings = _factory.Services.GetRequiredService<VaultSettingsService>();
        var existing = vaultSettings.GetVaults().FirstOrDefault(v => v.Path == VaultPath);
        VaultId = existing?.Id ?? vaultSettings.AddVault("测试知识库", VaultPath, "笔记").Id;
    }

    public string Sign(string method, string path, string? body)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return $"{ts}:{Signer.ComputeSignature(method, path, body, ts)}";
    }

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("BAIHUA_HOME", _oldBaihuaHome);
        Baihua.Contracts.BaihuaPaths.Reset();
        try { Directory.Delete(_tempHome, recursive: true); } catch { }
    }
}

/// <summary>
/// 极简本地 HTTP stub（TcpListener 手写协议，避免 HttpListener URL ACL 权限问题）。
/// </summary>
public sealed class StubHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, (int Status, string ContentType, string Body)> _routes;
    private readonly Task _loop;

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public StubHttpServer(Dictionary<string, (int Status, string ContentType, string Body)> routes)
    {
        _routes = routes;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            try
            {
                var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(requestLine)) return;
                var parts = requestLine.Split(' ');
                var path = parts.Length > 1 ? parts[1] : "/";
                int contentLength = 0;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line.AsSpan(16).Trim());
                }
                var body = contentLength > 0
                    ? await ReadExactFromReaderAsync(reader, contentLength)
                    : "";
                _ = body;

                var (status, contentType, respBody) = Respond(path);
                var bytes = Encoding.UTF8.GetBytes(respBody);
                var reason = status == 200 ? "OK" : status == 401 ? "Unauthorized" : status == 404 ? "Not Found" : "OK";
                var header = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(header));
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
            }
            catch { /* stub 尽力而为 */ }
        }
    }

    private (int Status, string ContentType, string Body) Respond(string path)
    {
        foreach (var (prefix, resp) in _routes)
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return resp;
        return (404, "application/json", "{\"error\":\"stub not found\"}");
    }

    private static async Task<string> ReadExactFromReaderAsync(StreamReader reader, int length)
    {
        var buf = new char[length];
        var read = 0;
        while (read < length)
        {
            var n = await reader.ReadBlockAsync(buf.AsMemory(read, length - read));
            if (n == 0) break;
            read += n;
        }
        return new string(buf, 0, read);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }
}
