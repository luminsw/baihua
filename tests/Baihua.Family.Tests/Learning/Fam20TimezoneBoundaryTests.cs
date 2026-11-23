using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Modules.Family.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// FAM-20 时区边界红测试：家长看板"今日/昨日/连续打卡"按北京时间（Asia/Shanghai）自然日计算。
///
/// 验收标准覆盖（本轮：时区边界 + 核心语义）：
///   - AC1  今日三件事：数量 + 趋势箭头（今天>昨天→up；无数据→"--"）
///   - AC3  连续打卡=家庭维度（任意成员有学习行为即算当天），按北京自然日，跨天不中断
///   - AC4  成长时间线：最近 30 天 + 时间倒序
///
/// 红测试方式（与 FAM-01 一致：固定时钟 + 北京时间语义）：
///   固定"现在"= 北京时间 2026-08-07（周五），此时北京日期与 UTC 日期不同
///   （北京 08-07 07:30 = UTC 08-06T23:30），天然暴露"用 UTC 算今天"的时区 bug。
///   当前看板契约（learnerId 筛选/新字段）尚未实现 → 探测返回契约缺失 → 红；
///   dev 实现后这些用例验证北京时间口径，UTC 实现会继续红。
/// </summary>
public class Fam20TimezoneBoundaryTests : IDisposable
{
    private readonly SqliteConnection _familyConn;
    private readonly SqliteConnection _vaultConn;
    private readonly IDbContextFactory<FamilyDbContext> _familyFactory;
    private readonly IDbContextFactory<VaultDbContext> _vaultFactory;

    private const string VaultId = "vault-fam20";

    /// <summary>固定"现在"：北京时间 2026-08-07（周五）07:30（= UTC 08-06T23:30）</summary>
    private readonly FakeTimeProvider _clock = FakeTimeProvider.Beijing20260807_0730();

    public Fam20TimezoneBoundaryTests()
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

    // ============ 数据准备 ============

    private int AddLearner(string name)
    {
        using var db = _familyFactory.CreateDbContext();
        var learner = new LearnerProfile
        {
            Name = name,
            AvatarEmoji = "🙂",
            Color = "#007bff",
            IsDefault = false
        };
        db.LearnerProfiles.Add(learner);
        db.SaveChanges();
        return learner.Id;
    }

    private void AddActivity(int learnerId, DateTime utc, string result = "remember")
    {
        using var db = _familyFactory.CreateDbContext();
        db.StudyActivities.Add(new StudyActivity
        {
            LearnerId = learnerId,
            VaultId = VaultId,
            ActivityType = "study",
            CardId = $"card-{utc.Ticks}",
            Result = result,
            CreatedAt = utc
        });
        db.SaveChanges();
    }

    private void AddAchievement(int learnerId, string key, string title, string icon, DateTime unlockedAtUtc)
    {
        using var db = _familyFactory.CreateDbContext();
        db.Achievements.Add(new Achievement
        {
            LearnerId = learnerId,
            Key = key,
            Title = title,
            Icon = icon,
            Tier = "bronze",
            Category = "study",
            UnlockedAt = unlockedAtUtc
        });
        db.SaveChanges();
    }

    // ============ 快照获取（契约缺失即红） ============

    private Fam20DashboardProbe.DashboardSnapshot GetSnapshot(int? learnerId = null)
    {
        var svc = Fam20DashboardProbe.CreateService(_familyFactory, _vaultFactory, _clock, out var error);
        Assert.NotNull(svc);
        Assert.Null(error);
        return Fam20DashboardProbe.GetSnapshot(svc!, VaultId, learnerId);
    }

    // ============ AC3：家庭维度连续打卡（北京时间） ============

    [Fact]
    public void FamilyStreak_AnyMemberActivity_CountsToday()
    {
        // 家庭维度：A 今天学了、B 没学 → 家庭打卡 = 1（任意成员有学习行为即算当天）
        // 注意 UTC 08-06T20:00 = 北京 08-07 04:00（今天）——用 UTC 日期会算成昨天（红）
        var a = AddLearner("A");
        var b = AddLearner("B");
        AddActivity(a, new DateTime(2026, 8, 6, 20, 0, 0, DateTimeKind.Utc));

        var snap = GetSnapshot();

        Assert.True(snap.ContractPresent, $"FAM-20-AC3 契约缺失: {snap.MissingDetail}");
        Assert.Equal(1, snap.FamilyStreak);
    }

    [Fact]
    public void FamilyStreak_BeijingConsecutiveDays_NotUtcDates()
    {
        // 家庭维度 + 北京连续：A 今天学（UTC 08-06T20:00 = 北京 08-07）、B 昨天学（UTC 08-06T02:00 = 北京 08-06）
        // 北京口径：昨天+今天连续 → FamilyStreak == 2
        // 两种错误实现都会给 1：
        //   a) 用 UTC 日期：两条同属 UTC 08-06 → 只算 1 天
        //   b) 取各 Learner streak 最大值：A=1、B=1 → 1（而不是家庭并集 2）
        var a = AddLearner("A");
        var b = AddLearner("B");
        AddActivity(a, new DateTime(2026, 8, 6, 20, 0, 0, DateTimeKind.Utc));
        AddActivity(b, new DateTime(2026, 8, 6, 2, 0, 0, DateTimeKind.Utc));

        var snap = GetSnapshot();

        Assert.True(snap.ContractPresent, $"FAM-20-AC3 契约缺失: {snap.MissingDetail}");
        Assert.Equal(2, snap.FamilyStreak);
    }

    [Fact]
    public void FamilyStreak_MidnightBoundary_Beijing0030()
    {
        // 时区边界：固定"现在"= 北京 08-07 00:30（= UTC 08-06T16:30）。
        // UTC 08-06T16:15 = 北京 08-07 00:15（今天）；UTC 08-06T15:45 = 北京 08-06 23:45（昨天）
        // 北京口径：昨天+今天连续 → FamilyStreak == 2
        // UTC 口径：两条同属 UTC 08-06 → 1（红）
        var clock0030 = new FakeTimeProvider(new DateTime(2026, 8, 7, 0, 30, 0));
        var a = AddLearner("A");
        var b = AddLearner("B");
        AddActivity(a, new DateTime(2026, 8, 6, 16, 15, 0, DateTimeKind.Utc));
        AddActivity(b, new DateTime(2026, 8, 6, 15, 45, 0, DateTimeKind.Utc));

        var svc = Fam20DashboardProbe.CreateService(_familyFactory, _vaultFactory, clock0030, out var error);
        Assert.NotNull(svc);
        Assert.Null(error);
        var snap = Fam20DashboardProbe.GetSnapshot(svc!, VaultId, learnerId: null);

        Assert.True(snap.ContractPresent, $"FAM-20-AC3 契约缺失: {snap.MissingDetail}");
        Assert.Equal(2, snap.FamilyStreak);
    }

    // ============ AC1：今日三件事（数量 + 趋势箭头） ============

    [Fact]
    public void TodayVsYesterday_TrendUp_AcrossUtcDateBoundary()
    {
        // 昨日（北京 08-06）：UTC 08-06T02:00 → 1 条
        // 今日（北京 08-07）：UTC 08-06T20:00 + 08-06T22:00 → 2 条（UTC 日期同属 08-06！）
        // 北京口径：TodayCompleted=2, YesterdayCompleted=1, 趋势=up
        // UTC 口径：今日=08-06 → 3 条、昨日=08-05 → 0 条 → up 但数量错（红）
        var a = AddLearner("A");
        AddActivity(a, new DateTime(2026, 8, 6, 2, 0, 0, DateTimeKind.Utc));
        AddActivity(a, new DateTime(2026, 8, 6, 20, 0, 0, DateTimeKind.Utc));
        AddActivity(a, new DateTime(2026, 8, 6, 22, 0, 0, DateTimeKind.Utc));

        var snap = GetSnapshot();

        Assert.True(snap.ContractPresent, $"FAM-20-AC1 契约缺失: {snap.MissingDetail}");
        Assert.Equal(2, snap.TodayCompleted);
        Assert.Equal(1, snap.YesterdayCompleted);
        Assert.Equal(Fam20DashboardProbe.TrendUp, snap.TrendArrow);
    }

    [Fact]
    public void Trend_NoData_ReturnsDash()
    {
        // 无任何学习记录 → 无数据显示 "--"（TrendArrow 为空串，页面渲染 "--"）
        var snap = GetSnapshot();

        Assert.True(snap.ContractPresent, $"FAM-20-AC1 契约缺失: {snap.MissingDetail}");
        Assert.Equal(0, snap.TodayCompleted);
        Assert.Equal(0, snap.YesterdayCompleted);
        Assert.Equal(Fam20DashboardProbe.TrendNone, snap.TrendArrow);
    }

    // ============ AC1：最新成就（最多 3 个、按解锁时间倒序） ============

    [Fact]
    public void LatestAchievements_MaxThree_NewestFirst()
    {
        // 4 个成就，解锁时间从新到旧：
        //   北京 08-07 06:00 = UTC 08-06T22:00（最新，今天）
        //   北京 08-06 20:00 = UTC 08-06T12:00（昨天）
        //   北京 08-05 10:00 = UTC 08-05T02:00
        //   北京 08-04 10:00 = UTC 08-04T02:00（最旧）
        // 期望：只返回最近 3 个，且第一个是最新的
        var a = AddLearner("A");
        AddAchievement(a, "streak_3", "三日不断", "🔥", new DateTime(2026, 8, 6, 22, 0, 0, DateTimeKind.Utc));
        AddAchievement(a, "cards_10", "十题小试", "🎯", new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc));
        AddAchievement(a, "first", "第一步", "🌟", new DateTime(2026, 8, 5, 2, 0, 0, DateTimeKind.Utc));
        AddAchievement(a, "creator_1", "初出茅庐", "✍️", new DateTime(2026, 8, 4, 2, 0, 0, DateTimeKind.Utc));

        var snap = GetSnapshot();

        Assert.True(snap.ContractPresent, $"FAM-20-AC1 契约缺失: {snap.MissingDetail}");
        Assert.True(snap.LatestAchievements.Count <= 3,
            $"FAM-20-AC1：最新成就最多 3 个（当前 {snap.LatestAchievements.Count}）（红）");
        Assert.Equal("三日不断", snap.LatestAchievements[0].Title);
        // 按解锁时间倒序（最新在前）
        var times = snap.LatestAchievements.Select(x => x.UnlockedAt!.Value).ToList();
        Assert.Equal(times.OrderByDescending(t => t), times);
    }

    // ============ AC4：成长时间线（最近 30 天 + 倒序） ============

    [Fact]
    public void GrowthTimeline_OnlyLast30Days_Descending()
    {
        // 固定"现在"= 北京 08-07。30 天窗口 = 北京 08-07 往前 29 天 = 07-08 00:00 起。
        // 北京 07-01（37 天前）→ 窗口外，不得出现
        // 北京 07-20（18 天前）→ 窗口内
        // 北京 08-07 06:00（今天）→ 窗口内、最新
        var a = AddLearner("A");
        AddActivity(a, new DateTime(2026, 7, 1, 2, 0, 0, DateTimeKind.Utc));  // 北京 07-01 10:00（窗口外）
        AddActivity(a, new DateTime(2026, 7, 20, 2, 0, 0, DateTimeKind.Utc)); // 北京 07-20 10:00（窗口内）
        AddActivity(a, new DateTime(2026, 8, 6, 22, 0, 0, DateTimeKind.Utc)); // 北京 08-07 06:00（今天）

        var snap = GetSnapshot();

        Assert.True(snap.ContractPresent, $"FAM-20-AC4 契约缺失: {snap.MissingDetail}");

        var beijingDates = snap.GrowthTimeline
            .Select(e => ToBeijingDate(e.Date!.Value))
            .ToList();

        // 窗口外（37 天前）的事件不得出现
        Assert.DoesNotContain(beijingDates, d => d < new DateTime(2026, 7, 8));
        // 按时间倒序（最新在前）
        for (int i = 0; i < beijingDates.Count - 1; i++)
            Assert.True(beijingDates[i] >= beijingDates[i + 1],
                "FAM-20-AC4：时间线必须按时间倒序排列（红）");
    }

    private static readonly TimeZoneInfo BeijingTz = ResolveBeijingTz();

    private static TimeZoneInfo ResolveBeijingTz()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
    }

    private static DateTime ToBeijingDate(DateTime d)
    {
        if (d.Kind == DateTimeKind.Utc)
            return TimeZoneInfo.ConvertTimeFromUtc(d, BeijingTz).Date;
        return d.Date; // Unspecified/Local 视为已是北京日期
    }
}
