using Baihua.Data;
using Baihua.Modules.Family.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// FAM-22 静态契约红测试：排行榜友好化。
///
/// 验收标准覆盖（本轮：静态契约层）：
///   - AC1  默认"和自己比"视图（本周 vs 上周），页面首次加载默认选中而非全家庭排行
///   - AC2  和自己比数据：本周/上周完成数 + 变化量（↑↓ + 百分比）
///   - AC3  大人/孩子分榜机制（TECH-08 未完成需兜底，但必须存在角色判定/分组机制）
///   - AC4  全家排行 Tab 可关闭（设置开关）
///   - AC5  全家排行默认关闭
///   - AC6  空状态/单人引导（"邀请更多小伙伴一起学吧"）
///
/// 红测试方式（FAM-20/21 同套路）：
///   1) 后端契约：反射探测，当前 LeaderboardService 无"和自己比"方法/角色分组/设置 → 红
///   2) 前端契约：源码级检查 Leaderboard.razor（改造现有页面），当前无"和自己比"视图 → 红
/// </summary>
public class Fam22LeaderboardContractTests : IDisposable
{
    private readonly SqliteConnection _familyConn;
    private readonly SqliteConnection _vaultConn;
    private readonly IDbContextFactory<FamilyDbContext> _familyFactory;
    private readonly IDbContextFactory<VaultDbContext> _vaultFactory;
    private readonly FakeTimeProvider _clock = FakeTimeProvider.Beijing20260807_0730();

    public Fam22LeaderboardContractTests()
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

    // ============ AC1/AC2：和自己比（后端契约） ============

    [Fact]
    public void LeaderboardService_MustHaveWeeklyCompareMethod()
    {
        // 契约：存在"和自己比"（本周 vs 上周）方法，参数含 learnerId（按成员维度）
        // 当前 LeaderboardService 只有 GetWeeklyLeaderboardAsync(vaultId) → 红
        Assert.True(Fam22LeaderboardProbe.HasWeeklyCompareMethod(),
            "FAM-22-AC1/AC2 契约：排行榜服务缺少'和自己比'方法（期望 GetWeeklyCompareAsync(vaultId, learnerId) 或等价）（红）");
    }

    [Fact]
    public void WeeklyCompare_MustExposeCompareFields()
    {
        // 契约：对比结果含本周/上周完成数 + 差值 + 百分比 + 趋势箭头
        var svc = new LeaderboardService(_familyFactory, _clock);
        var snap = Fam22LeaderboardProbe.GetCompareSnapshot(svc, "vault-fam22", learnerId: 1);

        Assert.True(snap.ContractPresent, $"FAM-22-AC2 契约缺失: {snap.MissingDetail}");
        Assert.True(snap.WeekTotal >= 0 && snap.LastWeekTotal >= 0,
            "FAM-22-AC2：本周/上周完成数必须为非负整数（红）");
        Assert.Contains(snap.Arrow,
            new[] { "up", "down", "flat", "" },
            StringComparer.Ordinal);
    }

    // ============ AC3：大人/孩子分榜（后端契约） ============

    [Fact]
    public void Leaderboard_MustSupportRoleBoards()
    {
        // 契约：排行榜必须支持按角色分组/过滤（孩子榜/大人榜）
        Assert.True(Fam22LeaderboardProbe.HasRoleGroupedLeaderboard(),
            "FAM-22-AC3 契约：排行榜缺少角色分组机制（期望 role 参数或 Kids/Adults 分组结构）（红）");
    }

    [Fact]
    public void Learner_RoleDetermination_MechanismMustExist()
    {
        // 契约：角色判定机制必须存在——LearnerProfile 有 Role/年龄字段（TECH-08），
        // 或排行榜支持角色过滤/分组（TECH-08 未完成时的兜底方案）
        Assert.True(Fam22LeaderboardProbe.HasLearnerRoleMechanism(),
            "FAM-22-AC3 契约：缺少大人/孩子判定机制（Learner.Role 字段或角色过滤/分组兜底）（红）");
    }

    // ============ AC4/AC5：全家排行开关（后端契约） ============

    [Fact]
    public void AllFamilyTab_Setting_MustBePersistable()
    {
        // 契约：全家排行开关必须可持久化（设置服务，存 DB 或浏览器存储）
        Assert.NotNull(Fam22LeaderboardProbe.FindAllFamilyTabSettingType());
    }

    // ============ 前端源码级契约（Leaderboard.razor） ============

    private static readonly string LeaderboardPagePath = RepoPath.FindUp(Path.Combine(
        "services", "Baihua.Web", "Pages", "Leaderboard.razor"));

    private static string ReadLeaderboardSource()
    {
        Assert.True(File.Exists(LeaderboardPagePath),
            "FAM-22 契约：Pages/Leaderboard.razor 不存在（红）——排行榜页面必须存在（改造现有页面）");
        return File.ReadAllText(LeaderboardPagePath);
    }

    [Fact]
    public void LeaderboardPage_HasViewToggle_DefaultSelfCompare()
    {
        // AC1：视图切换"和自己比"/"家庭排行"，默认选中"和自己比"
        var source = ReadLeaderboardSource();
        Assert.True(
            source.Contains("和自己比", StringComparison.OrdinalIgnoreCase),
            "FAM-22-AC1：缺少'和自己比'视图切换按钮（红）");
        Assert.True(
            source.Contains("家庭排行", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("FamilyBoard", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("family-rank", StringComparison.OrdinalIgnoreCase),
            "FAM-22-AC1：缺少'家庭排行'视图切换按钮（红）");
        // 默认选中"和自己比"：初始 active 状态不是"家庭排行"
        var selfCompareActive =
            RegexActiveFirst(source, "和自己比") ||
            source.Contains("activeTab = self", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("activeView = self", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("SelfCompare", StringComparison.OrdinalIgnoreCase);
        Assert.True(selfCompareActive,
            "FAM-22-AC1：默认视图必须是'和自己比'（红）");
    }

    [Fact]
    public void LeaderboardPage_HasSelfCompareView_WithChangePercent()
    {
        // AC1/AC2：和自己比视图显示本周完成数、上周完成数、变化量（↑↓ + 百分比）
        var source = ReadLeaderboardSource();
        Assert.True(
            source.Contains("上周", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("LastWeek", StringComparison.OrdinalIgnoreCase),
            "FAM-22-AC2：和自己比视图缺少'上周'完成数（红）");
        Assert.True(
            source.Contains("%", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Percent", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("百分比", StringComparison.OrdinalIgnoreCase),
            "FAM-22-AC2：变化量必须显示百分比（红）");
    }

    [Fact]
    public void LeaderboardPage_HasKidsAndAdultsTabs()
    {
        // AC3：家庭排行视图含"孩子榜"/"大人榜"Tab
        var source = ReadLeaderboardSource();
        Assert.True(
            source.Contains("孩子榜", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("KidsTab", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("kids", StringComparison.OrdinalIgnoreCase),
            "FAM-22-AC3：缺少'孩子榜'Tab（红）");
        Assert.True(
            source.Contains("大人榜", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("AdultsTab", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("adults", StringComparison.OrdinalIgnoreCase),
            "FAM-22-AC3：缺少'大人榜'Tab（红）");
    }

    [Fact]
    public void LeaderboardPage_AllFamilyTab_ConditionallyRendered()
    {
        // AC4/AC5：全家 Tab 条件渲染（默认关闭）——不能是无条件显示的静态 Tab
        var source = ReadLeaderboardSource();
        Assert.True(
            source.Contains("全家", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("AllFamily", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("all-family", StringComparison.OrdinalIgnoreCase),
            "FAM-22-AC4：缺少'全家排行'Tab（红）");
        // 必须有条件渲染逻辑（@if 或设置开关变量），不能无条件显示
        var conditional =
            source.Contains("@if", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("ShowAllFamily", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("allFamilyEnabled", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("showAllFamily", StringComparison.OrdinalIgnoreCase);
        Assert.True(conditional,
            "FAM-22-AC4/AC5：'全家'Tab 必须条件渲染（默认关闭，需设置开关）（红）");
    }

    [Fact]
    public void LeaderboardPage_HasSoloGuideText()
    {
        // AC6：单人/空榜时显示引导文案"邀请更多小伙伴一起学吧"
        // 页面已本地化：契约断言 resx key 引用 + 中性 resx（中文单语言，已无 zh-CN 附属资源）中的实际文案
        var source = ReadLeaderboardSource();
        Assert.True(
            source.Contains("Leaderboard_InviteHint", StringComparison.OrdinalIgnoreCase),
            "FAM-22-AC6：空状态缺少引导文案 key（Leaderboard_InviteHint）（红）");

        var zhResx = RepoPath.FindUp(Path.Combine(
            "services", "Baihua.Web", "Localization", "SharedResources.resx"));
        var zhSource = File.ReadAllText(zhResx);
        Assert.True(
            zhSource.Contains("邀请更多小伙伴一起学吧", StringComparison.OrdinalIgnoreCase),
            "FAM-22-AC6：resx 缺少'邀请更多小伙伴一起学吧'文案（红）");
    }

    private static bool RegexActiveFirst(string source, string keyword)
    {
        // 粗略检测：关键字出现在 "active" 修饰附近的初始 Tab 定义（如 tab active 或 active == "self"）
        var idx = source.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        var start = Math.Max(0, idx - 120);
        var window = source.Substring(start, Math.Min(160, source.Length - start));
        return window.Contains("active", StringComparison.OrdinalIgnoreCase);
    }
}
