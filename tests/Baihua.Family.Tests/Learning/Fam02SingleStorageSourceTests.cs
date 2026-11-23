using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Modules.Family.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// FAM-02 红测试：消除双存储源（daily-*.json 文件 + StudyActivities DB）。
///
/// 验收标准：
///   - GetTodayProgress 以 DB 为单一事实源，同一份数据不会因读取路径不同而给不同结果
///   - 不存在"先查 DB 失败 → 静默 fallback 文件"掩盖不一致的路径
///
/// 红测试方式：
///   1) 行为红：DB 查询失败时 GetTodayProgress 不得静默返回文件 fallback 结果（当前会 fallback → 红）
///   2) 回归锚：文件写入失败时 DB 仍是权威源（当前 DB 正常即绿，作为单一源正面锚）
/// </summary>
public class Fam02SingleStorageSourceTests : IDisposable
{
    private readonly SqliteConnection _familyConn;
    private readonly SqliteConnection _vaultConn;
    private readonly IDbContextFactory<FamilyDbContext> _familyFactory;
    private readonly IDbContextFactory<VaultDbContext> _vaultFactory;

    private const string VaultId = "vault-fam02";

    public Fam02SingleStorageSourceTests()
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

    private DailyCardService CreateService(IDbContextFactory<FamilyDbContext>? familyFactory = null)
    {
        var vaultSettings = new VaultSettingsService(_vaultFactory, NullLogger<VaultSettingsService>.Instance);
        var learnerService = new LearnerService(_familyFactory, NullLogger<LearnerService>.Instance);
        var cardRepo = new CardRepository(vaultSettings, _familyFactory, learnerService, NullLogger<CardRepository>.Instance);
        return new DailyCardService(
            familyFactory ?? _familyFactory,
            learnerService,
            cardRepo,
            NullLogger<DailyCardService>.Instance,
            TestLocalizer.Create(),
            new Baihua.Core.Time.SystemTimeProvider());
    }

    private void AddStudyActivity(DateTime createdAt)
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

        db.StudyActivities.Add(new StudyActivity
        {
            LearnerId = learner.Id,
            VaultId = VaultId,
            ActivityType = "study",
            CardId = "card-1",
            Result = "remember",
            CreatedAt = createdAt
        });
        db.SaveChanges();
    }

    [Fact]
    public void GetTodayProgress_DbFailure_DoesNotSilentlyFallbackToFiles()
    {
        // 契约：DB 是单一事实源——DB 查询失败时不得静默 fallback 到 daily-*.json 文件
        // （当前实现：GetTodayProgressFromDb 抛异常 → catch 返回 null → 静默 fallback 文件 → 红）
        AddStudyActivity(DateTime.UtcNow);

        var brokenFactory = new Mock<IDbContextFactory<FamilyDbContext>>();
        brokenFactory.Setup(f => f.CreateDbContext()).Throws(new InvalidOperationException("db down"));
        brokenFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var svc = CreateService(brokenFactory.Object);

        // 期望：明确失败（抛异常或显式失败态），而不是静默返回文件 fallback 的空进度
        Assert.ThrowsAny<Exception>(() => svc.GetTodayProgress(VaultId));
    }

    [Fact]
    public void RecordAnswer_FileWriteFailure_DbRemainsAuthoritative()
    {
        // 回归锚：vault 未配置（文件路径解析为空 → 文件写入必然失败）时，
        // DB 写入仍成功，GetTodayProgress 必须返回 DB 的进度（Completed == 1）。
        // 当前实现 DB 正常时读 DB → 绿。锁定"DB 为权威源"的正面行为。
        var svc = CreateService();
        var result = svc.RecordAnswerAsync(VaultId, "card-1", "remember").GetAwaiter().GetResult();
        Assert.True(result, "文件写入失败不应导致整体失败（DB 仍成功）");

        var progress = svc.GetTodayProgress(VaultId);
        Assert.Equal(1, progress.Completed);
    }
}
