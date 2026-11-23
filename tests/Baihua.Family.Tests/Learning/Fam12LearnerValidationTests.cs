using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Modules.Family.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// FAM-12 红测试：Learner 输入校验。
///
/// 验收标准：
///   - 空名/全空格/超长名/非法 emoji → 阻止创建 + 明确错误
///   - 正常输入 → 创建成功（回归锚）
///
/// 红测试方式：当前 CreateAsync 只 Trim 不校验，任何输入都创建成功 → 断言"阻止 + 抛错"即红。
/// 失败信号契约：输入校验失败应抛出异常（ArgumentException 族或自定义校验异常）。
/// </summary>
public class Fam12LearnerValidationTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly IDbContextFactory<FamilyDbContext> _factory;
    private const int MaxNameLength = 20;

    public Fam12LearnerValidationTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var options = new DbContextOptionsBuilder<FamilyDbContext>()
            .UseSqlite(_conn).Options;
        using (var ctx = new FamilyDbContext(options)) ctx.Database.EnsureCreated();
        _factory = new FakeDbFactory<FamilyDbContext>(() => new FamilyDbContext(options));
    }

    public void Dispose() => _conn.Dispose();

    private LearnerService CreateService()
        => new LearnerService(_factory, NullLogger<LearnerService>.Instance);

    private int CountLearners()
    {
        using var db = _factory.CreateDbContext();
        return db.LearnerProfiles.Count();
    }

    // ============ 校验红测试：输入非法 → 阻止 + 抛错 ============

    [Fact]
    public void EmptyName_IsRejected_WithError()
    {
        var svc = CreateService();
        Assert.ThrowsAny<Exception>(
            () => svc.CreateAsync("", "🙂", "#007bff").GetAwaiter().GetResult());
        Assert.Equal(0, CountLearners());
    }

    [Fact]
    public void WhitespaceOnlyName_IsRejected_WithError()
    {
        var svc = CreateService();
        Assert.ThrowsAny<Exception>(
            () => svc.CreateAsync("   ", "🙂", "#007bff").GetAwaiter().GetResult());
        Assert.Equal(0, CountLearners());
    }

    [Fact]
    public void OverlongName_IsRejected_WithError()
    {
        var svc = CreateService();
        var longName = new string('名', MaxNameLength + 1);
        Assert.ThrowsAny<Exception>(
            () => svc.CreateAsync(longName, "🙂", "#007bff").GetAwaiter().GetResult());
        Assert.Equal(0, CountLearners());
    }

    [Fact]
    public void NonEmojiAvatar_IsRejected_WithError()
    {
        var svc = CreateService();
        // 非法：普通文本不是 emoji
        Assert.ThrowsAny<Exception>(
            () => svc.CreateAsync("小明", "not-an-emoji", "#007bff").GetAwaiter().GetResult());
        Assert.Equal(0, CountLearners());
    }

    [Fact]
    public void MultiEmojiAvatar_IsRejected_WithError()
    {
        var svc = CreateService();
        // 非法：多个 emoji 字符（要求单个 emoji）
        Assert.ThrowsAny<Exception>(
            () => svc.CreateAsync("小明", "👍👍", "#007bff").GetAwaiter().GetResult());
        Assert.Equal(0, CountLearners());
    }

    // ============ 回归锚：正常输入 → 创建成功 ============

    [Fact]
    public void ValidNameAndEmoji_CreatesLearner()
    {
        var svc = CreateService();
        var learner = svc.CreateAsync("小明", "🙂", "#007bff").GetAwaiter().GetResult();

        Assert.NotNull(learner);
        Assert.Equal("小明", learner.Name);
        Assert.Equal(1, CountLearners());
    }
}
