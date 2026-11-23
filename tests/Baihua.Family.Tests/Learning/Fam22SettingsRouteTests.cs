using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Baihua.Contracts.Achievements;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Family.Tests.TestDoubles;
using Xunit;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// FAM-22 P1 回归：全家排行开关端点必须接受 POST（WebUI ApiService 用
/// PostWithMetricsAsync 发送 POST）。arch review 发现服务端曾声明 [HttpPut]，
/// 客户端 POST → 运行时 405，开关静默失败（allFamilyEnabled 回退 false）。
///
/// 为什么需要路由级测试：既有单测直接调用 Controller action，不经 HTTP 路由
/// 中间件，抓不到方法不匹配（405）。本测试走 WebApplicationFactory + 真实
/// 路由，锁定 HTTP 契约：POST 200 / GET 200（PUT 不再提供）。
/// </summary>
[CollectionDefinition("Fam22SettingsRoute", DisableParallelization = true)]
public class Fam22SettingsRouteCollection { }

[Collection("Fam22SettingsRoute")]
public class Fam22SettingsRouteTests : IClassFixture<Fam22SettingsRouteFixture>
{
    private readonly Fam22SettingsRouteFixture _fx;

    public Fam22SettingsRouteTests(Fam22SettingsRouteFixture fx) => _fx = fx;

    private const string SettingsUrl = "/api/achievements/leaderboard/settings/all-family-tab";

    [Fact]
    public async Task Post_AllFamilyTabSetting_Returns200()
    {
        // arch P1：WebUI 用 POST 设置开关，服务端必须接受 POST（405 = bug）
        using var req = new HttpRequestMessage(HttpMethod.Post, SettingsUrl)
        {
            Content = new StringContent(
                "{\"allFamilyTabEnabled\":true}", Encoding.UTF8, "application/json")
        };
        var resp = await _fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Get_AllFamilyTabSetting_Returns200()
    {
        // 读取开关（GET 契约不变，作为基线）
        var resp = await _fx.Client.GetAsync(SettingsUrl);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Post_AllFamilyTabSetting_RoundTripsValue()
    {
        // 设置 true 后读取应回显 true（开关真实生效，非空 DTO 兜底）
        using var req = new HttpRequestMessage(HttpMethod.Post, SettingsUrl)
        {
            Content = new StringContent(
                "{\"allFamilyTabEnabled\":true}", Encoding.UTF8, "application/json")
        };
        var post = await _fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var dto = await post.Content.ReadFromJsonAsync<LeaderboardSettingsDto>();
        Assert.NotNull(dto);
        Assert.True(dto.AllFamilyTabEnabled);
    }
}

/// <summary>
/// 精简 host fixture：真实 Family Host（TestServer）+ 内存 SQLite FamilyDbContext，
/// 走完整 HTTP 路由中间件（可抓 405 类方法不匹配）。
/// </summary>
public sealed class Fam22SettingsRouteFixture : IDisposable
{
    private readonly string _oldBaihuaHome;
    private readonly string _tempHome;
    private readonly StubVaultServer _vaultStub;
    private readonly WebApplicationFactory<Program> _factory;

    public HttpClient Client { get; }

    public Fam22SettingsRouteFixture()
    {
        _oldBaihuaHome = Environment.GetEnvironmentVariable("BAIHUA_HOME") ?? "";
        _tempHome = Path.Combine(Path.GetTempPath(), "baihua-fam22-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempHome);
        Environment.SetEnvironmentVariable("BAIHUA_HOME", _tempHome);
        Baihua.Contracts.BaihuaPaths.Reset();

        _vaultStub = new StubVaultServer();
        Environment.SetEnvironmentVariable("BAIHUA_VAULT_URL", _vaultStub.BaseUrl);

        // 预建库表（同 AiChatEndpointsAuthTests：ServerAddressService 会抢先建表，
        // 测试环境在 host 创建前先 EnsureCreated 建全表，避免 Migrate 撞表）。
        // 产品已迁移 PostgreSQL，测试 host 统一覆盖为 SQLite（见 TestSqliteDb.ConfigureSqlite）。
        var familyDbPath = Path.Combine(_tempHome, "family.db");
        var vaultDbPath = Path.Combine(_tempHome, "vault.db");
        using (var preCtx = new FamilyDbContext(TestSqliteDb.FamilyOptions(familyDbPath)))
        {
            preCtx.Database.EnsureCreated();
        }
        using (var preVault = new VaultDbContext(TestSqliteDb.VaultOptions(vaultDbPath)))
        {
            preVault.Database.EnsureCreated();
        }

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("BAIHUA_SKIP_MUTEX", "true");
                builder.UseSetting("BAIHUA_SKIP_ACCESS_CONTROL", "true");
                builder.ConfigureServices(services =>
                    TestSqliteDb.ConfigureSqlite(services, familyDbPath, vaultDbPath));
            });
        Client = _factory.CreateClient();
    }

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
        _vaultStub.Dispose();
        Environment.SetEnvironmentVariable("BAIHUA_HOME", _oldBaihuaHome);
        Baihua.Contracts.BaihuaPaths.Reset();
        try { Directory.Delete(_tempHome, recursive: true); } catch { }
    }
}

/// <summary>
/// 极简 vault stub：仅提供 /mg/manifest（host 启动探测用）。
/// </summary>
public sealed class StubVaultServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public StubVaultServer()
    {
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
                var body = path == "/mg/manifest"
                    ? "{\"cursor\":0,\"minSeq\":1,\"files\":[]}"
                    : "{}";
                var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n";
                var bytes = Encoding.UTF8.GetBytes(header + body);
                await stream.WriteAsync(bytes);
            }
            catch { /* client disconnect */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }
}
