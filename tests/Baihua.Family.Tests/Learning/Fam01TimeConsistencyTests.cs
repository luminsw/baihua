using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Modules.Family.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// FAM-01 红测试：统一时区（北京时间）。
///
/// 验收标准（固定时钟 2026-08-07T07:30+08:00，周五）：
///   - GetTodayProgress 返回"北京时间今天"（08-07）的进度，不用 UTC 日期（08-06）算
///   - 周榜在周日 23:59:59 前不翻篇（北京时间周一 00:00 起算本周）
///   - streak 在北京时间 00:00 切换日期
///
/// 红测试方式：
///   1) 契约红：DailyCardService/LeaderboardService 必须存在可注入时间源（当前无 → 红）
///   2) 行为红：注入固定时钟后按北京时间语义断言（当前实现用 UTC → 红）
/// </summary>
public class Fam01TimeConsistencyTests : IDisposable
{
    private readonly SqliteConnection _familyConn;
    private readonly SqliteConnection _vaultConn;
    private readonly IDbContextFactory<FamilyDbContext> _familyFactory;
    private readonly IDbContextFactory<VaultDbContext> _vaultFactory;
    private readonly FakeTimeProvider _clock = FakeTimeProvider.Beijing20260807_0730();

    private const string VaultId = "vault-fam01";

    public Fam01TimeConsistencyTests()
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

    // ============ 契约红：必须存在可注入时间源 ============

    [Fact]
    public void DailyCardService_MustAcceptInjectableTimeSource()
    {
        var (injectable, _) = TimeSourceProbe.Probe(typeof(DailyCardService));
        Assert.True(injectable,
            "FAM-01 契约：DailyCardService 构造函数必须接受可注入时间源（ITimeProvider/Clock 参数）——当前缺失（红）");
    }

    [Fact]
    public void LeaderboardService_MustAcceptInjectableTimeSource()
    {
        var (injectable, _) = TimeSourceProbe.Probe(typeof(LeaderboardService));
        Assert.True(injectable,
            "FAM-01 契约：LeaderboardService 构造函数必须接受可注入时间源——当前缺失（红）");
    }

    // ============ 行为红：固定时钟 + 北京时间语义 ============

    private object? Resolve(Type t)
    {
        if (t == typeof(IDbContextFactory<FamilyDbContext>)) return _familyFactory;
        if (t == typeof(IDbContextFactory<VaultDbContext>)) return _vaultFactory;
        if (t == typeof(VaultSettingsService))
            return new VaultSettingsService(_vaultFactory, NullLogger<VaultSettingsService>.Instance);
        if (t == typeof(LearnerService))
            return new LearnerService(_familyFactory, NullLogger<LearnerService>.Instance);
        if (t == typeof(CardRepository))
            return new CardRepository(
                new VaultSettingsService(_vaultFactory, NullLogger<VaultSettingsService>.Instance),
                _familyFactory,
                new LearnerService(_familyFactory, NullLogger<LearnerService>.Instance),
                NullLogger<CardRepository>.Instance);
        if (t == typeof(Microsoft.Extensions.Logging.ILogger<DailyCardService>))
            return NullLogger<DailyCardService>.Instance;
        if (t == typeof(Microsoft.Extensions.Logging.ILogger<LeaderboardService>))
            return NullLogger<LeaderboardService>.Instance;
        if (t == typeof(Microsoft.Extensions.Localization.IStringLocalizer<Baihua.Core.Localization.SharedResources>))
            return TestLocalizer.Create();
        throw new InvalidOperationException($"未提供构造参数: {t.FullName}");
    }

    private DailyCardService CreateDailyCardService()
    {
        var (injectable, compatible) = TimeSourceProbe.Probe(typeof(DailyCardService));
        Assert.True(injectable, "FAM-01 契约：DailyCardService 缺少可注入时间源（红）");
        if (!compatible)
            Assert.Fail("FAM-01：已找到时间注入点但与 FakeTimeProvider 不兼容——请对齐产品 ITimeProvider 形状或调整测试引用");
        return TimeSourceProbe.ConstructWithClock<DailyCardService>(_clock, Resolve);
    }

    private LeaderboardService CreateLeaderboardService()
    {
        var (injectable, compatible) = TimeSourceProbe.Probe(typeof(LeaderboardService));
        Assert.True(injectable, "FAM-01 契约：LeaderboardService 缺少可注入时间源（红）");
        if (!compatible)
            Assert.Fail("FAM-01：已找到时间注入点但与 FakeTimeProvider 不兼容——请对齐产品 ITimeProvider 形状或调整测试引用");
        return TimeSourceProbe.ConstructWithClock<LeaderboardService>(_clock, Resolve);
    }

    private void AddLearnerAndActivities(params (DateTime UtcTime, string Result)[] activities)
    {
        using var db = _familyFactory.CreateDbContext();
        var learner = db.LearnerProfiles.FirstOrDefault() ?? new LearnerProfile
        {
            Name = "小明",
            AvatarEmoji = "🙂",
            Color = "#007bff",
            IsDefault = true
        };
        if (learner.Id == 0) db.LearnerProfiles.Add(learner);
        db.SaveChanges();

        foreach (var (utc, result) in activities)
        {
            db.StudyActivities.Add(new StudyActivity
            {
                LearnerId = learner.Id,
                VaultId = VaultId,
                ActivityType = "study",
                CardId = $"card-{utc.Ticks}",
                Result = result,
                CreatedAt = utc
            });
        }
        db.SaveChanges();
    }

    [Fact]
    public void GetTodayProgress_WithBeijingFixedClock_CountsBeijingToday()
    {
        // 固定"现在"= 北京时间 08-07 07:30（= UTC 08-06T23:30）。
        // 插入 UTC 08-06T02:00 的一条学习记录：
        //   北京时间 = 08-06 10:00 → 是"昨天" → 不计入今日进度（期望 Completed == 0）
        //   当前实现用 UTC 日期（today = 08-06）→ 会算成 Completed == 1（红）
        AddLearnerAndActivities((new DateTime(2026, 8, 6, 2, 0, 0, DateTimeKind.Utc), "remember"));

        var svc = CreateDailyCardService();
        var progress = svc.GetTodayProgress(VaultId);

        Assert.Equal(0, progress.Completed);
    }

    [Fact]
    public void WeeklyLeaderboard_WithBeijingFixedClock_DoesNotFlipBeforeSundayEnd()
    {
        // 固定"现在"= 北京 08-07（周五）。本周 = 北京 08-03(周一) 00:00 起。
        // 插入 UTC 08-02T20:00 的学习记录 = 北京 08-03 04:00（本周一清晨）→ 应计入本周（期望 CardsStudied == 1）
        // 当前实现用 UTC 周界（UTC 周一 08-03 00:00）→ 08-02T20:00 落在上周（红）
        AddLearnerAndActivities((new DateTime(2026, 8, 2, 20, 0, 0, DateTimeKind.Utc), "remember"));

        var svc = CreateLeaderboardService();
        var entries = svc.GetWeeklyLeaderboardAsync().GetAwaiter().GetResult();

        var entry = Assert.Single(entries);
        Assert.Equal(1, entry.CardsStudied);
    }

    [Fact]
    public void Streak_WithBeijingFixedClock_FlipsAtBeijingMidnight()
    {
        // 固定"现在"= 北京 08-07 07:30。插入两条 UTC 同日（08-06）的记录：
        //   UTC 08-06T20:00 = 北京 08-07 04:00（今天）
        //   UTC 08-06T02:00 = 北京 08-06 10:00（昨天）
        // 北京口径：连续两天 → streak == 2
        // 当前实现用 UTC 日期：两条同属 UTC 08-06 → streak == 1（红）
        AddLearnerAndActivities(
            (new DateTime(2026, 8, 6, 20, 0, 0, DateTimeKind.Utc), "remember"),
            (new DateTime(2026, 8, 6, 2, 0, 0, DateTimeKind.Utc), "remember"));

        var svc = CreateDailyCardService();
        var progress = svc.GetTodayProgress(VaultId);

        Assert.Equal(2, progress.Streak);
    }
}

/// <summary>极简 IDbContextFactory 实现（避免 Moq 每处 setup）</summary>
public sealed class FakeDbFactory<T> : IDbContextFactory<T> where T : DbContext
{
    private readonly Func<T> _factory;
    public FakeDbFactory(Func<T> factory) => _factory = factory;
    public T CreateDbContext() => _factory();
    public Task<T> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(_factory());
}
