using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Modules.Family.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// FAM-22 时区边界红测试：排行榜"和自己比"按北京时间（Asia/Shanghai）周界计算。
///
/// 验收标准覆盖（本轮：时区边界 + 核心语义）：
///   - AC2  和自己比数据正确：本周 10 / 上周 7 → 变化 ↑3（42.9%）；周界由 FAM-01 保证
///   - AC5  全家排行默认关闭（新用户未配置过设置 → 不显示全家 Tab）
///
/// 红测试方式（FAM-20/21 同套路）：
///   固定"现在"= 北京时间 2026-08-07（周五）。本周 = 北京 08-03（周一）00:00 起，
///   上周 = 北京 07-27（周一）00:00 起。
///   UTC 08-02T20:00 = 北京 08-03 04:00（本周一清晨）——用 UTC 周界会把它算进上周（红）。
///   当前无"和自己比"方法 → 探测返回契约缺失 → 红；dev 实现后验证北京时间口径。
/// </summary>
public class Fam22TimezoneBoundaryTests : IDisposable
{
    private readonly SqliteConnection _familyConn;
    private readonly SqliteConnection _vaultConn;
    private readonly IDbContextFactory<FamilyDbContext> _familyFactory;
    private readonly IDbContextFactory<VaultDbContext> _vaultFactory;

    private const string VaultId = "vault-fam22";

    /// <summary>固定"现在"：北京时间 2026-08-07（周五）07:30</summary>
    private readonly FakeTimeProvider _clock = FakeTimeProvider.Beijing20260807_0730();

    public Fam22TimezoneBoundaryTests()
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

    /// <summary>批量插入 count 条学习记录（同一时刻）</summary>
    private void AddActivities(int learnerId, DateTime utc, int count)
    {
        for (int i = 0; i < count; i++)
            AddActivity(learnerId, utc.AddTicks(i), "remember");
    }

    // ============ 快照获取（契约缺失即红） ============

    private Fam22LeaderboardProbe.CompareSnapshot GetCompareSnapshot(int learnerId)
    {
        var svc = new LeaderboardService(_familyFactory, _clock);
        return Fam22LeaderboardProbe.GetCompareSnapshot(svc, VaultId, learnerId);
    }

    // ============ AC2：本周 vs 上周（北京时间周界） ============

    [Fact]
    public void WeeklyCompare_BeijingWeekBoundary_NotUtcWeek()
    {
        // 固定"现在"= 北京 08-07（周五）。本周 = 北京 08-03(周一)~08-09，上周 = 北京 07-27(周一)~08-02。
        // 本周 10 张：UTC 08-02T20:00 = 北京 08-03 04:00（本周一清晨，UTC 却属 08-02 上周！）+ 其余 9 条本周内
        // 上周 7 张：UTC 07-30T02:00 = 北京 07-30 10:00（上周三）
        // 北京口径：本周 10、上周 7 → Delta=3、Percent≈42.9、Arrow=up
        // UTC 周界口径：08-02T20:00 落上周 → 本周 9、上周 8 → 红
        var a = AddLearner("A");
        AddActivities(a, new DateTime(2026, 8, 2, 20, 0, 0, DateTimeKind.Utc), 1); // 北京 08-03 04:00（本周）
        AddActivities(a, new DateTime(2026, 8, 6, 2, 0, 0, DateTimeKind.Utc), 9);  // 北京 08-06 10:00（本周）
        AddActivities(a, new DateTime(2026, 7, 30, 2, 0, 0, DateTimeKind.Utc), 7);  // 北京 07-30 10:00（上周）

        var snap = GetCompareSnapshot(a);

        Assert.True(snap.ContractPresent, $"FAM-22-AC2 契约缺失: {snap.MissingDetail}");
        Assert.Equal(10, snap.WeekTotal);
        Assert.Equal(7, snap.LastWeekTotal);
        Assert.Equal(3, snap.Delta);
        Assert.Equal(42.9, Math.Round(snap.Percent, 1));
        Assert.Equal("up", snap.Arrow);
    }

    [Fact]
    public void WeeklyCompare_Down_WhenThisWeekLess()
    {
        // 本周 3 < 上周 5 → Delta=-2、Arrow=down
        var a = AddLearner("A");
        AddActivities(a, new DateTime(2026, 8, 6, 2, 0, 0, DateTimeKind.Utc), 3); // 本周
        AddActivities(a, new DateTime(2026, 7, 30, 2, 0, 0, DateTimeKind.Utc), 5); // 上周

        var snap = GetCompareSnapshot(a);

        Assert.True(snap.ContractPresent, $"FAM-22-AC2 契约缺失: {snap.MissingDetail}");
        Assert.Equal(3, snap.WeekTotal);
        Assert.Equal(5, snap.LastWeekTotal);
        Assert.Equal(-2, snap.Delta);
        Assert.Equal("down", snap.Arrow);
    }

    [Fact]
    public void WeeklyCompare_Flat_WhenEqual()
    {
        // 本周 == 上周 → Delta=0、Arrow=flat
        var a = AddLearner("A");
        AddActivities(a, new DateTime(2026, 8, 6, 2, 0, 0, DateTimeKind.Utc), 4); // 本周
        AddActivities(a, new DateTime(2026, 7, 30, 2, 0, 0, DateTimeKind.Utc), 4); // 上周

        var snap = GetCompareSnapshot(a);

        Assert.True(snap.ContractPresent, $"FAM-22-AC2 契约缺失: {snap.MissingDetail}");
        Assert.Equal(4, snap.WeekTotal);
        Assert.Equal(4, snap.LastWeekTotal);
        Assert.Equal(0, snap.Delta);
        Assert.Equal("flat", snap.Arrow);
    }

    [Fact]
    public void WeeklyCompare_NoLastWeekData_ShowsDash()
    {
        // 上周无数据 → 无法算变化 → Arrow=""（页面显示"--"），本周数仍正确
        var a = AddLearner("A");
        AddActivities(a, new DateTime(2026, 8, 6, 2, 0, 0, DateTimeKind.Utc), 2); // 本周

        var snap = GetCompareSnapshot(a);

        Assert.True(snap.ContractPresent, $"FAM-22-AC2 契约缺失: {snap.MissingDetail}");
        Assert.Equal(2, snap.WeekTotal);
        Assert.Equal(0, snap.LastWeekTotal);
        Assert.Equal("", snap.Arrow);
    }

    // ============ AC5：全家排行默认关闭 ============

    [Fact]
    public void AllFamilyTab_DefaultOff_ForNewUser()
    {
        // 新用户/未配置过设置 → 全家排行默认关闭（false）
        // 契约缺失（无设置服务）→ null → 红
        var setting = Fam22LeaderboardProbe.GetAllFamilyTabDefault(_familyFactory, _vaultFactory, _clock);
        Assert.True(setting.HasValue,
            "FAM-22-AC5 契约缺失：全家排行设置服务不可用（期望可读取默认值）（红）");
        Assert.False(setting!.Value,
            "FAM-22-AC5：新用户全家排行必须默认关闭（false）（红）");
    }
}
