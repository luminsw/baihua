using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Modules.Family.Services.Todo;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// 个人待办清单服务测试（单用户、极简：标题 + 完成状态）。
/// </summary>
public class TodoServiceTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly TodoService _service;
    private readonly IDbContextFactory<FamilyDbContext> _factory;

    public TodoServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var options = new DbContextOptionsBuilder<FamilyDbContext>()
            .UseSqlite(_conn).Options;
        using (var ctx = new FamilyDbContext(options)) ctx.Database.EnsureCreated();
        _factory = new TestDbFactory(options);
        _service = new TodoService(_factory);
    }

    public void Dispose() => _conn.Dispose();

    // ============ 创建 ============

    [Fact]
    public async Task Create_TrimsAndPersists()
    {
        var item = await _service.CreateAsync("  买牛奶  ");

        Assert.NotNull(item);
        Assert.Equal("买牛奶", item!.Title);
        Assert.False(item.IsDone);
        Assert.Null(item.CompletedAt);

        using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.TodoItems.CountAsync());
    }

    [Fact]
    public async Task Create_EmptyOrWhitespace_ReturnsNull()
    {
        Assert.Null(await _service.CreateAsync(""));
        Assert.Null(await _service.CreateAsync("   "));
        Assert.Null(await _service.CreateAsync(null!));
    }

    [Fact]
    public async Task Create_TooLong_ReturnsNull()
    {
        var tooLong = new string('长', 201);
        Assert.Null(await _service.CreateAsync(tooLong));
    }

    // ============ 查询 ============

    [Fact]
    public async Task GetAll_ReturnsInCreationOrder()
    {
        var a = await _service.CreateAsync("第一项");
        var b = await _service.CreateAsync("第二项");

        var items = await _service.GetAllAsync();

        Assert.Equal(2, items.Count);
        Assert.Equal("第一项", items[0].Title);
        Assert.Equal("第二项", items[1].Title);
        Assert.True(items[0].Id < items[1].Id);
    }

    // ============ 更新 ============

    [Fact]
    public async Task Update_ToggleDone_SetsAndClearsCompletedAt()
    {
        var created = (await _service.CreateAsync("锻炼"))!;

        var done = await _service.UpdateAsync(created.Id, null, true, null);
        Assert.NotNull(done);
        Assert.True(done!.IsDone);
        Assert.NotNull(done.CompletedAt);

        var undone = await _service.UpdateAsync(created.Id, null, false, null);
        Assert.NotNull(undone);
        Assert.False(undone!.IsDone);
        Assert.Null(undone.CompletedAt);
    }

    [Fact]
    public async Task Update_Rename_TrimsTitle()
    {
        var created = (await _service.CreateAsync("旧标题"))!;

        var renamed = await _service.UpdateAsync(created.Id, "  新标题  ", null, null);

        Assert.NotNull(renamed);
        Assert.Equal("新标题", renamed!.Title);
        Assert.False(renamed.IsDone);
    }

    [Fact]
    public async Task Update_NotFound_ReturnsNull()
    {
        Assert.Null(await _service.UpdateAsync(9999, "不存在", null, null));
        Assert.Null(await _service.UpdateAsync(9999, null, true, null));
    }

    // ============ 删除 ============

    [Fact]
    public async Task Delete_Existing_RemovesAndReturnsTrue()
    {
        var created = (await _service.CreateAsync("要删除的"))!;

        var deleted = await _service.DeleteAsync(created.Id);

        Assert.True(deleted);
        using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.TodoItems.CountAsync());
    }

    [Fact]
    public async Task Delete_NotFound_ReturnsFalse()
    {
        Assert.False(await _service.DeleteAsync(9999));
    }

    // ============ 目标（一级） ============

    [Fact]
    public async Task CreateGoal_TrimsAndPersists()
    {
        var goal = await _service.CreateGoalAsync("  办理护照  ");

        Assert.NotNull(goal);
        Assert.Equal("办理护照", goal!.Title);

        using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.TodoGoals.CountAsync());
    }

    [Fact]
    public async Task CreateGoal_EmptyOrTooLong_ReturnsNull()
    {
        Assert.Null(await _service.CreateGoalAsync(""));
        Assert.Null(await _service.CreateGoalAsync("   "));
        Assert.Null(await _service.CreateGoalAsync(new string('长', 201)));
    }

    [Fact]
    public async Task GetGoals_ReturnsGoalsWithItemsInOrder()
    {
        var goal = (await _service.CreateGoalAsync("目标"))!;
        var a = (await _service.CreateAsync("事项一", goal.Id))!;
        var b = (await _service.CreateAsync("事项二", goal.Id))!;
        await _service.CreateAsync("无目标事项");

        var goals = await _service.GetGoalsAsync();

        Assert.Single(goals);
        Assert.Equal("目标", goals[0].Title);
        Assert.Equal(2, goals[0].Items.Count);
        Assert.Equal("事项一", goals[0].Items[0].Title);
        Assert.Equal("事项二", goals[0].Items[1].Title);
        Assert.True(a.Id < b.Id);
    }

    [Fact]
    public async Task CreateGoalWithItems_Valid_CreatesGoalAndItems()
    {
        (string Title, string? Note)[] items =
        [
            ("准备材料", "身份证原件及复印件、户口本、2 寸白底照片"),
            ("去派出所申请", "户籍所在地派出所户籍窗口，记得提前预约"),
            ("", "空标题会被跳过"),
        ];
        var goal = await _service.CreateGoalWithItemsAsync("办理身份证", items);

        Assert.NotNull(goal);
        Assert.Equal("办理身份证", goal!.Title);
        Assert.Equal(2, goal.Items.Count);
        Assert.Equal("准备材料", goal.Items[0].Title);
        Assert.Equal("身份证原件及复印件、户口本、2 寸白底照片", goal.Items[0].Note);

        using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.TodoGoals.CountAsync());
        Assert.Equal(2, await db.TodoItems.CountAsync());
    }

    [Fact]
    public async Task CreateGoalWithItems_AllInvalid_ReturnsNullAndPersistsNothing()
    {
        var goal = await _service.CreateGoalWithItemsAsync("目标", new[]
        {
            ("", "空标题"),
            ("  ", null),
        });

        Assert.Null(goal);
        using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.TodoGoals.CountAsync());
        Assert.Equal(0, await db.TodoItems.CountAsync());
    }

    [Fact]
    public async Task DeleteGoal_CascadeDeletesItsItems()
    {
        var goal = (await _service.CreateGoalAsync("要删的目标"))!;
        var item = (await _service.CreateAsync("子待办", goal.Id))!;
        await _service.CreateAsync("无关待办");

        var deleted = await _service.DeleteGoalAsync(goal.Id);

        Assert.True(deleted);
        using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.TodoGoals.CountAsync());
        Assert.Equal(0, await db.TodoItems.CountAsync(i => i.GoalId == goal.Id));
        Assert.Equal(1, await db.TodoItems.CountAsync()); // 无关待办保留
        Assert.Null(await db.TodoItems.FirstOrDefaultAsync(i => i.Id == item.Id));
    }

    [Fact]
    public async Task DeleteGoal_NotFound_ReturnsFalse()
    {
        Assert.False(await _service.DeleteGoalAsync(9999));
    }

    // ============ Note（执行指引） ============

    [Fact]
    public async Task Create_WithNote_TrimsAndPersists()
    {
        var item = await _service.CreateAsync("登录网站", note: "  移民局小程序，提前预约  ");

        Assert.NotNull(item);
        Assert.Equal("移民局小程序，提前预约", item!.Note);
    }

    [Fact]
    public async Task Update_WithNote_UpdatesNote()
    {
        var created = (await _service.CreateAsync("办事"))!;

        var updated = await _service.UpdateAsync(created.Id, null, null, " 需要带身份证 ");

        Assert.NotNull(updated);
        Assert.Equal("需要带身份证", updated!.Note);
    }

    [Fact]
    public async Task Update_NoteTooLong_ReturnsNull()
    {
        var created = (await _service.CreateAsync("办事"))!;

        Assert.Null(await _service.UpdateAsync(created.Id, null, null, new string('长', 1001)));
    }

    private sealed class TestDbFactory(DbContextOptions<FamilyDbContext> options) : IDbContextFactory<FamilyDbContext>
    {
        public FamilyDbContext CreateDbContext() => new(options);

        public Task<FamilyDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
