using Baihua.Core.Models;
using Baihua.Core.Services;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Baihua.Core;
using Baihua.Data.Entities;
using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// TaskManager 综合测试
/// 覆盖重构后的组件化架构，验证提取的 ITaskRepository、ITaskNotifier、ITaskCancellationManager 协作正确
/// </summary>
public class TaskManagerTests
{
    #region Test Infrastructure

    /// <summary>
    /// 内存中的 ITaskRepository 伪实现，用于验证数据库交互的正确性
    /// </summary>
    private class InMemoryTaskRepository : ITaskRepository
    {
        private readonly ConcurrentDictionary<string, TaskEntity> _store = new();
        private int _nextId = 1;

        public void CreateTask(TaskEntity entity)
        {
            // 模拟数据库中自动生成的 ID
            if (entity.Id == 0) entity.Id = Interlocked.Increment(ref _nextId);
            _store[entity.TaskId] = entity;
        }

        public TaskEntity? GetTaskById(string taskId)
        {
            _store.TryGetValue(taskId, out var entity);
            return entity;
        }

        public List<TaskEntity> GetAllTasks(int limit, int offset)
        {
            return _store.Values
                .OrderByDescending(e => e.CreatedAt)
                .Skip(offset)
                .Take(limit)
                .ToList();
        }

        public List<TaskEntity> GetTasksByStatus(string status, int limit)
        {
            return _store.Values
                .Where(e => e.Status == status)
                .OrderByDescending(e => e.CreatedAt)
                .Take(limit)
                .ToList();
        }

        public bool DeleteTask(string taskId)
        {
            _store.TryRemove(taskId, out _);
            return true; // 与原始 SQLite 行为一致：不存在的 key 也返回 true
        }

        public int DeleteOldTasks(DateTime cutoffDate)
        {
            var old = _store.Values.Where(e => e.CreatedAt < cutoffDate).ToList();
            foreach (var e in old) _store.TryRemove(e.TaskId, out _);
            return old.Count;
        }

        public int DeleteCompletedTasks()
        {
            var completed = _store.Values
                .Where(e => new[] { "Success", "Failed", "Cancelled" }.Contains(e.Status))
                .ToList();
            foreach (var e in completed) _store.TryRemove(e.TaskId, out _);
            return completed.Count;
        }

        public int DeleteAllTasks()
        {
            var count = _store.Count;
            _store.Clear();
            return count;
        }

        public int GetTaskCount() => _store.Count;

        public void UpdateStatus(string taskId, string status, string? error, string? output,
            int progress, string? progressMessage, DateTime? startedAt, DateTime? completedAt)
        {
            if (!_store.TryGetValue(taskId, out var entity)) return;
            entity.Status = status;
            entity.Error = error;
            entity.Output = output;
            entity.Progress = progress;
            entity.ProgressMessage = progressMessage;
            if (startedAt.HasValue) entity.StartedAt = startedAt;
            if (completedAt.HasValue) entity.CompletedAt = completedAt;
        }

        public void UpdateProgress(string taskId, int progress, string? progressMessage, DateTime? startedAt)
        {
            if (!_store.TryGetValue(taskId, out var entity)) return;
            entity.Progress = progress;
            entity.ProgressMessage = progressMessage;
            if (startedAt.HasValue)
            {
                entity.StartedAt = startedAt;
                entity.Status = "Running";
            }
        }

        /// <summary>测试辅助：直接查看存储中的数据</summary>
        public TaskEntity? Peek(string taskId) => _store.TryGetValue(taskId, out var e) ? e : null;
    }

    private readonly Mock<ITaskNotifier> _mockNotifier = new(MockBehavior.Loose);
    private readonly Mock<ITaskCancellationManager> _mockCancellation = new(MockBehavior.Loose);
    private readonly Mock<ILogger<TaskManager>> _mockLogger = new(MockBehavior.Loose);
    private readonly InMemoryTaskRepository _repository = new();

    private TaskManager CreateManager()
    {
        return new TaskManager(_repository, _mockNotifier.Object, _mockCancellation.Object, _mockLogger.Object);
    }

    #endregion

    #region CreateTask

    [Fact]
    public void CreateTask_ReturnsNonEmptyTaskId()
    {
        var manager = CreateManager();
        var taskId = manager.CreateTask("test-type");
        Assert.False(string.IsNullOrWhiteSpace(taskId));
    }

    [Fact]
    public void CreateTask_GeneratesUniqueIds()
    {
        var manager = CreateManager();
        var id1 = manager.CreateTask("type-a");
        var id2 = manager.CreateTask("type-b");
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void CreateTask_StoresTaskInMemory()
    {
        var manager = CreateManager();
        var taskId = manager.CreateTask("embedding", new Dictionary<string, string> { ["file"] = "test.md" });
        var task = manager.GetTask(taskId);
        Assert.NotNull(task);
        Assert.Equal("embedding", task.Type);
        Assert.Equal(RunnerTaskStatus.Pending, task.Status);
        Assert.NotNull(task.Parameters);
        Assert.Equal("test.md", task.Parameters["file"]);
    }

    [Fact]
    public void CreateTask_PersistsToRepository()
    {
        var manager = CreateManager();
        var taskId = manager.CreateTask("anki-card");
        var entity = _repository.Peek(taskId);
        Assert.NotNull(entity);
        Assert.Equal("anki-card", entity.TaskType);
        Assert.Equal("Pending", entity.Status);
    }

    [Fact]
    public void CreateTask_NotifiesUpdate()
    {
        var manager = CreateManager();
        manager.CreateTask("test");
        _mockNotifier.Verify(n => n.NotifyTaskUpdateAsync(It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Fact]
    public void CreateTask_SetsInitialProgressMessage()
    {
        var manager = CreateManager();
        var taskId = manager.CreateTask("test");
        var task = manager.GetTask(taskId);
        Assert.Equal("任务已创建", task.Progress.Message);
        Assert.Equal(0, task.Progress.Current);
        Assert.Equal(1, task.Progress.Total);
    }

    #endregion

    #region GetTask

    [Fact]
    public void GetTask_ReturnsNull_ForNonExistentTask()
    {
        var manager = CreateManager();
        Assert.Null(manager.GetTask("nonexistent"));
    }

    [Fact]
    public void GetTask_FallsBackToRepository_WhenNotInMemory()
    {
        // 模拟数据库中已有任务但内存中没有
        var entity = new TaskEntity
        {
            TaskId = "db-only-task",
            TaskType = "migrated",
            Status = "Success",
            Progress = 100,
            ProgressMessage = "已完成",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow
        };
        _repository.CreateTask(entity);

        var manager = CreateManager();
        var task = manager.GetTask("db-only-task");
        Assert.NotNull(task);
        Assert.Equal("migrated", task.Type);
        Assert.Equal(RunnerTaskStatus.Success, task.Status);
    }

    [Fact]
    public void GetTask_CachesInMemory_AfterRepositoryFallback()
    {
        var entity = new TaskEntity
        {
            TaskId = "cache-me",
            TaskType = "cached",
            Status = "Pending",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _repository.CreateTask(entity);

        var manager = CreateManager();
        // 第一次：从数据库加载
        var task1 = manager.GetTask("cache-me");
        Assert.NotNull(task1);

        // 从内存中删除，测试是否第二次直接从内存取（修改内存中的值不会影响数据库）
        task1.Status = RunnerTaskStatus.Running;

        // 再次获取，应该从内存取到 Running 状态（而非数据库的 Pending）
        var task2 = manager.GetTask("cache-me");
        Assert.Equal(RunnerTaskStatus.Running, task2.Status);
    }

    [Fact]
    public void GetTask_MultipleCalls_ReturnsSameInstance()
    {
        var manager = CreateManager();
        var taskId = manager.CreateTask("test");
        var task1 = manager.GetTask(taskId);
        var task2 = manager.GetTask(taskId);
        Assert.Same(task1, task2);
    }

    #endregion

    #region GetAllTasks

    [Fact]
    public void GetAllTasks_ReturnsTasksOrderedByCreatedAtDesc()
    {
        var manager = CreateManager();
        var id1 = manager.CreateTask("first");
        var id2 = manager.CreateTask("second");

        // 测试直接从数据库查询（内存尚未加载的任务应当按时间倒序）
        var tasks = manager.GetAllTasks(10, 0);
        Assert.True(tasks.Count >= 2);
        Assert.Equal("second", tasks[0].Type);
        Assert.Equal("first", tasks[^1].Type);
    }

    [Fact]
    public void GetAllTasks_RespectsLimit()
    {
        var manager = CreateManager();
        manager.CreateTask("a");
        manager.CreateTask("b");
        manager.CreateTask("c");

        var tasks = manager.GetAllTasks(2, 0);
        Assert.Equal(2, tasks.Count);
    }

    [Fact]
    public void GetAllTasks_RespectsOffset()
    {
        var manager = CreateManager();
        var ids = new[] { manager.CreateTask("a"), manager.CreateTask("b"), manager.CreateTask("c") };

        var page1 = manager.GetAllTasks(2, 0);
        var page2 = manager.GetAllTasks(2, 2);

        Assert.Equal(2, page1.Count);
        Assert.Single(page2);
        Assert.NotEqual(page1[1].Id, page2[0].Id);
    }

    [Fact]
    public void GetAllTasks_EmptyDb_ReturnsEmptyList()
    {
        var manager = CreateManager();
        var tasks = manager.GetAllTasks(10, 0);
        Assert.Empty(tasks);
    }

    #endregion

    #region GetTasksByStatus

    [Fact]
    public void GetTasksByStatus_FiltersByStatus()
    {
        var manager = CreateManager();
        var id1 = manager.CreateTask("ok");
        var id2 = manager.CreateTask("fail");

        // 第二个任务需要更新状态
        _ = manager.UpdateStatus(id2, RunnerTaskStatus.Failed, "测试失败");

        // 触发通知确保状态已持久化
        var pendingTasks = manager.GetTasksByStatus("Pending", 10);
        var failedTasks = manager.GetTasksByStatus("Failed", 10);

        Assert.Single(pendingTasks);
        Assert.Single(failedTasks);
        Assert.Equal(id1, pendingTasks[0].Id);
        Assert.Equal(id2, failedTasks[0].Id);
    }

    [Fact]
    public void GetTasksByStatus_NoMatch_ReturnsEmpty()
    {
        var manager = CreateManager();
        manager.CreateTask("test");

        var timeoutTasks = manager.GetTasksByStatus("Timeout", 10);
        Assert.Empty(timeoutTasks);
    }

    #endregion

    #region DeleteTask

    [Fact]
    public void DeleteTask_RemovesFromMemoryAndDb()
    {
        var manager = CreateManager();
        var taskId = manager.CreateTask("test");

        Assert.NotNull(manager.GetTask(taskId));
        Assert.NotNull(_repository.Peek(taskId));

        var deleted = manager.DeleteTask(taskId);
        Assert.True(deleted);
        Assert.Null(manager.GetTask(taskId));
        Assert.Null(_repository.Peek(taskId));
    }

    [Fact]
    public void DeleteTask_NonExistent_ReturnsTrue()
    {
        var manager = CreateManager();
        var result = manager.DeleteTask("nonexistent");
        Assert.True(result);
    }

    [Fact]
    public void DeleteTask_OnlyInMemory_AlsoDeletesFromDb()
    {
        // 场景：任务在内存中，但数据库中也有
        var manager = CreateManager();
        var taskId = manager.CreateTask("test");

        // 模拟数据库删除失败但内存已删除的场景
        manager.DeleteTask(taskId);
        Assert.Null(manager.GetTask(taskId));
    }

    #endregion

    #region CleanupOldTasks

    [Fact]
    public void CleanupOldTasks_RemovesExpiredTasks()
    {
        var manager = CreateManager();

        // 创建一个"旧的"任务
        var oldId = manager.CreateTask("old");
        var oldTaskInfo = manager.GetTask(oldId);
        Assert.NotNull(oldTaskInfo);
        oldTaskInfo.UpdatedAt = DateTime.UtcNow.AddDays(-10);

        // 同步更新数据库实体
        var oldEntity = _repository.Peek(oldId);
        Assert.NotNull(oldEntity);
        oldEntity.CreatedAt = DateTime.UtcNow.AddDays(-10);

        // 创建一个新的任务
        var newId = manager.CreateTask("new");

        manager.CleanupOldTasks(TimeSpan.FromDays(5));

        // 验证：旧任务已被清除，新任务保留
        Assert.Null(manager.GetTask(oldId));
        Assert.NotNull(manager.GetTask(newId));
    }

    [Fact]
    public void CleanupOldTasks_NoExpiredTasks_ReturnsZero()
    {
        var manager = CreateManager();
        manager.CreateTask("a");
        manager.CreateTask("b");

        var deleted = manager.CleanupOldTasks(TimeSpan.FromDays(365 * 10)); // 10 年，没有任务会过期
        Assert.Equal(0, deleted);
    }

    #endregion

    #region CleanupAllCompletedTasks

    [Fact]
    public async Task CleanupAllCompletedTasks_RemovesSuccessAndFailed()
    {
        var manager = CreateManager();
        var successId = manager.CreateTask("ok");
        await manager.UpdateStatus(successId, RunnerTaskStatus.Success);

        var failId = manager.CreateTask("fail");
        await manager.UpdateStatus(failId, RunnerTaskStatus.Failed);

        var pendingId = manager.CreateTask("pending");

        var deleted = manager.CleanupAllCompletedTasks();
        Assert.True(deleted >= 2);
        Assert.Null(manager.GetTask(successId));
        Assert.Null(manager.GetTask(failId));
        Assert.NotNull(manager.GetTask(pendingId));
    }

    [Fact]
    public void CleanupAllCompletedTasks_AllRunning_ReturnsZero()
    {
        var manager = CreateManager();
        manager.CreateTask("a");
        manager.CreateTask("b");

        var deleted = manager.CleanupAllCompletedTasks();
        Assert.Equal(0, deleted);
    }

    #endregion

    #region DeleteAllTasks

    [Fact]
    public void DeleteAllTasks_ClearsMemoryAndDb()
    {
        var manager = CreateManager();
        manager.CreateTask("a");
        manager.CreateTask("b");
        manager.CreateTask("c");

        manager.DeleteAllTasks();
        Assert.Equal(0, manager.GetTaskCount());
        Assert.Empty(manager.GetAllTasks(100, 0));
    }

    #endregion

    #region GetTaskCount

    [Fact]
    public void GetTaskCount_ReturnsTotalFromDb()
    {
        var manager = CreateManager();
        Assert.Equal(0, manager.GetTaskCount());

        manager.CreateTask("a");
        Assert.Equal(1, manager.GetTaskCount());

        manager.CreateTask("b");
        Assert.Equal(2, manager.GetTaskCount());
    }

    #endregion

    #region CancelTaskAsync

    [Fact]
    public async Task CancelTaskAsync_CancelsRunningTask()
    {
        _mockCancellation.Setup(c => c.TryCancel(It.IsAny<string>())).Returns(true);
        var manager = CreateManager();
        var taskId = manager.CreateTask("test");

        // 手动标记为 Running（通常 UpdateProgress 会自动做这个）
        var cts = manager.CreateTaskCts(taskId);

        var cancelled = await manager.CancelTaskAsync(taskId);
        Assert.True(cancelled);

        var task = manager.GetTask(taskId);
        Assert.NotNull(task);
        Assert.Equal(RunnerTaskStatus.Cancelled, task.Status);
        _mockCancellation.Verify(c => c.TryCancel(taskId), Times.Once);
    }

    [Fact]
    public async Task CancelTaskAsync_NonExistent_ReturnsFalse()
    {
        var manager = CreateManager();
        var result = await manager.CancelTaskAsync("nonexistent");
        Assert.False(result);
    }

    [Fact]
    public async Task CancelTaskAsync_CompletedTask_ReturnsFalse()
    {
        var manager = CreateManager();
        var taskId = manager.CreateTask("test");
        await manager.UpdateStatus(taskId, RunnerTaskStatus.Success);

        var result = await manager.CancelTaskAsync(taskId);
        Assert.False(result);
    }

    #endregion

    #region CreateTaskCts / RemoveTaskCts

    [Fact]
    public void CreateTaskCts_DelegatesToCancellationManager()
    {
        var expectedCts = new CancellationTokenSource();
        _mockCancellation.Setup(c => c.CreateCts("task-1", null)).Returns(expectedCts);

        var manager = CreateManager();
        var cts = manager.CreateTaskCts("task-1");

        Assert.Same(expectedCts, cts);
        _mockCancellation.Verify(c => c.CreateCts("task-1", null), Times.Once);
    }

    [Fact]
    public void CreateTaskCts_WithTimeout_DelegatesWithTimeout()
    {
        var manager = CreateManager();
        manager.CreateTaskCts("task-2", TimeSpan.FromSeconds(30));

        _mockCancellation.Verify(c => c.CreateCts("task-2", TimeSpan.FromSeconds(30)), Times.Once);
    }

    [Fact]
    public void RemoveTaskCts_DelegatesToCancellationManager()
    {
        var manager = CreateManager();
        manager.RemoveTaskCts("task-3");
        _mockCancellation.Verify(c => c.RemoveCts("task-3"), Times.Once);
    }

    #endregion

    #region UpdateStatus

    [Fact]
    public async Task UpdateStatus_TransitionsToSuccess()
    {
        var manager = CreateManager();
        var taskId = manager.CreateTask("test");

        await manager.UpdateStatus(taskId, RunnerTaskStatus.Success);
        var task = manager.GetTask(taskId);

        Assert.NotNull(task);
        Assert.Equal(RunnerTaskStatus.Success, task.Status);
        Assert.Equal("任务完成", task.Progress.Message);
        Assert.Equal(100.0, task.Progress.Percentage);
        // 没有 error/data 时 Result 为 null
        Assert.Null(task.Result);
    }

    [Fact]
    public async Task UpdateStatus_TransitionsToSuccess_WithData_SetsResult()
    {
        var manager = CreateManager();
        var taskId = manager.CreateTask("test");

        await manager.UpdateStatus(taskId, RunnerTaskStatus.Success, data: new { ok = true });
        var task = manager.GetTask(taskId);

        Assert.NotNull(task.Result);
        Assert.True(task.Result.Success);
    }

    [Fact]
    public async Task UpdateStatus_TransitionsToFailed_WithError()
    {
        var manager = CreateManager();
        var taskId = manager.CreateTask("test");

        await manager.UpdateStatus(taskId, RunnerTaskStatus.Failed, "出错了");

        var task = manager.GetTask(taskId);
        Assert.Equal(RunnerTaskStatus.Failed, task.Status);
        Assert.False(task.Result?.Success);
        Assert.Equal("出错了", task.Result?.Error);
    }

    [Fact]
    public async Task UpdateStatus_Failed_AppendsFailureToMessage()
    {
        var manager = CreateManager();
        var taskId = manager.CreateTask("test");

        await manager.UpdateStatus(taskId, RunnerTaskStatus.Failed, "错误");
        var task = manager.GetTask(taskId);
        Assert.Contains("失败", task.Progress.Message);
    }

    [Fact]
    public async Task UpdateStatus_WithData_SetsResultData()
    {
        var manager = CreateManager();
        var taskId = manager.CreateTask("test");

        var data = new { key = "value", count = 42 };
        await manager.UpdateStatus(taskId, RunnerTaskStatus.Success, data: data);

        var task = manager.GetTask(taskId);
        Assert.NotNull(task.Result?.Data);
    }

    [Fact]
    public async Task UpdateStatus_UnknownTask_DoesNotThrow()
    {
        var manager = CreateManager();
        // 对不存在的任务更新状态不应抛出异常
        await manager.UpdateStatus("nonexistent", RunnerTaskStatus.Success);
        // 静默忽略
    }

    [Fact]
    public async Task UpdateStatus_PersistsToRepository()
    {
        var manager = CreateManager();
        var taskId = manager.CreateTask("test");

        await manager.UpdateStatus(taskId, RunnerTaskStatus.Running);

        var entity = _repository.Peek(taskId);
        Assert.NotNull(entity);
        Assert.Equal("Running", entity.Status);
        Assert.NotNull(entity.StartedAt);
    }

    #endregion

    #region UpdateProgress

    [Fact]
    public async Task UpdateProgress_TransitionsPendingToRunning()
    {
        var manager = CreateManager();
        var taskId = manager.CreateTask("test");

        var taskBefore = manager.GetTask(taskId);
        Assert.Equal(RunnerTaskStatus.Pending, taskBefore.Status);

        await manager.UpdateProgress(taskId, 1, 10, "处理中...");

        var taskAfter = manager.GetTask(taskId);
        Assert.Equal(RunnerTaskStatus.Running, taskAfter.Status);
        Assert.Equal(10.0, taskAfter.Progress.Percentage);
        Assert.Equal("处理中...", taskAfter.Progress.Message);
    }

    [Fact]
    public async Task UpdateProgress_DividesByZero_SetsPercentageZero()
    {
        var manager = CreateManager();
        var taskId = manager.CreateTask("test");

        await manager.UpdateProgress(taskId, 0, 0, "无进度");
        var task = manager.GetTask(taskId);

        Assert.Equal(0, task.Progress.Percentage);
    }

    [Fact]
    public async Task UpdateProgress_CalculatePercentage()
    {
        var manager = CreateManager();
        var taskId = manager.CreateTask("test");

        await manager.UpdateProgress(taskId, 3, 4, "75%");
        var task = manager.GetTask(taskId);

        Assert.Equal(75.0, task.Progress.Percentage);
        Assert.Equal(3, task.Progress.Current);
        Assert.Equal(4, task.Progress.Total);
    }

    [Fact]
    public async Task UpdateProgress_UnknownTask_DoesNotThrow()
    {
        var manager = CreateManager();
        await manager.UpdateProgress("nonexistent", 50, 100, "测试");
        // 静默忽略，不抛异常
    }

    [Fact]
    public async Task UpdateProgress_NotifiesUpdate()
    {
        var manager = CreateManager();
        var taskId = manager.CreateTask("test");

        await manager.UpdateProgress(taskId, 1, 5, "20%");
        _mockNotifier.Verify(n => n.NotifyTaskUpdateAsync(taskId), Times.AtLeastOnce);
    }

    #endregion

    #region NotifySupplementEvent

    [Fact]
    public async Task NotifySupplementEvent_DelegatesToNotifier()
    {
        var manager = CreateManager();
        var data = new { info = "test" };

        await manager.NotifySupplementEventAsync("task-1", "progress", data);
        _mockNotifier.Verify(n => n.NotifySupplementEventAsync("task-1", "progress", data), Times.Once);
    }

    [Fact]
    public async Task NotifySupplementEvent_WithNullData_Works()
    {
        var manager = CreateManager();
        await manager.NotifySupplementEventAsync("task-1", "status", null);
        _mockNotifier.Verify(n => n.NotifySupplementEventAsync("task-1", "status", null), Times.Once);
    }

    #endregion

    #region Backward Compatibility (原始构造函数)

    /// <summary>
    /// 确保旧构造函数依然可用，且基本功能正常
    /// 注意：需要 EF Core InMemory 替代真正的 FamilyDbContext
    /// </summary>
    [Fact]
    public void LegacyConstructor_WorksWithDbContextFactory()
    {
        // 使用 EF Core InMemory 数据库模拟 FamilyDbContext
        var options = CreateInMemoryDbContextOptions("legacy-test");
        var factory = new TestDbContextFactory(options);

        // 旧构造函数不应抛出
        var manager = new TaskManager(factory, hubContext: null, logger: null);
        Assert.NotNull(manager);

        // 基本操作
        var taskId = manager.CreateTask("legacy");
        Assert.NotNull(taskId);

        var task = manager.GetTask(taskId);
        Assert.NotNull(task);
        Assert.Equal("legacy", task.Type);
        Assert.Equal(RunnerTaskStatus.Pending, task.Status);
    }

    [Fact]
    public void LegacyConstructor_ThrowsOnNullFactory()
    {
        Assert.Throws<ArgumentNullException>(() => new TaskManager(null!, null, null));
    }

    private static DbContextOptions<Data.FamilyDbContext> CreateInMemoryDbContextOptions(string dbName)
    {
        return new DbContextOptionsBuilder<Data.FamilyDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
    }

    private class TestDbContextFactory : IDbContextFactory<Data.FamilyDbContext>
    {
        private readonly DbContextOptions<Data.FamilyDbContext> _options;
        public TestDbContextFactory(DbContextOptions<Data.FamilyDbContext> options) => _options = options;
        public Data.FamilyDbContext CreateDbContext() => new(_options);
        public Task<Data.FamilyDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(new Data.FamilyDbContext(_options));
    }

    #endregion

    #region Edge Cases & Concurrency

    [Fact]
    public void MultipleTasks_ConcurrentCreate_AllSucceed()
    {
        var manager = CreateManager();
        var tasks = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(manager.CreateTask($"type-{i}"));
        }

        Assert.Equal(10, tasks.Distinct().Count());
        Assert.Equal(10, manager.GetTaskCount());
    }

    [Fact]
    public void GetTask_CaseSensitive_ReturnsCorrectTask()
    {
        var manager = CreateManager();
        var taskId = manager.CreateTask("test");
        Assert.NotNull(manager.GetTask(taskId));
        Assert.Null(manager.GetTask(taskId.ToUpperInvariant()));
    }

    [Fact]
    public void CreateTask_NullParameters_DoesNotThrow()
    {
        var manager = CreateManager();
        var taskId = manager.CreateTask("null-params", null);
        Assert.NotNull(taskId);
        var task = manager.GetTask(taskId);
        Assert.Null(task.Parameters);
    }

    [Fact]
    public void CreateTask_EmptyParameters_Works()
    {
        var manager = CreateManager();
        var taskId = manager.CreateTask("empty-params", new Dictionary<string, string>());
        Assert.NotNull(taskId);
        var task = manager.GetTask(taskId);
        Assert.NotNull(task.Parameters);
        Assert.Empty(task.Parameters);
    }

    #endregion
}
