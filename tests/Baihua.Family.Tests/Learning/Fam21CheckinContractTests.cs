using Baihua.Data;
using Baihua.Modules.Family.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// FAM-21 静态契约红测试：学习打卡页。
///
/// 验收标准覆盖（本轮：静态契约层）：
///   - AC1  今日学习清单：按 Learner 分组，每条=内容名称+学习时间+完成状态 ✅
///   - AC2  空状态引导：文案 + CTA"前往每日卡片"（跳 /daily-card），连续打卡显示 0
///   - AC3  关联 StudyActivity 详情：开始/结束时间、学习卡片数、正确率、来源标签（可追溯）
///   - AC4  打卡日历：最近 7 天（🔥/⬜）+ 今天高亮；连续打卡天数从今天往前数
///   - AC5  时区：北京时间 00:00 为日期边界（行为用例在 Fam21TimezoneBoundaryTests）
///   - AC6  加载状态 + 错误提示/重试
///
/// 红测试方式（FAM-20 同套路）：
///   1) 后端契约：反射探测（服务类/方法/字段），当前无 CheckinService → 红
///   2) 端点契约：无打卡数据端点 → 红
///   3) 前端契约：源码级检查 Checkin.razor + FamilyNavMenu.razor（FAM-11 先例），当前页面不存在 → 红
/// </summary>
public class Fam21CheckinContractTests : IDisposable
{
    private readonly SqliteConnection _familyConn;
    private readonly SqliteConnection _vaultConn;
    private readonly IDbContextFactory<FamilyDbContext> _familyFactory;
    private readonly IDbContextFactory<VaultDbContext> _vaultFactory;
    private readonly FakeTimeProvider _clock = FakeTimeProvider.Beijing20260807_0730();

    public Fam21CheckinContractTests()
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

    private Fam21CheckinProbe.CheckinSnapshot GetSnapshot()
    {
        var svc = Fam21CheckinProbe.CreateService(_familyFactory, _vaultFactory, _clock, out var error);
        Assert.True(svc is not null, error ?? "未知错误");
        Assert.True(error is null, error);
        return Fam21CheckinProbe.GetSnapshot(svc!, "vault-fam21");
    }

    // ============ AC1/AC3/AC4：后端契约 ============

    [Fact]
    public void CheckinService_MustExist()
    {
        // 契约：存在打卡服务类（CheckinService 或名称含 Checkin 的 Service）
        Assert.NotNull(Fam21CheckinProbe.FindCheckinServiceType());
    }

    [Fact]
    public void CheckinService_MustHaveDataMethod()
    {
        // 契约：存在返回打卡数据的方法（GetCheckinDataAsync 或等价）
        Assert.True(Fam21CheckinProbe.HasCheckinDataMethod(),
            "FAM-21 契约：打卡服务缺少数据方法（期望 GetCheckinDataAsync(vaultId) 或等价）（红）");
    }

    [Fact]
    public void CheckinEndpoint_MustExist()
    {
        // 契约：存在打卡数据端点（名称含 Checkin 的 GET action）
        Assert.True(Fam21CheckinProbe.HasCheckinEndpoint(),
            "FAM-21 契约：缺少打卡数据端点（期望 /api/checkin 或等价 GET action）（红）");
    }

    [Fact]
    public void CheckinService_MustAcceptInjectableTimeSource()
    {
        // AC5：打卡按北京自然日判定，服务必须可注入时间源（延续 FAM-01）
        var type = Fam21CheckinProbe.FindCheckinServiceType();
        Assert.NotNull(type);
        var (injectable, compatible) = TimeSourceProbe.Probe(type!);
        Assert.True(injectable,
            "FAM-21-AC5：打卡服务必须接受可注入时间源（ITimeProvider 参数）（红）");
        Assert.True(compatible, "FAM-21-AC5：打卡服务时间注入点必须与 ITimeProvider 兼容（红）");
    }

    [Fact]
    public void CheckinData_MustExposeTodayRecordsGroupedByLearner()
    {
        // AC1：今日学习清单——按 Learner 分组，每条含 Learner 名 + 内容名称 + 学习时间 + 完成状态
        var snap = GetSnapshot();
        Assert.True(snap.ContractPresent, $"FAM-21-AC1 契约缺失: {snap.MissingDetail}");
        Assert.All(snap.TodayRecords, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.LearnerName), "FAM-21-AC1：记录缺少 Learner 名（红）");
            Assert.False(string.IsNullOrWhiteSpace(r.Content), "FAM-21-AC1：记录缺少学习内容名称（红）");
            Assert.True(r.Time.HasValue, "FAM-21-AC1：记录缺少学习时间（红）");
        });
    }

    [Fact]
    public void CheckinData_MustExposeFamilyStreak()
    {
        // AC2/AC4：连续打卡天数（从今天往前连续有记录的天数）
        var snap = GetSnapshot();
        Assert.True(snap.ContractPresent, $"FAM-21-AC2/AC4 契约缺失: {snap.MissingDetail}");
        Assert.True(snap.FamilyStreak >= 0,
            "FAM-21：缺少连续打卡天数 FamilyStreak（红）");
    }

    [Fact]
    public void CheckinData_MustExposeLast7DaysCalendar()
    {
        // AC4：最近 7 天打卡日历（7 格：日期 + 是否打卡 + 是否今天）
        var snap = GetSnapshot();
        Assert.True(snap.ContractPresent, $"FAM-21-AC4 契约缺失: {snap.MissingDetail}");
        Assert.Equal(7, snap.Last7Days.Count);
        Assert.True(snap.Last7Days.Count(d => d.IsToday) == 1,
            "FAM-21-AC4：7 天日历中必须恰好有 1 格标记为今天（红）");
    }

    [Fact]
    public void CheckinRecord_MustHaveTraceableSource()
    {
        // AC3：来源可追溯——记录必须带来源标签（每日卡片/自由学习/复习模式），不是凭空生成
        var snap = GetSnapshot();
        Assert.True(snap.ContractPresent, $"FAM-21-AC3 契约缺失: {snap.MissingDetail}");
        Assert.All(snap.TodayRecords, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Source),
                "FAM-21-AC3：记录缺少来源标签 Source（每日卡片/自由学习/复习模式）（红）");
        });
    }

    [Fact]
    public void CheckinRecord_MustExposeDetailFields()
    {
        // AC3：展开详情——开始/结束时间、学习卡片数、正确率
        var snap = GetSnapshot();
        Assert.True(snap.ContractPresent, $"FAM-21-AC3 契约缺失: {snap.MissingDetail}");
        Assert.All(snap.TodayRecords, r =>
        {
            Assert.True(r.StartTime.HasValue, "FAM-21-AC3：详情缺少开始时间（红）");
            Assert.True(r.EndTime.HasValue, "FAM-21-AC3：详情缺少结束时间（红）");
            Assert.True(r.CardCount >= 0, "FAM-21-AC3：详情缺少学习卡片数（红）");
            Assert.True(r.Accuracy >= 0 && r.Accuracy <= 100, "FAM-21-AC3：详情缺少正确率（0-100）（红）");
        });
    }

    // ============ AC2/AC6：前端源码级契约 ============

    private static readonly string CheckinPagePath = RepoPath.FindUp(Path.Combine(
        "services", "Baihua.Web", "Pages", "Checkin.razor"));

    private static readonly string NavMenuPath = RepoPath.FindUp(Path.Combine(
        "services", "Baihua.Web", "Shared", "FamilyNavMenu.razor"));

    private static string ReadCheckinSource()
    {
        Assert.True(File.Exists(CheckinPagePath),
            "FAM-21 契约：Pages/Checkin.razor 不存在（红）——需要新建学习打卡页");
        return File.ReadAllText(CheckinPagePath);
    }

    [Fact]
    public void CheckinPage_Exists_WithFamilyRoute()
    {
        // 路由契约：/family/checkin 或等价路由可达（Blazor 声明式路由）
        var source = ReadCheckinSource();
        Assert.True(
            source.Contains("@page \"/family/checkin\"", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("@page \"/checkin\"", StringComparison.OrdinalIgnoreCase),
            "FAM-21：页面缺少路由声明（@page \"/family/checkin\" 或 @page \"/checkin\"）（红）");
    }

    [Fact]
    public void FamilyNavMenu_HasCheckinEntry_InFamilyScene()
    {
        // 菜单契约：家庭分类（Scene=1）新增"学习打卡"入口（href=checkin）
        Assert.True(File.Exists(NavMenuPath), $"找不到 FamilyNavMenu.razor（路径: {NavMenuPath}）");
        var lines = File.ReadAllLines(NavMenuPath);

        var checkinItem = lines
            .Select(l => System.Text.RegularExpressions.Regex.Match(
                l.Trim(), @"new\(""([^""]+)""[^)]*,\s*1\s*[,)]"))
            .FirstOrDefault(m => m.Success && m.Groups[1].Value.Contains("checkin", StringComparison.OrdinalIgnoreCase));

        Assert.True(checkinItem != null,
            "FAM-21：家庭场景（Scene=1）菜单缺少 checkin 入口（红）");
    }

    [Fact]
    public void CheckinPage_HasTodayListGroupedByLearner()
    {
        // AC1：今日学习清单按 Learner 分组展示
        var source = ReadCheckinSource();
        Assert.True(
            source.Contains("Learner", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("group", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("分组", StringComparison.OrdinalIgnoreCase),
            "FAM-21-AC1：页面缺少按 Learner 分组的学习清单（红）");
    }

    [Fact]
    public void CheckinPage_HasEmptyStateCta_ToDailyCard()
    {
        // AC2：空状态引导 + CTA"前往每日卡片"（跳转 /daily-card，页面已 i18n 化 → 断言 resx key）
        var source = ReadCheckinSource();
        Assert.True(
            source.Contains("Checkin_GoDailyCard", StringComparison.OrdinalIgnoreCase),
            "FAM-21-AC2：空状态缺少引导 CTA key（Checkin_GoDailyCard）（红）");
        Assert.True(
            source.Contains("/daily-card", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("daily-card", StringComparison.OrdinalIgnoreCase),
            "FAM-21-AC2：引导 CTA 必须跳转 /daily-card（红）");
    }

    [Fact]
    public void CheckinPage_HasStreakAnd7DayCalendar()
    {
        // AC4：连续打卡天数 + 最近 7 天日历（实心🔥/空心⬜/今天高亮）
        var source = ReadCheckinSource();
        Assert.True(
            source.Contains("连续打卡", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Streak", StringComparison.OrdinalIgnoreCase),
            "FAM-21-AC4：页面缺少连续打卡天数（红）");
        var hasCalendarMarkers =
            source.Contains("🔥", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("⬜", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("calendar", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("日历", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasCalendarMarkers,
            "FAM-21-AC4：页面缺少 7 天打卡日历（🔥/⬜ 标记）（红）");
    }

    [Fact]
    public void CheckinPage_HasExpandableDetail_WithSource()
    {
        // AC3：记录可点击展开，显示 StudyActivity 详情（含来源标签）
        var source = ReadCheckinSource();
        Assert.True(
            source.Contains("expand", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("detail", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("详情", StringComparison.OrdinalIgnoreCase),
            "FAM-21-AC3：页面缺少可展开的详情区域（红）");
        Assert.True(
            source.Contains("Source", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("来源", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("复习模式", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("每日卡片", StringComparison.OrdinalIgnoreCase),
            "FAM-21-AC3：详情缺少来源标签（每日卡片/自由学习/复习模式）（红）");
    }

    [Fact]
    public void CheckinPage_HasLoadingState()
    {
        // AC6：加载中显示 loading 状态
        var source = ReadCheckinSource();
        Assert.True(
            source.Contains("loading", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("isLoading", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("skeleton", StringComparison.OrdinalIgnoreCase),
            "FAM-21-AC6：页面缺少 loading 状态（红）");
    }

    [Fact]
    public void CheckinPage_HasErrorRetry()
    {
        // AC6：请求失败显示错误提示 + 重试
        var source = ReadCheckinSource();
        Assert.True(
            source.Contains("retry", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("重试", StringComparison.OrdinalIgnoreCase),
            "FAM-21-AC6：错误状态缺少'重试'按钮（红）");
    }
}
