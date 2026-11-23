using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Modules.Family.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// FAM-21 时区边界红测试：学习打卡按北京时间（Asia/Shanghai）自然日自动判定。
///
/// 验收标准覆盖（本轮：时区边界 + 核心语义）：
///   - AC5  跨天边界：北京 23:30 算当天打卡，00:15 算次日打卡；北京 00:00 为日期边界
///   - AC4  打卡日历：最近 7 天格子（实心/空心/今天高亮）+ 连续天数从今天往前连续数
///   - 打卡行为：当天有 StudyActivity 即自动已打卡（无需手动按钮）
///
/// 红测试方式（FAM-20 同套路）：
///   固定"现在"= 北京时间 2026-08-08 00:30（= UTC 08-07T16:30），
///   北京 08-07 23:30 与 08-08 00:15 两条记录 UTC 同属 08-07 —— 用 UTC 日期会算成同一天（红）。
///   当前无 CheckinService → 探测返回契约缺失 → 红；dev 实现后这些用例验证北京时间口径。
/// </summary>
public class Fam21TimezoneBoundaryTests : IDisposable
{
    private readonly SqliteConnection _familyConn;
    private readonly SqliteConnection _vaultConn;
    private readonly IDbContextFactory<FamilyDbContext> _familyFactory;
    private readonly IDbContextFactory<VaultDbContext> _vaultFactory;

    private const string VaultId = "vault-fam21";

    /// <summary>固定"现在"：北京时间 2026-08-08 00:30（= UTC 08-07T16:30）——北京日期与 UTC 日期不同</summary>
    private readonly FakeTimeProvider _clock = new(new DateTime(2026, 8, 8, 0, 30, 0));

    public Fam21TimezoneBoundaryTests()
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

    // ============ 快照获取（契约缺失即红） ============

    private Fam21CheckinProbe.CheckinSnapshot GetSnapshot()
    {
        var svc = Fam21CheckinProbe.CreateService(_familyFactory, _vaultFactory, _clock, out var error);
        Assert.NotNull(svc);
        Assert.Null(error);
        return Fam21CheckinProbe.GetSnapshot(svc!, VaultId);
    }

    // ============ AC5：北京时间 00:00 为日期边界 ============

    [Fact]
    public void Checkin_SplitsByBeijingMidnight_NotUtcDate()
    {
        // 固定"现在"= 北京 08-08 00:30（= UTC 08-07T16:30）。
        // 记录1：UTC 08-07T15:30 = 北京 08-07 23:30（昨天）
        // 记录2：UTC 08-07T16:15 = 北京 08-08 00:15（今天）
        // 北京口径：今日清单只含记录2；UTC 口径：两条同属 UTC 08-07 → 都会出现在"今日"（红）
        var a = AddLearner("A");
        AddActivity(a, new DateTime(2026, 8, 7, 15, 30, 0, DateTimeKind.Utc));
        AddActivity(a, new DateTime(2026, 8, 7, 16, 15, 0, DateTimeKind.Utc));

        var snap = GetSnapshot();

        Assert.True(snap.ContractPresent, $"FAM-21-AC5 契约缺失: {snap.MissingDetail}");
        var times = snap.TodayRecords.Select(r => r.Time!.Value).ToList();
        Assert.DoesNotContain(times, t => IsBeijingDate(t, 2026, 8, 7));
        Assert.Contains(times, t => IsBeijingDate(t, 2026, 8, 8));
    }

    [Fact]
    public void Checkin_Streak_AcrossBeijingMidnight()
    {
        // 北京 08-07 23:30（昨天）+ 08-08 00:15（今天）→ 连续两天 → FamilyStreak == 2
        // UTC 口径：两条同属 UTC 08-07 → 只算 1 天（红）
        var a = AddLearner("A");
        AddActivity(a, new DateTime(2026, 8, 7, 15, 30, 0, DateTimeKind.Utc));
        AddActivity(a, new DateTime(2026, 8, 7, 16, 15, 0, DateTimeKind.Utc));

        var snap = GetSnapshot();

        Assert.True(snap.ContractPresent, $"FAM-21-AC5 契约缺失: {snap.MissingDetail}");
        Assert.Equal(2, snap.FamilyStreak);
    }

    // ============ 打卡行为：自动判定 ============

    [Fact]
    public void Checkin_TodayActivity_AutoCheckedIn()
    {
        // 今天（北京 08-08）有学习记录 → 自动已打卡：
        //   今日清单非空，且条目 IsCompleted == true（无需手动"打卡"按钮）
        var a = AddLearner("A");
        AddActivity(a, new DateTime(2026, 8, 7, 16, 15, 0, DateTimeKind.Utc)); // 北京 08-08 00:15

        var snap = GetSnapshot();

        Assert.True(snap.ContractPresent, $"FAM-21 契约缺失: {snap.MissingDetail}");
        Assert.NotEmpty(snap.TodayRecords);
        Assert.All(snap.TodayRecords, r => Assert.True(r.IsCompleted,
            "FAM-21：当天有学习记录必须自动标记为已打卡 ✅（红）"));
        Assert.True(snap.FamilyStreak >= 1, "FAM-21：今天有学习记录则连续打卡 ≥ 1（红）");
    }

    [Fact]
    public void Checkin_NoRecord_EmptyListAndZeroStreak()
    {
        // 无任何记录 → 今日清单为空、连续打卡 0（页面显示引导 CTA）
        var snap = GetSnapshot();

        Assert.True(snap.ContractPresent, $"FAM-21 契约缺失: {snap.MissingDetail}");
        Assert.Empty(snap.TodayRecords);
        Assert.Equal(0, snap.FamilyStreak);
    }

    // ============ AC4：7 天日历 + 连续天数 ============

    [Fact]
    public void Checkin_Calendar_7DaysWithCheckedMarkers()
    {
        // 固定"现在"= 北京 08-08 00:30。最近 7 天 = 08-02 ~ 08-08。
        // 有记录的天：08-08（今天）、08-07、08-06、08-05、08-02 → 5 天实心，08-04/08-03 空心
        var a = AddLearner("A");
        AddActivity(a, new DateTime(2026, 8, 7, 16, 15, 0, DateTimeKind.Utc)); // 北京 08-08 00:15
        AddActivity(a, new DateTime(2026, 8, 6, 16, 0, 0, DateTimeKind.Utc));  // 北京 08-07 00:00
        AddActivity(a, new DateTime(2026, 8, 5, 16, 0, 0, DateTimeKind.Utc));  // 北京 08-06 00:00
        AddActivity(a, new DateTime(2026, 8, 4, 16, 0, 0, DateTimeKind.Utc));  // 北京 08-05 00:00
        AddActivity(a, new DateTime(2026, 8, 1, 16, 0, 0, DateTimeKind.Utc));  // 北京 08-02 00:00

        var snap = GetSnapshot();

        Assert.True(snap.ContractPresent, $"FAM-21-AC4 契约缺失: {snap.MissingDetail}");
        Assert.Equal(7, snap.Last7Days.Count);

        var checkedDates = snap.Last7Days.Where(d => d.IsChecked).Select(d => ToBeijingDate(d.Date!.Value)).ToList();
        Assert.Equal(5, checkedDates.Count);
        Assert.Contains(new DateTime(2026, 8, 8), checkedDates); // 今天已打卡
        Assert.Contains(new DateTime(2026, 8, 2), checkedDates);
        Assert.DoesNotContain(new DateTime(2026, 8, 3), checkedDates); // 空心
        Assert.DoesNotContain(new DateTime(2026, 8, 4), checkedDates); // 空心

        // 今天高亮：恰好 1 格 IsToday，且是 08-08
        var today = snap.Last7Days.Single(d => d.IsToday);
        Assert.Equal(new DateTime(2026, 8, 8), ToBeijingDate(today.Date!.Value));
    }

    [Fact]
    public void Checkin_Streak_BreaksOnGapDay()
    {
        // 今天 08-08 有记录、08-07 无记录 → 连续中断 → FamilyStreak == 1（只算今天）
        // （若实现把"有记录的天数"当连续天数 → 会算 3 之类的错误值，红）
        var a = AddLearner("A");
        AddActivity(a, new DateTime(2026, 8, 7, 16, 15, 0, DateTimeKind.Utc)); // 北京 08-08 00:15（今天）
        AddActivity(a, new DateTime(2026, 8, 5, 16, 0, 0, DateTimeKind.Utc));  // 北京 08-06 00:00（前天）

        var snap = GetSnapshot();

        Assert.True(snap.ContractPresent, $"FAM-21-AC4 契约缺失: {snap.MissingDetail}");
        Assert.Equal(1, snap.FamilyStreak);
    }

    // ============ 工具 ============

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

    private static bool IsBeijingDate(DateTime d, int year, int month, int day)
        => ToBeijingDate(d) == new DateTime(year, month, day);
}
