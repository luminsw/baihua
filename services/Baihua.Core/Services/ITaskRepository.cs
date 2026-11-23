using Baihua.Core;
using Baihua.Data.Entities;

namespace Baihua.Core.Services;

/// <summary>
/// 任务持久化仓库接口——从 TaskManager 中提取的数据库关注点
/// 负责 TaskEntity 的 CRUD，与内存缓存无关，与 SignalR 推送无关
/// </summary>
public interface ITaskRepository
{
    /// <summary>保存新任务到数据库</summary>
    void CreateTask(TaskEntity entity);

    /// <summary>按 TaskId 加载任务实体（不含内存缓存）</summary>
    TaskEntity? GetTaskById(string taskId);

    /// <summary>查询任务列表（分页）</summary>
    List<TaskEntity> GetAllTasks(int limit, int offset);

    /// <summary>按状态查询</summary>
    List<TaskEntity> GetTasksByStatus(string status, int limit);

    /// <summary>删除指定任务</summary>
    bool DeleteTask(string taskId);

    /// <summary>删除符合条件的旧任务</summary>
    int DeleteOldTasks(DateTime cutoffDate);

    /// <summary>删除所有已完成的任务</summary>
    int DeleteCompletedTasks();

    /// <summary>删除所有任务</summary>
    int DeleteAllTasks();

    /// <summary>获取任务总数</summary>
    int GetTaskCount();

    /// <summary>更新任务状态字段</summary>
    void UpdateStatus(string taskId, string status, string? error, string? output, int progress, string? progressMessage, DateTime? startedAt, DateTime? completedAt);

    /// <summary>更新任务进度字段</summary>
    void UpdateProgress(string taskId, int progress, string? progressMessage, DateTime? startedAt);
}
