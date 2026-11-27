using Baihua.Core.Services;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Family.Tests.Learning;
using Baihua.Modules.Family.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// StudyRecordMigrationService 测试（旧 .study/daily-*.json → SQLite StudyActivities）：
/// ① 正常迁移：文件名日期 → UTC 当日 12 点，Answers 逐卡片入库，坏文件名跳过
/// ② 幂等：已有任何 StudyActivities 记录则整体跳过；重复执行不产生重复记录
/// ③ 无默认学习者 → 不迁移
/// ④ 同天同卡片同学习者去重
/// </summary>
public class StudyRecordMigrationTests : IDisposable
{
    private readonly string _vaultDir;
    private readonly SqliteConnection _connection;
    private readonly SqliteConnection _vaultConn;
    private readonly FakeDbFactory<FamilyDbContext> _familyFactory;
    private readonly FakeDbFactory<VaultDbContext> _vaultFactory;
    private readonly ServiceProvider _serviceProvider;

    public StudyRecordMigrationTests()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "StudyMigTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultDir);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<FamilyDbContext>().UseSqlite(_connection).Options;
        using (var ctx = new FamilyDbContext(options)) ctx.Database.EnsureCreated();
        _familyFactory = new FakeDbFactory<FamilyDbContext>(() => new FamilyDbContext(options));

        _vaultConn = new SqliteConnection("DataSource=:memory:");
        _vaultConn.Open();
        var vaultOptions = new DbContextOptionsBuilder<VaultDbContext>().UseSqlite(_vaultConn).Options;
        using (var ctx = new VaultDbContext(vaultOptions)) ctx.Database.EnsureCreated();
        _vaultFactory = new FakeDbFactory<VaultDbContext>(() => new VaultDbContext(vaultOptions));

        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<FamilyDbContext>>(_familyFactory);
        services.AddSingleton(new VaultSettingsService(_vaultFactory, NullLogger<VaultSettingsService>.Instance));
        services.AddSingleton(new LearnerService(_familyFactory, NullLogger<LearnerService>.Instance));
        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
        _vaultConn.Dispose();
        try { Directory.Delete(_vaultDir, recursive: true); }
        catch { /* 忽略清理失败 */ }
    }

    // ===================== Helpers =====================

    private async Task SeedVaultAsync()
    {
        await using var ctx = await _vaultFactory.CreateDbContextAsync();
        ctx.Vaults.Add(new Vault { VaultId = "mig-vault", Name = "迁移库", Path = _vaultDir, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
    }

    private async Task SeedDefaultLearnerAsync()
    {
        await using var ctx = await _familyFactory.CreateDbContextAsync();
        ctx.LearnerProfiles.Add(new LearnerProfile { Name = "默认学习者", IsDefault = true });
        await ctx.SaveChangesAsync();
    }

    /// <summary>daily-*.json 用 PascalCase key（服务端用默认序列化选项反序列化）</summary>
    private void WriteDailyFile(string date, string json)
    {
        var studyDir = Path.Combine(_vaultDir, "cards", ".study");
        Directory.CreateDirectory(studyDir);
        File.WriteAllText(Path.Combine(studyDir, $"daily-{date}.json"), json);
    }

    private StudyRecordMigrationService CreateService()
        => new(_serviceProvider, NullLogger<StudyRecordMigrationService>.Instance);

    private async Task<List<StudyActivity>> GetActivitiesAsync()
    {
        await using var ctx = await _familyFactory.CreateDbContextAsync();
        return await ctx.StudyActivities.OrderBy(a => a.CardId).ToListAsync();
    }

    // ===================== 测试 =====================

    [Fact]
    public async Task MigratesDailyFiles_ToStudyActivities_WithUtcNoon()
    {
        await SeedVaultAsync();
        await SeedDefaultLearnerAsync();
        WriteDailyFile("2025-01-15", """{"Completed": 2, "Answers": {"card-1": "remember", "card-2": "forgot"}}""");
        WriteDailyFile("not-a-date", """{"Completed": 0, "Answers": {"card-x": "remember"}}"""); // 坏文件名跳过

        await CreateService().MigrateAsync();

        var activities = await GetActivitiesAsync();
        Assert.Equal(2, activities.Count);
        Assert.All(activities, a =>
        {
            Assert.Equal("study", a.ActivityType);
            Assert.Equal("mig-vault", a.VaultId);
        });
        // 注：不断言 Kind——SQLite 存 TEXT，EF Core 读回恒为 Unspecified（驱动行为）
        var card1 = activities.Single(a => a.CardId == "card-1");
        Assert.Equal("remember", card1.Result);
        Assert.Equal(new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc), card1.CreatedAt);
    }

    [Fact]
    public async Task SecondRun_NoDuplicateRecords()
    {
        await SeedVaultAsync();
        await SeedDefaultLearnerAsync();
        WriteDailyFile("2025-01-15", """{"Completed": 1, "Answers": {"card-1": "hard"}}""");

        var svc = CreateService();
        await svc.MigrateAsync();
        await svc.MigrateAsync(); // 二次执行：库中已有记录 → 整体跳过

        Assert.Equal(1, (await GetActivitiesAsync()).Count);
    }

    [Fact]
    public async Task ExistingStudyActivities_SkipsMigrationEntirely()
    {
        await SeedVaultAsync();
        await SeedDefaultLearnerAsync();
        WriteDailyFile("2025-01-15", """{"Completed": 1, "Answers": {"card-1": "remember"}}""");
        await using (var ctx = await _familyFactory.CreateDbContextAsync())
        {
            ctx.StudyActivities.Add(new StudyActivity { LearnerId = 1, VaultId = "other", ActivityType = "chat", CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        await CreateService().MigrateAsync();

        var activities = await GetActivitiesAsync();
        var activity = Assert.Single(activities);
        Assert.Equal("chat", activity.ActivityType); // 只有预置那条，daily 文件未迁移
    }

    [Fact]
    public async Task NoDefaultLearner_NothingMigrated()
    {
        await SeedVaultAsync();
        WriteDailyFile("2025-01-15", """{"Completed": 1, "Answers": {"card-1": "remember"}}""");

        await CreateService().MigrateAsync();

        Assert.Empty(await GetActivitiesAsync());
    }

    [Fact]
    public async Task PreExistingSameDaySameCard_MigrationSkippedEntirely()
    {
        // 注意实际契约：只要库里有任何 StudyActivity，MigrateAsync 在入口就整体跳过——
        // 循环内的逐卡片去重（alreadyExists 检查）因早退守卫在前，实际不可达（死代码）。
        // 幂等性由"整体跳过"这一更粗的守卫保证。
        await SeedVaultAsync();
        await SeedDefaultLearnerAsync();

        var dateUtc = new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        await using (var ctx = await _familyFactory.CreateDbContextAsync())
        {
            var learner = await ctx.LearnerProfiles.FirstAsync(l => l.IsDefault);
            ctx.StudyActivities.Add(new StudyActivity
            {
                LearnerId = learner.Id, VaultId = "mig-vault", ActivityType = "study",
                CardId = "card-1", Result = "remember", CreatedAt = dateUtc
            });
            await ctx.SaveChangesAsync();
        }

        WriteDailyFile("2025-01-15", """{"Completed": 2, "Answers": {"card-1": "hard", "card-2": "remember"}}""");

        await CreateService().MigrateAsync();

        var activities = await GetActivitiesAsync();
        var activity = Assert.Single(activities); // card-2 不会补入
        Assert.Equal("card-1", activity.CardId);
        Assert.Equal("remember", activity.Result); // 原记录未被覆盖
    }

    [Fact]
    public async Task EmptyAnswersFile_Skipped()
    {
        await SeedVaultAsync();
        await SeedDefaultLearnerAsync();
        WriteDailyFile("2025-01-15", """{"Completed": 0, "Answers": {}}""");

        await CreateService().MigrateAsync();

        Assert.Empty(await GetActivitiesAsync());
    }
}
