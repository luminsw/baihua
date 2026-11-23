using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Baihua.Core;
using Baihua.Data;
using Baihua.Data.Entities;

namespace Baihua.Core.Services;

/// <summary>
/// 基于 EF Core + SQLite 的任务持久化实现
/// 每个方法都创建独立的 DbContext 生命周期（与原始 TaskManager 行为一致）
/// </summary>
public class TaskRepository : ITaskRepository
{
    private readonly IDbContextFactory<FamilyDbContext> _dbContextFactory;
    private readonly ILogger<TaskRepository>? _logger;

    public TaskRepository(IDbContextFactory<FamilyDbContext> dbContextFactory, ILogger<TaskRepository>? logger = null)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public void CreateTask(TaskEntity entity)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        try
        {
            dbContext.Tasks.Add(entity);
            dbContext.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "保存任务到数据库失败: {TaskId}", entity.TaskId);
        }
    }

    public TaskEntity? GetTaskById(string taskId)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        return dbContext.Tasks.FirstOrDefault(t => t.TaskId == taskId);
    }

    public List<TaskEntity> GetAllTasks(int limit = 100, int offset = 0)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        return dbContext.Tasks
            .OrderByDescending(t => t.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToList();
    }

    public List<TaskEntity> GetTasksByStatus(string status, int limit = 100)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        return dbContext.Tasks
            .Where(t => t.Status == status)
            .OrderByDescending(t => t.CreatedAt)
            .Take(limit)
            .ToList();
    }

    public bool DeleteTask(string taskId)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        try
        {
            var task = dbContext.Tasks.FirstOrDefault(t => t.TaskId == taskId);
            if (task != null)
            {
                dbContext.Tasks.Remove(task);
                dbContext.SaveChanges();
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "删除任务失败: {TaskId}", taskId);
            return false;
        }
    }

    public int DeleteOldTasks(DateTime cutoffDate)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        try
        {
            var oldTasks = dbContext.Tasks
                .Where(t => t.CreatedAt < cutoffDate)
                .ToList();
            var count = oldTasks.Count;
            dbContext.Tasks.RemoveRange(oldTasks);
            dbContext.SaveChanges();
            return count;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "清理旧任务失败");
            return 0;
        }
    }

    public int DeleteCompletedTasks()
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        try
        {
            var completedTasks = dbContext.Tasks
                .Where(t => t.Status == "Success" || t.Status == "Failed" || t.Status == "Cancelled")
                .ToList();
            var count = completedTasks.Count;
            dbContext.Tasks.RemoveRange(completedTasks);
            dbContext.SaveChanges();
            return count;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "清理已完成任务失败");
            return 0;
        }
    }

    public int DeleteAllTasks()
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        try
        {
            var allTasks = dbContext.Tasks.ToList();
            dbContext.Tasks.RemoveRange(allTasks);
            dbContext.SaveChanges();
            return 1;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "清空所有任务失败");
            return 0;
        }
    }

    public int GetTaskCount()
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        return dbContext.Tasks.Count();
    }

    public void UpdateStatus(string taskId, string status, string? error, string? output,
        int progress, string? progressMessage, DateTime? startedAt, DateTime? completedAt)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        try
        {
            var dbTask = dbContext.Tasks.FirstOrDefault(t => t.TaskId == taskId);
            if (dbTask == null) return;

            dbTask.Status = status;
            dbTask.Error = error;
            dbTask.Output = output;
            dbTask.Progress = progress;
            dbTask.ProgressMessage = progressMessage;
            if (startedAt.HasValue) dbTask.StartedAt = startedAt;
            if (completedAt.HasValue) dbTask.CompletedAt = completedAt;
            dbContext.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "更新任务状态到数据库失败: {TaskId}", taskId);
        }
    }

    public void UpdateProgress(string taskId, int progress, string? progressMessage, DateTime? startedAt)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        try
        {
            var dbTask = dbContext.Tasks.FirstOrDefault(t => t.TaskId == taskId);
            if (dbTask == null) return;

            dbTask.Progress = progress;
            dbTask.ProgressMessage = progressMessage;
            if (startedAt.HasValue)
            {
                dbTask.StartedAt = startedAt;
                dbTask.Status = "Running";
            }
            dbContext.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "更新任务进度到数据库失败: {TaskId}", taskId);
        }
    }
}
