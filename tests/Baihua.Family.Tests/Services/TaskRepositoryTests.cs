using Baihua.Core.Models;
using Baihua.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// TaskRepository 单元测试
/// 使用 EF Core InMemory 提供者验证数据库 CRUD 逻辑
/// </summary>
public class TaskRepositoryTests
{
    private readonly TaskRepository _repository;
    private readonly Data.Entities.TaskEntity _sampleTask;

    public TaskRepositoryTests()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<Data.FamilyDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        var factory = new TestDbContextFactory(options);
        _repository = new TaskRepository(factory, Mock.Of<ILogger<TaskRepository>>());

        _sampleTask = new Data.Entities.TaskEntity
        {
            TaskId = "test-1",
            TaskType = "embedding",
            Status = "Pending",
            Input = """{"file":"test.md"}""",
            Progress = 0,
            ProgressMessage = "已创建",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    // ============ Create & Read ============

    [Fact]
    public void CreateTask_AddsToStore()
    {
        _repository.CreateTask(_sampleTask);

        var loaded = _repository.GetTaskById("test-1");
        Assert.NotNull(loaded);
        Assert.Equal("embedding", loaded.TaskType);
        Assert.Equal("Pending", loaded.Status);
    }

    [Fact]
    public void GetTaskById_NonExistent_ReturnsNull()
    {
        var loaded = _repository.GetTaskById("nonexistent");
        Assert.Null(loaded);
    }

    [Fact]
    public void GetAllTasks_ReturnsAll()
    {
        _repository.CreateTask(_sampleTask);
        _repository.CreateTask(new Data.Entities.TaskEntity
        {
            TaskId = "test-2", TaskType = "anki", Status = "Running",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        var tasks = _repository.GetAllTasks(10, 0);
        Assert.Equal(2, tasks.Count);
    }

    [Fact]
    public void GetAllTasks_Empty_ReturnsEmpty()
    {
        var tasks = _repository.GetAllTasks(10, 0);
        Assert.Empty(tasks);
    }

    [Fact]
    public void GetAllTasks_RespectsPaging()
    {
        for (int i = 0; i < 5; i++)
        {
            _repository.CreateTask(new Data.Entities.TaskEntity
            {
                TaskId = $"page-{i}", TaskType = "t", Status = "Pending",
                CreatedAt = DateTime.UtcNow.AddMinutes(-i), UpdatedAt = DateTime.UtcNow
            });
        }

        Assert.Equal(3, _repository.GetAllTasks(3, 0).Count);
        Assert.Equal(2, _repository.GetAllTasks(3, 3).Count);
        Assert.Empty(_repository.GetAllTasks(10, 10));
    }

    [Fact]
    public void GetTasksByStatus_FiltersCorrectly()
    {
        _repository.CreateTask(_sampleTask);
        _repository.CreateTask(new Data.Entities.TaskEntity
        {
            TaskId = "running-1", TaskType = "t", Status = "Running",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        _repository.CreateTask(new Data.Entities.TaskEntity
        {
            TaskId = "success-1", TaskType = "t", Status = "Success",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        Assert.Single(_repository.GetTasksByStatus("Pending"));
        Assert.Single(_repository.GetTasksByStatus("Running"));
        Assert.Single(_repository.GetTasksByStatus("Success"));
        Assert.Empty(_repository.GetTasksByStatus("Failed"));
    }

    // ============ Delete ============

    [Fact]
    public void DeleteTask_RemovesFromDb()
    {
        _repository.CreateTask(_sampleTask);
        Assert.NotNull(_repository.GetTaskById("test-1"));

        _repository.DeleteTask("test-1");
        Assert.Null(_repository.GetTaskById("test-1"));
    }

    [Fact]
    public void DeleteTask_NonExistent_ReturnsTrue()
    {
        Assert.True(_repository.DeleteTask("nonexistent"));
    }

    [Fact]
    public void DeleteOldTasks_RemovesBeforeCutoff()
    {
        _repository.CreateTask(_sampleTask); // 刚创建
        _repository.CreateTask(new Data.Entities.TaskEntity
        {
            TaskId = "old-1", TaskType = "old", Status = "Success",
            CreatedAt = DateTime.UtcNow.AddDays(-10), UpdatedAt = DateTime.UtcNow.AddDays(-10)
        });

        var deleted = _repository.DeleteOldTasks(DateTime.UtcNow.AddDays(-1));
        Assert.True(deleted >= 1);
        Assert.NotNull(_repository.GetTaskById("test-1")); // 新任务还在
        Assert.Null(_repository.GetTaskById("old-1"));
    }

    [Fact]
    public void DeleteCompletedTasks_RemovesOnlyCompleted()
    {
        _repository.CreateTask(_sampleTask); // Pending
        _repository.CreateTask(new Data.Entities.TaskEntity
        {
            TaskId = "s-1", TaskType = "t", Status = "Success",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        _repository.CreateTask(new Data.Entities.TaskEntity
        {
            TaskId = "f-1", TaskType = "t", Status = "Failed",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        // 验证开始状态
        Assert.Equal(3, _repository.GetTaskCount());

        _repository.DeleteCompletedTasks();

        // 验证结果
        Assert.Equal(1, _repository.GetTaskCount());
        Assert.NotNull(_repository.GetTaskById("test-1")); // Pending 还在
        Assert.Null(_repository.GetTaskById("s-1"));
        Assert.Null(_repository.GetTaskById("f-1"));
    }

    [Fact]
    public void DeleteAllTasks_ClearsEverything()
    {
        _repository.CreateTask(_sampleTask);
        _repository.CreateTask(new Data.Entities.TaskEntity
        {
            TaskId = "other", TaskType = "t", Status = "Pending",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        _repository.DeleteAllTasks();
        Assert.Equal(0, _repository.GetTaskCount());
    }

    // ============ Update ============

    [Fact]
    public void UpdateStatus_ModifiesFields()
    {
        _repository.CreateTask(_sampleTask);

        var now = DateTime.UtcNow;
        _repository.UpdateStatus("test-1", "Running", null, null, 0, "开始执行", now, null);

        var updated = _repository.GetTaskById("test-1");
        Assert.Equal("Running", updated.Status);
        Assert.Equal("开始执行", updated.ProgressMessage);
        Assert.NotNull(updated.StartedAt);
    }

    [Fact]
    public void UpdateProgress_ModifiesProgress()
    {
        _repository.CreateTask(_sampleTask);

        _repository.UpdateProgress("test-1", 50, "进行中", DateTime.UtcNow);

        var updated = _repository.GetTaskById("test-1");
        Assert.Equal(50, updated.Progress);
        Assert.Equal("进行中", updated.ProgressMessage);
        Assert.Equal("Running", updated.Status); // startedAt 非 null 时应自动设为 Running
    }

    [Fact]
    public void UpdateProgress_NoStartTime_DoesNotChangeStatus()
    {
        _repository.CreateTask(_sampleTask);

        _repository.UpdateProgress("test-1", 30, "30%", null);

        var updated = _repository.GetTaskById("test-1");
        Assert.Equal(30, updated.Progress);
        Assert.Equal("Pending", updated.Status); // 保持原状态
    }

    [Fact]
    public void UpdateStatus_NonExistent_DoesNotThrow()
    {
        _repository.UpdateStatus("nonexistent", "Running", null, null, 0, null, null, null);
        // 静默通过
    }

    // ============ Count ============

    [Fact]
    public void GetTaskCount_ReturnsCorrectNumber()
    {
        Assert.Equal(0, _repository.GetTaskCount());

        _repository.CreateTask(_sampleTask);
        Assert.Equal(1, _repository.GetTaskCount());

        _repository.CreateTask(new Data.Entities.TaskEntity
        {
            TaskId = "another", TaskType = "t", Status = "Pending",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        Assert.Equal(2, _repository.GetTaskCount());

        _repository.DeleteTask("test-1");
        Assert.Equal(1, _repository.GetTaskCount());
    }

    // ============ EF InMemory helper ============

    private class TestDbContextFactory : IDbContextFactory<Data.FamilyDbContext>
    {
        private readonly DbContextOptions<Data.FamilyDbContext> _options;
        public TestDbContextFactory(DbContextOptions<Data.FamilyDbContext> options) => _options = options;
        public Data.FamilyDbContext CreateDbContext() => new(_options);
        public Task<Data.FamilyDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(new Data.FamilyDbContext(_options));
    }
}
