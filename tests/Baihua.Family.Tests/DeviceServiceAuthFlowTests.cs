using System.Reflection;
using Baihua.Core;
using Baihua.Core.WebSocket;
using Baihua.Data;
using Baihua.Family.Tests.TestDoubles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Baihua.Family.Tests;

/// <summary>
/// DeviceService 授权状态机测试（服务端核心状态流转）：
/// 注册 → 授权（authorized 定向推送参数）→ 拒绝 → 撤销（revoked 定向推送参数）→ 重新激活。
/// 覆盖用户场景：WebUI 批准后设备必须变为已授权；撤销后变为 Revoked 且推送带正确 deviceId。
/// </summary>
public class DeviceServiceAuthFlowTests : IDisposable
{
    private readonly SqliteConnection _sqlite;
    private readonly Mock<IDbContextFactory<FamilyDbContext>> _factory;

    public DeviceServiceAuthFlowTests()
    {
        // SQLite 内存库（真实约束：DeviceId UNIQUE），每个测试独立连接；
        // 产品已用 PG now() 默认值，SQLite 需注册 now() 函数（见 TestSqliteDb）
        _sqlite = TestSqliteDb.OpenInMemory();
        var options = new DbContextOptionsBuilder<FamilyDbContext>()
            .UseSqlite(_sqlite)
            .Options;
        using (var ctx = new FamilyDbContext(options))
        {
            ctx.Database.EnsureCreated();
        }
        _factory = new Mock<IDbContextFactory<FamilyDbContext>>();
        _factory.Setup(f => f.CreateDbContext()).Returns(() => new FamilyDbContext(options));
    }

    public void Dispose()
    {
        _sqlite.Dispose();
    }

    private DeviceService CreateService(DeviceWebSocketHub? hub = null)
    {
        var config = new ConfigurationBuilder().Build();
        return new DeviceService(config, NullLogger<DeviceService>.Instance, _factory.Object,
            wsHub: hub);
    }

    [Fact]
    public void AuthorizeDevice_marks_device_authorized_with_registered_deviceId()
    {
        var hub = new DeviceWebSocketHub(NullLogger<DeviceWebSocketHub>.Instance);
        var service = CreateService(hub);
        service.AutoAuthorizeEnabled = false;

        var request = service.SubmitLanDiscoveryRequest("望月·书轩", deviceId: "android-abc123");
        var result = service.AuthorizeDevice(request.RequestId);

        Assert.True(result.success);
        Assert.NotNull(result.accessToken);
        using var ctx = _factory.Object.CreateDbContext();
        var device = ctx.AuthorizedDevices.Single(d => d.DeviceName == "望月·书轩");
        Assert.Equal("Authorized", device.Status);
        Assert.Equal("android-abc123", device.DeviceId);
        Assert.Equal(result.accessToken, device.AccessToken);
    }

    [Fact]
    public void RevokeDevice_marks_device_revoked()
    {
        var service = CreateService();
        var request = service.SubmitLanDiscoveryRequest("安卓机", deviceId: "android-xyz");
        service.AuthorizeDevice(request.RequestId);

        var ok = service.RevokeDevice("android-xyz");
        Assert.True(ok);

        using var ctx = _factory.Object.CreateDbContext();
        var device = ctx.AuthorizedDevices.Single(d => d.DeviceId == "android-xyz");
        Assert.Equal("Revoked", device.Status);
    }

    [Fact]
    public void RevokeDevice_unknown_deviceId_returns_false_without_crash()
    {
        var service = CreateService();
        Assert.False(service.RevokeDevice("no-such-device"));
    }

    [Fact]
    public void RejectRequest_removes_pending_and_reauthorization_allowed()
    {
        var service = CreateService();
        service.AutoAuthorizeEnabled = false;

        var request = service.SubmitLanDiscoveryRequest("待拒设备", deviceId: "android-rej");
        Assert.True(service.RejectRequest(request.RequestId));

        // 拒绝后可重新注册并授权（同一 deviceId）
        var request2 = service.SubmitLanDiscoveryRequest("待拒设备", deviceId: "android-rej");
        var result = service.AuthorizeDevice(request2.RequestId);
        Assert.True(result.success);
    }

    [Fact]
    public void Revoked_device_can_be_reactivated_with_new_deviceId()
    {
        var service = CreateService();
        var request = service.SubmitLanDiscoveryRequest("重激活", deviceId: "android-old");
        service.AuthorizeDevice(request.RequestId);
        service.RevokeDevice("android-old");

        var ok = service.ReactivateRevokedDevice("重激活", "android-new");
        Assert.True(ok);

        using var ctx = _factory.Object.CreateDbContext();
        var device = ctx.AuthorizedDevices.Single(d => d.DeviceName == "重激活");
        Assert.Equal("Authorized", device.Status);
        Assert.Equal("android-new", device.DeviceId);
    }

    [Fact]
    public void AuthorizedDevice_authorized_then_revoked_then_reauthorized_same_id()
    {
        // 同 DeviceId 被撤销后重新授权：更新原记录为 Authorized（DeviceId UNIQUE 约束路径）
        var service = CreateService();
        var req1 = service.SubmitLanDiscoveryRequest("轮回设备", deviceId: "android-loop");
        service.AuthorizeDevice(req1.RequestId);
        service.RevokeDevice("android-loop");

        var req2 = service.SubmitLanDiscoveryRequest("轮回设备", deviceId: "android-loop");
        var result = service.AuthorizeDevice(req2.RequestId);
        Assert.True(result.success);

        using var ctx = _factory.Object.CreateDbContext();
        var devices = ctx.AuthorizedDevices.Where(d => d.DeviceId == "android-loop").ToList();
        Assert.Single(devices); // 不新增重复记录
        Assert.Equal("Authorized", devices[0].Status);
    }

    [Fact]
    public void AuthorizeDevice_sameName_differentDeviceId_is_rejected()
    {
        // 名称碰撞防护：攻击者冒用他人设备名、不同 DeviceId 申请授权 → 拒绝且不泄露原令牌
        var service = CreateService();
        var req1 = service.SubmitLanDiscoveryRequest("同名设备", deviceId: "android-original");
        Assert.True(service.AuthorizeDevice(req1.RequestId).success);

        var req2 = service.SubmitLanDiscoveryRequest("同名设备", deviceId: "android-attacker");
        var result = service.AuthorizeDevice(req2.RequestId);
        Assert.False(result.success);
        Assert.Null(result.accessToken);
        Assert.NotNull(result.error);
        Assert.Contains("同名", result.error);

        using var ctx = _factory.Object.CreateDbContext();
        var devices = ctx.AuthorizedDevices
            .Where(d => d.DeviceName == "同名设备" && d.Status == "Authorized").ToList();
        Assert.Single(devices);
        Assert.Equal("android-original", devices[0].DeviceId);
    }

    [Fact]
    public void AuthorizeDevice_sameName_sameDeviceId_returns_existing_token()
    {
        // 同一物理设备重新配对（同 DeviceId）→ 返回现有令牌，不重复建记录
        var service = CreateService();
        var req1 = service.SubmitLanDiscoveryRequest("重配设备", deviceId: "android-repair");
        var first = service.AuthorizeDevice(req1.RequestId);
        Assert.True(first.success);

        var req2 = service.SubmitLanDiscoveryRequest("重配设备", deviceId: "android-repair");
        var second = service.AuthorizeDevice(req2.RequestId);
        Assert.True(second.success);
        Assert.Equal(first.accessToken, second.accessToken);

        using var ctx = _factory.Object.CreateDbContext();
        Assert.Single(ctx.AuthorizedDevices.Where(d => d.DeviceName == "重配设备" && d.Status == "Authorized").ToList());
    }

    [Fact]
    public void AutoAuthorize_sameName_differentDeviceId_is_rejected()
    {
        var service = CreateService();
        var (ok1, token1, _) = service.AutoAuthorizeDevice("自动设备", deviceId: "android-a1");
        Assert.True(ok1);

        // 同名不同 DeviceId：不自动授权（回退人工审批）
        var (ok2, token2, err2) = service.AutoAuthorizeDevice("自动设备", deviceId: "android-a2");
        Assert.False(ok2);
        Assert.Null(token2);
        Assert.NotNull(err2);

        // 同 DeviceId 重新自动授权 → 返回原令牌
        var (ok3, token3, _) = service.AutoAuthorizeDevice("自动设备", deviceId: "android-a1");
        Assert.True(ok3);
        Assert.Equal(token1, token3);
    }

    // ============ 一次性配对码 ============

    [Fact]
    public void PairCode_validate_success_rotates_code()
    {
        var service = CreateService();
        var code = service.GetPairCode();

        Assert.True(service.ValidatePairCode(code));
        Assert.NotEqual(code, service.GetPairCode()); // 一次性：验证后即轮换
        Assert.False(service.ValidatePairCode(code)); // 旧码立即失效
    }

    [Fact]
    public void PairCode_wrong_or_empty_does_not_rotate()
    {
        var service = CreateService();
        var code = service.GetPairCode();

        Assert.False(service.ValidatePairCode("WRONG-CODE"));
        Assert.False(service.ValidatePairCode(""));
        Assert.False(service.ValidatePairCode(null));
        Assert.Equal(code, service.GetPairCode()); // 未轮换
    }

    [Fact]
    public void PairCode_refresh_generates_new_code()
    {
        var service = CreateService();
        var old = service.GetPairCode();
        var fresh = service.RefreshPairCode();

        Assert.NotEqual(old, fresh);
        Assert.Equal(fresh, service.GetPairCode());
    }
}
