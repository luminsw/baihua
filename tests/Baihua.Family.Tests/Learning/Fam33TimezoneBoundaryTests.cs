using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Modules.Family.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// FAM-33 时区/行为边界测试：补签 + 连击保护（北京时间自然日）。
///
/// 验收标准（pm 拍板语义 2026-08-07）：
///   - AC1  补签成功：最近 3 天内无 StudyActivity 的 ⬜ 日期可补签，断点接上
///   - AC2  月 3 次限制（家庭维度）：超出提示"本月补签次数已用完"
///   - AC3  该日有学习记录 → 已是 🔥 → 补签被拒；无记录的 ⬜ → 可补签
///   - AC4  连击保护：今天没学不归零（显示昨天截止值+"今天还没学"）；中断 2 天归零
///
/// 固定"现在"= 北京时间 2026-08-07（周五）07:30。
/// </summary>
public class Fam33TimezoneBoundaryTests : IDisposable
{
    private readonly SqliteConnection _familyConn;
    private readonly SqliteConnection _vaultConn;
    private readonly IDbContextFactory<FamilyDbContext> _familyFactory;
    private readonly IDbContextFactory<VaultDbContext> _vaultFactory;

    private const string VaultId = "vault-fam33";

    private readonly FakeTimeProvider _clock = FakeTimeProvider.Beijing20260807_0730();

    public Fam33TimezoneBoundaryTests()
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

    /// <summary>在指定北京日期（当日 10:00）插入学习记录</summary>
    private void AddActivityOn(int learnerId, DateTime beijingDate)
    {
        var utc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(beijingDate.AddHours(10), DateTimeKind.Unspecified), BeijingTz);
        using var db = _familyFactory.CreateDbContext();
        db.StudyActivities.Add(new StudyActivity
        {
            LearnerId = learnerId,
            VaultId = VaultId,
            ActivityType = "study",
            CardId = $"card-{utc.Ticks}",
            Result = "remember",
            CreatedAt = utc
        });
        db.SaveChanges();
    }

    private object CreateService()
    {
        var svc = Fam33MakeupProbe.CreateService(_familyFactory, _vaultFactory, _clock, out var error);
        Assert.NotNull(svc);
        Assert.Null(error);
        return svc!;
    }

    // ============ AC1：补签成功（无记录 ⬜ 日期，3 天窗口） ============

    [Fact]
    public void Makeup_Within3DayWindow_SucceedsAndReconnectsStreak()
    {
        // 北京 08-05（前天）无学习记录（⬜ 可补签）；08-06 有记录（🔥）；08-07（今天）有记录。
        // 补签 08-05 → 成功 → 格子变 🔥，streak 重算（08-05~08-07 连续 → 3）
        var a = AddLearner("A");
        AddActivityOn(a, new DateTime(2026, 8, 6)); // 昨天
        AddActivityOn(a, new DateTime(2026, 8, 7)); // 今天
        // 08-05 无记录

        var svc = CreateService();
        var makeup = Fam33MakeupProbe.InvokeMakeup(svc, VaultId, new DateTime(2026, 8, 5));

        Assert.True(makeup.ContractPresent, $"FAM-33-AC1 契约缺失: {makeup.MissingDetail}");
        Assert.True(makeup.Success, $"FAM-33-AC1：3 天窗口内无记录日期补签必须成功（消息: {makeup.Message}）（红）");

        var snap = Fam33MakeupProbe.GetSnapshot(svc, VaultId);
        Assert.True(snap.ContractPresent, $"FAM-33-AC1 契约缺失: {snap.MissingDetail}");
        // 08-05 格子已打卡（🔥）
        var dayCell = snap.Last7Days.FirstOrDefault(c =>
            c.Date.HasValue && c.Date.Value.Date == new DateTime(2026, 8, 5));
        Assert.True(dayCell != null && dayCell.IsChecked,
            "FAM-33-AC1：补签后该日格子必须变为已打卡（🔥）（红）");
        // 断点接上：08-05/08-06/08-07 连续 → 3
        Assert.Equal(3, snap.FamilyStreak);
    }

    // ============ AC2：月补签次数限制（家庭维度） ============

    [Fact]
    public void Makeup_MonthlyLimit_3Times_ThenRejected()
    {
        // 本月已用 3 次 → 第 4 次补签被拒，提示"本月补签次数已用完"
        // 月限检查优先于窗口（dev 实现语义：保证"已用完"提示优先）
        var a = AddLearner("A");
        // 08-04/08-05/08-06 均无记录（⬜ 可补签）

        var svc = CreateService();
        var snap0 = Fam33MakeupProbe.GetSnapshot(svc, VaultId);
        Assert.True(snap0.ContractPresent, $"FAM-33-AC2 契约缺失: {snap0.MissingDetail}");
        Assert.Equal(3, snap0.MakeupRemaining); // 默认月 3 次

        // 用掉 3 次（补签 3 个无记录日期）
        for (int i = 4; i <= 6; i++)
        {
            var r = Fam33MakeupProbe.InvokeMakeup(svc, VaultId, new DateTime(2026, 8, i));
            Assert.True(r.ContractPresent, $"FAM-33-AC2 契约缺失: {r.MissingDetail}");
            Assert.True(r.Success, $"FAM-33-AC2：第 {i - 3} 次补签应成功（红）");
        }

        // 第 4 次被拒（任何日期——月限检查优先，报"已用完"）
        var rejected = Fam33MakeupProbe.InvokeMakeup(svc, VaultId, new DateTime(2026, 8, 3));
        Assert.True(rejected.ContractPresent, $"FAM-33-AC2 契约缺失: {rejected.MissingDetail}");
        Assert.False(rejected.Success, "FAM-33-AC2：第 4 次补签必须被拒（月限 3 次）（红）");
        Assert.True(
            rejected.Message.Contains("已用完") || rejected.Message.Contains("用完") || rejected.Message.Contains("limit", StringComparison.OrdinalIgnoreCase),
            $"FAM-33-AC2：拒绝消息必须提示'本月补签次数已用完'（实际: {rejected.Message}）（红）");
    }

    // ============ AC3：有学习记录（已🔥）不可补签 ============

    [Fact]
    public void Makeup_HasStudyActivity_Rejected()
    {
        // 该日有学习记录 → 已是 🔥 → 补签被拒（无需补签，不显示入口）
        var a = AddLearner("A");
        AddActivityOn(a, new DateTime(2026, 8, 6)); // 08-06 有记录

        var svc = CreateService();
        var result = Fam33MakeupProbe.InvokeMakeup(svc, VaultId, new DateTime(2026, 8, 6));

        Assert.True(result.ContractPresent, $"FAM-33-AC3 契约缺失: {result.MissingDetail}");
        Assert.False(result.Success,
            "FAM-33-AC3：已有学习记录的日期（🔥）不得补签（红）");
        Assert.True(
            result.Message.Contains("已有学习记录") || result.Message.Contains("无需补签") || result.Message.Contains("已打卡"),
            $"FAM-33-AC3：拒绝消息必须说明'该日已有学习记录，无需补签'（实际: {result.Message}）（红）");
    }

    // ============ AC4：连击保护 ============

    [Fact]
    public void Streak_TodayNotStudied_ShowsGrace_NotZero()
    {
        // 连续 7 天（07-31~08-06）有记录，今天（08-07）还没学 → 保护：
        // streak 保持 7（昨天截止值），状态=今天还没学，不归零
        var a = AddLearner("A");
        for (int i = 7; i >= 1; i--)
            AddActivityOn(a, new DateTime(2026, 8, 7).AddDays(-i)); // 07-31..08-06

        var svc = CreateService();
        var snap = Fam33MakeupProbe.GetSnapshot(svc, VaultId);

        Assert.True(snap.ContractPresent, $"FAM-33-AC4 契约缺失: {snap.MissingDetail}");
        Assert.True(snap.FamilyStreak >= 7,
            $"FAM-33-AC4：今天没学不得归零，连续打卡应保持 7（当前 {snap.FamilyStreak}）（红）");
        Assert.True(
            snap.StreakStatus.Contains("今天还没学") || snap.StreakStatus.Contains("未学") ||
            snap.StreakStatus.Contains("grace", StringComparison.OrdinalIgnoreCase) ||
            snap.StreakStatus.Contains("保护", StringComparison.OrdinalIgnoreCase),
            $"FAM-33-AC4：缺少'今天还没学'保护状态（实际: '{snap.StreakStatus}'）（红）");
    }

    [Fact]
    public void Streak_2DaysGap_ResetsToZero()
    {
        // 中断超过保护期（今天+昨天都无记录）→ 连击归零
        var a = AddLearner("A");
        for (int i = 8; i >= 2; i--)
            AddActivityOn(a, new DateTime(2026, 8, 7).AddDays(-i)); // 07-30..08-05
        // 今天(08-07) 和昨天(08-06) 均无记录

        var svc = CreateService();
        var snap = Fam33MakeupProbe.GetSnapshot(svc, VaultId);

        Assert.True(snap.ContractPresent, $"FAM-33-AC4 契约缺失: {snap.MissingDetail}");
        Assert.Equal(0, snap.FamilyStreak);
    }

    private static readonly TimeZoneInfo BeijingTz = ResolveBeijingTz();

    private static TimeZoneInfo ResolveBeijingTz()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
    }
}
