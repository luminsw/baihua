using Baihua.Data;
using Baihua.Modules.Family.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// FAM-33 静态契约红测试：补签 + 连击保护。
///
/// 验收标准覆盖（本轮：静态契约层）：
///   - AC1  补签机制：存在补签方法（3 天窗口）
///   - AC2  补签次数限制：每月最多 3 次，超出提示
///   - AC3  无学习记录不可补签（可补签格子标记）
///   - AC4  连击保护：单日中断不归零（今天还没学/宽限期），超保护期归零
///
/// 红测试方式（FAM-21 同套路）：当前 CheckinService 只有 GetCheckinDataAsync +
/// 无保护的锚点 streak，无补签/保护字段 → 红。
/// </summary>
public class Fam33CheckinContractTests : IDisposable
{
    private readonly SqliteConnection _familyConn;
    private readonly SqliteConnection _vaultConn;
    private readonly IDbContextFactory<FamilyDbContext> _familyFactory;
    private readonly IDbContextFactory<VaultDbContext> _vaultFactory;
    private readonly FakeTimeProvider _clock = FakeTimeProvider.Beijing20260807_0730();

    public Fam33CheckinContractTests()
    {
        _familyConn = new SqliteConnection("DataSource=:memory:");
        _familyConn.Open();
        var familyOptions = new DbContextOptionsBuilder<FamilyDbContext>()
            .UseSqlite(_familyConn).Options;
        using (var ctx = new FamilyDbContext(familyOptions)) ctx.Database.EnsureCreated();
        _familyFactory = new FakeDbFactory<FamilyDbContext>(() => new FamilyDbContext(familyOptions));

        _vaultConn = new SqliteConnection("DataSource=:memory:");
        _vaultConn.Open();
        var vaultOptions = new DbContextOptionsBuilder<VaultDbContext>()
            .UseSqlite(_vaultConn).Options;
        using (var ctx = new VaultDbContext(vaultOptions)) ctx.Database.EnsureCreated();
        _vaultFactory = new FakeDbFactory<VaultDbContext>(() => new VaultDbContext(vaultOptions));
    }

    public void Dispose()
    {
        _familyConn.Dispose();
        _vaultConn.Dispose();
    }

    // ============ 后端契约 ============

    [Fact]
    public void CheckinService_MustHaveMakeupMethod()
    {
        // AC1：补签方法（名称含 Makeup，参数含日期）
        Assert.True(Fam33MakeupProbe.HasMakeupMethod(),
            "FAM-33-AC1 契约：CheckinService 缺少补签方法（期望 MakeupCheckinAsync(date, vaultId) 或等价）（红）");
    }

    [Fact]
    public void CheckinData_MustExposeMakeupQuota()
    {
        // AC2：每月补签次数（剩余次数，默认月 3 次）
        var snap = GetSnapshot();
        Assert.True(snap.ContractPresent, $"FAM-33-AC2 契约缺失: {snap.MissingDetail}");
        Assert.True(snap.MakeupRemaining >= 0,
            "FAM-33-AC2：打卡数据缺少每月剩余补签次数 MakeupRemaining（红）");
    }

    [Fact]
    public void CheckinCalendar_MustMarkMakeupableDays()
    {
        // AC3：日历格子标记可补签（该日有学习记录未打卡才可补）
        var snap = GetSnapshot();
        Assert.True(snap.ContractPresent, $"FAM-33-AC3 契约缺失: {snap.MissingDetail}");
        Assert.All(snap.Last7Days, c =>
        {
            // 已打卡格子不可补签；可补签格子的语义由行为测试锁定
            Assert.False(c.IsChecked && c.IsMakeupable,
                "FAM-33-AC3：已打卡（🔥）格子不应标记为可补签（红）");
        });
    }

    [Fact]
    public void CheckinData_MustExposeStreakProtection()
    {
        // AC4：连击保护状态（今天还没学/已中断 N 天）
        Assert.True(Fam33MakeupProbe.HasStreakProtectionField(),
            "FAM-33-AC4 契约：打卡数据缺少连击保护状态字段（StreakStatus/Protection）（红）");
    }

    // ============ 前端源码级契约（Checkin.razor 扩展） ============

    private static readonly string CheckinPagePath = RepoPath.FindUp(Path.Combine(
        "services", "Baihua.Web", "Pages", "Checkin.razor"));

    private static string ReadCheckinSource()
    {
        Assert.True(File.Exists(CheckinPagePath),
            "FAM-33 契约：Pages/Checkin.razor 不存在（红）");
        return File.ReadAllText(CheckinPagePath);
    }

    [Fact]
    public void CheckinPage_HasMakeupEntry()
    {
        // AC1 UI：日历中可补签格显示补签入口（虚线边框 + "补"字提示）
        var source = ReadCheckinSource();
        Assert.True(
            source.Contains("补签", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Makeup", StringComparison.OrdinalIgnoreCase),
            "FAM-33-AC1：打卡页缺少补签入口（红）");
    }

    [Fact]
    public void CheckinPage_HasMakeupConfirmDialog_WithQuota()
    {
        // 补签确认弹窗：显示日期 + 本月剩余次数（页面已 i18n 化 → 断言 resx key）
        var source = ReadCheckinSource();
        Assert.True(
            source.Contains("Checkin_MakeupQuota", StringComparison.OrdinalIgnoreCase),
            "FAM-33：补签确认弹窗缺少'本月剩余补签次数'提示 key（Checkin_MakeupQuota）（红）");
        Assert.True(
            source.Contains("本月补签次数已用完", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("已用完", StringComparison.OrdinalIgnoreCase),
            "FAM-33-AC2：缺少'本月补签次数已用完'提示（红）");
    }

    [Fact]
    public void CheckinPage_HasStreakGraceText()
    {
        // AC4 UI：连击保护文案（"连续打卡 X 天（今天还没学）" / "已中断 1 天，明天前补学可恢复"）
        var source = ReadCheckinSource();
        Assert.True(
            source.Contains("今天还没学", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("已中断", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("可恢复", StringComparison.OrdinalIgnoreCase),
            "FAM-33-AC4：打卡页缺少连击保护文案（今天还没学/已中断可恢复）（红）");
    }

    private Fam33MakeupProbe.CheckinSnapshot GetSnapshot()
    {
        var svc = Fam33MakeupProbe.CreateService(_familyFactory, _vaultFactory, _clock, out var error);
        Assert.True(svc is not null, error ?? "未知错误");
        Assert.True(error is null, error);
        return Fam33MakeupProbe.GetSnapshot(svc!, "vault-fam33");
    }
}
