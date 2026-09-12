using Baihua.Data;
using Baihua.Modules.Family.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// FAM-20 静态契约红测试：家长看板重做（10 秒版）。
///
/// 验收标准覆盖（本轮：静态契约层）：
///   - AC1  第一屏三件事：今日三件事 / 连续打卡天数(火焰) / 最新成就(最近 3 个)
///   - AC2  第一屏空状态：引导 CTA（"添加第一个学习者" / "开始今天的学习吧"）
///   - AC3  连续打卡=家庭维度（任意成员有学习行为即算当天）
///   - AC4  第二屏成长时间线（30 天、倒序、分页 20 条）
///   - AC5  成员选择器（默认全部成员，可切单成员）——后端必须支持 learnerId 维度
///   - AC6  加载骨架屏 + 错误提示/重试
///
/// 红测试方式（与 FAM-01/11 先例一致）：
///   1) 后端契约：反射探测（方法签名/返回字段），当前 LeaderboardService.GetDashboardAsync
///      只接受 vaultId、返回 DashboardData 无任何新字段 → 红
///   2) 端点契约：/api/achievements/dashboard 的 GetDashboard action 无 learnerId 参数 → 红
///   3) 前端契约：源码级检查 Dashboard.razor（FAM-11 已允许），当前看板无成员选择器、
///      无"今日三件事"、无家庭连续打卡、无成长时间线、无骨架屏/重试 → 红
/// </summary>
public class Fam20DashboardContractTests : IDisposable
{
    private readonly SqliteConnection _familyConn;
    private readonly SqliteConnection _vaultConn;
    private readonly IDbContextFactory<FamilyDbContext> _familyFactory;
    private readonly IDbContextFactory<VaultDbContext> _vaultFactory;
    private readonly FakeTimeProvider _clock = FakeTimeProvider.Beijing20260807_0730();

    public Fam20DashboardContractTests()
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

    private Fam20DashboardProbe.DashboardSnapshot GetSnapshot()
    {
        var svc = Fam20DashboardProbe.CreateService(_familyFactory, _vaultFactory, _clock, out var error);
        Assert.NotNull(svc);
        Assert.Null(error);
        return Fam20DashboardProbe.GetSnapshot(svc!, "vault-fam20", learnerId: null);
    }

    // ============ AC5：成员筛选（后端方法契约） ============

    [Fact]
    public void DashboardService_MustSupportLearnerFilter()
    {
        // 契约：看板数据方法必须接受 learnerId 参数（null=全部成员，非 null=单成员维度）
        // 当前 LeaderboardService.GetDashboardAsync(string? vaultId) 无 learnerId → 红
        Assert.True(Fam20DashboardProbe.HasLearnerFilteredDashboardMethod(),
            "FAM-20-AC5 契约：看板数据方法缺少 learnerId 参数（期望 GetDashboardAsync(vaultId, learnerId)）（红）");
    }

    [Fact]
    public void DashboardEndpoint_MustAcceptLearnerIdQuery()
    {
        // 契约：/api/achievements/dashboard 端点必须接受 learnerId 查询参数（WebUI 成员选择器依赖）
        // 当前 GetDashboard([FromQuery] string? vaultId) 无 learnerId → 红
        Assert.True(Fam20DashboardProbe.HasDashboardEndpointWithLearnerFilter(),
            "FAM-20-AC5 契约：看板端点缺少 learnerId 查询参数（红）");
    }

    // ============ AC1：今日三件事字段契约 ============

    [Fact]
    public void DashboardData_MustExposeTodayHighlights()
    {
        // 契约：看板结果必须有 TodayCompleted / YesterdayCompleted / TrendArrow / TodayActivities
        // 当前 DashboardData 只有 FamilyStats/WeeklyTrend/RecentAchievements/ResultDistribution → 红
        var snap = GetSnapshot();
        Assert.True(snap.ContractPresent, $"FAM-20-AC1 契约缺失: {snap.MissingDetail}");
        Assert.True(snap.TodayCompleted >= 0 && snap.YesterdayCompleted >= 0,
            "FAM-20-AC1：今日/昨日完成卡片数必须存在且为非负整数（红）");
        Assert.Contains(snap.TrendArrow,
            new[] { Fam20DashboardProbe.TrendUp, Fam20DashboardProbe.TrendDown, Fam20DashboardProbe.TrendFlat, Fam20DashboardProbe.TrendNone },
            StringComparer.Ordinal);
    }

    [Fact]
    public void DashboardData_TodayActivities_MustHaveLearnerAndDescription()
    {
        var snap = GetSnapshot();
        Assert.True(snap.ContractPresent, $"FAM-20-AC1 契约缺失: {snap.MissingDetail}");
        // 契约：今日三件事条目 = Learner 名 + 学习内容描述（"谁 + 做了什么"）
        Assert.All(snap.TodayActivities, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.LearnerName), "FAM-20-AC1：今日学习条目缺少 Learner 名（红）");
            Assert.False(string.IsNullOrWhiteSpace(a.Description), "FAM-20-AC1：今日学习条目缺少内容描述（红）");
        });
    }

    // ============ AC3：家庭维度连续打卡字段契约 ============

    [Fact]
    public void DashboardData_MustExposeFamilyStreak()
    {
        var snap = GetSnapshot();
        Assert.True(snap.ContractPresent, $"FAM-20-AC3 契约缺失: {snap.MissingDetail}");
        Assert.True(snap.FamilyStreak >= 0,
            "FAM-20-AC3：看板结果缺少家庭维度连续打卡天数 FamilyStreak（红）");
    }

    // ============ AC1：最新成就字段契约 ============

    [Fact]
    public void DashboardData_LatestAchievements_MustBeAtMostThree()
    {
        var snap = GetSnapshot();
        Assert.True(snap.ContractPresent, $"FAM-20-AC1 契约缺失: {snap.MissingDetail}");
        // 契约：最新成就最多 3 个，且每条有标题/图标/解锁时间
        Assert.True(snap.LatestAchievements.Count <= 3,
            $"FAM-20-AC1：最新成就最多显示 3 个（当前 {snap.LatestAchievements.Count}）（红）");
        Assert.All(snap.LatestAchievements, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Title), "FAM-20-AC1：成就条目缺少标题（红）");
            Assert.True(a.UnlockedAt.HasValue, "FAM-20-AC1：成就条目缺少解锁时间 UnlockedAt（红）");
        });
    }

    // ============ AC4：成长时间线字段契约 ============

    [Fact]
    public void DashboardData_MustExposeGrowthTimeline()
    {
        var snap = GetSnapshot();
        Assert.True(snap.ContractPresent, $"FAM-20-AC4 契约缺失: {snap.MissingDetail}");
        Assert.All(snap.GrowthTimeline, e =>
        {
            Assert.True(e.Date.HasValue, "FAM-20-AC4：时间线条目缺少日期（红）");
            Assert.False(string.IsNullOrWhiteSpace(e.LearnerName), "FAM-20-AC4：时间线条目缺少 Learner 名（红）");
            Assert.False(string.IsNullOrWhiteSpace(e.Description), "FAM-20-AC4：时间线条目缺少事件描述（红）");
        });
    }

    [Fact]
    public void GrowthTimeline_MustSupportPagingOf20()
    {
        // 契约：时间线分页每页 20 条（PageSize 属性或分页方法参数）
        var snap = GetSnapshot();
        Assert.True(snap.ContractPresent, $"FAM-20-AC4 契约缺失: {snap.MissingDetail}");
        Assert.Equal(Fam20DashboardProbe.TimelinePageSize, snap.PageSize);
    }

    // ============ AC6：看板服务时间源可注入（时区语义依赖，延续 FAM-01） ============

    [Fact]
    public void DashboardService_MustAcceptInjectableTimeSource()
    {
        var (injectable, compatible) = TimeSourceProbe.Probe(Fam20DashboardProbe.FindDashboardServiceType());
        Assert.True(injectable,
            "FAM-20-AC6/FAM-01：看板服务必须接受可注入时间源（ITimeProvider 参数）——家庭维度打卡/今日对比按北京时间计算（红）");
        Assert.True(compatible, "FAM-20：看板服务时间注入点必须与 ITimeProvider 兼容（红）");
    }

    // ============ 前端源码级契约（FAM-11 先例：源码=路由与结构的事实源） ============

    private static readonly string DashboardPagePath = RepoPath.FindUp(Path.Combine(
        "services", "Baihua.Web", "Pages", "Dashboard.razor"));

    /// <summary>
    /// 读取看板前端源码：主页面 + 建议拆分的子组件（DashboardFirstScreen/GrowthTimeline/MemberSelector）。
    /// 组件存在则并入检查，保证"整体拆分"或"单体页面"两种实现都能被覆盖。
    /// </summary>
    private static string ReadDashboardSource()
    {
        Assert.True(File.Exists(DashboardPagePath),
            "FAM-20 契约：Pages/Dashboard.razor 不存在（红）——家长看板页面必须存在");
        var sb = new System.Text.StringBuilder(File.ReadAllText(DashboardPagePath));

        var webRoot = Path.GetDirectoryName(DashboardPagePath)!;
        var componentsRoot = Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(DashboardPagePath))!, "Components");
        foreach (var name in new[] { "DashboardFirstScreen.razor", "GrowthTimeline.razor", "MemberSelector.razor" })
        {
            foreach (var root in new[] { webRoot, componentsRoot })
            {
                var candidate = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault();
                if (candidate != null)
                {
                    sb.AppendLine();
                    sb.AppendLine($"<!-- {name} -->");
                    sb.AppendLine(File.ReadAllText(candidate));
                }
            }
        }
        return sb.ToString();
    }

    [Fact]
    public void DashboardPage_HasMemberSelector()
    {
        // AC5：看板顶部成员选择器（默认"全部成员"）
        var source = ReadDashboardSource();
        Assert.True(
            source.Contains("全部成员", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("member-selector", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("MemberSelector", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("learner-selector", StringComparison.OrdinalIgnoreCase),
            "FAM-20-AC5：看板缺少成员选择器（全部成员/单成员切换）（红）");
    }

    [Fact]
    public void DashboardPage_FirstScreen_ShowsTodayThreeItems()
    {
        // AC1：第一屏"今日三件事"（Learner + 做了什么 + 数量 + 趋势）
        var source = ReadDashboardSource();
        Assert.True(
            source.Contains("今日三件事", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("TodayHighlights", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("today-highlights", StringComparison.OrdinalIgnoreCase),
            "FAM-20-AC1：第一屏缺少'今日三件事'区域（红）");
    }

    [Fact]
    public void DashboardPage_FirstScreen_ShowsFamilyStreakWithFlame()
    {
        // AC1：连续打卡天数 + 火焰/星星激励图标
        var source = ReadDashboardSource();
        var hasStreakLabel =
            source.Contains("连续打卡", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("FamilyStreak", StringComparison.OrdinalIgnoreCase);
        var hasFlame =
            source.Contains("🔥", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("flame", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("火焰", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasStreakLabel && hasFlame,
            "FAM-20-AC1：第一屏缺少'连续打卡天数 + 火焰图标'（红）");
    }

    [Fact]
    public void DashboardPage_FirstScreen_ShowsLatestAchievementsWithRelativeTime()
    {
        // AC1：最新成就（1-3 个）+ 解锁时间相对文案（"X 分钟前/今天"）
        var source = ReadDashboardSource();
        var hasAchievements =
            source.Contains("成就", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Achievement", StringComparison.OrdinalIgnoreCase);
        var hasRelativeTime =
            source.Contains("分钟前", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("小时前", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("昨天", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("RelativeTime", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasAchievements && hasRelativeTime,
            "FAM-20-AC1：第一屏缺少'最新成就 + 相对解锁时间（X 分钟前/今天）'（红）");
    }

    [Fact]
    public void DashboardPage_ShowsEmptyStateCta_WhenNoData()
    {
        // AC2：无 Learner/无学习记录时显示引导 CTA（不空白）
        var source = ReadDashboardSource();
        Assert.True(
            source.Contains("添加第一个学习者", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("开始今天的学习吧", StringComparison.OrdinalIgnoreCase),
            "FAM-20-AC2：空状态缺少引导 CTA（'添加第一个学习者'或'开始今天的学习吧'）（红）");
    }

    [Fact]
    public void DashboardPage_SecondScreen_ShowsGrowthTimeline()
    {
        // AC4：第二屏成长时间线（30 天事件流）
        var source = ReadDashboardSource();
        Assert.True(
            source.Contains("成长时间线", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("GrowthTimeline", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("growth-timeline", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("timeline", StringComparison.OrdinalIgnoreCase),
            "FAM-20-AC4：第二屏缺少'成长时间线'区域（红）");
    }

    [Fact]
    public void DashboardPage_HasLoadingSkeleton()
    {
        // AC6：加载中显示骨架屏（不闪烁）
        var source = ReadDashboardSource();
        Assert.True(
            source.Contains("skeleton", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("骨架", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Skeleton", StringComparison.OrdinalIgnoreCase),
            "FAM-20-AC6：加载状态缺少骨架屏（skeleton）（红）");
    }

    [Fact]
    public void DashboardPage_HasErrorRetry()
    {
        // AC6：请求失败显示错误提示 + 重试按钮
        var source = ReadDashboardSource();
        Assert.True(
            source.Contains("retry", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("重试", StringComparison.OrdinalIgnoreCase),
            "FAM-20-AC6：错误状态缺少'重试'按钮（红）");
    }
}
