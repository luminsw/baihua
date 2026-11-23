using System.Text.Json;
using Baihua.Core;
using Baihua.Data.Entities;

namespace Baihua.Core.Services;

/// <summary>
/// 任务实体映射器——从 TaskManager 中提取的 TaskEntity→TaskInfo 转换关注点
/// </summary>
public static class TaskEntityMapper
{
    /// <summary>
    /// 将数据库实体 TaskEntity 映射为领域模型 TaskInfo
    /// </summary>
    public static TaskInfo MapFromEntity(TaskEntity entity)
    {
        Dictionary<string, string>? parameters = null;
        if (!string.IsNullOrEmpty(entity.Input))
        {
            try { parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(entity.Input); } catch { /* 反序列化失败时返回 null */ }
        }

        object? resultData = null;
        if (!string.IsNullOrEmpty(entity.Output))
        {
            try { resultData = JsonSerializer.Deserialize<object>(entity.Output); } catch { /* 反序列化失败时返回 null */ }
        }

        return new TaskInfo
        {
            Id = entity.TaskId,
            Type = entity.TaskType,
            Status = Enum.Parse<RunnerTaskStatus>(entity.Status),
            Parameters = parameters,
            Progress = new TaskProgress
            {
                Current = entity.Progress,
                Total = 100,
                Message = entity.ProgressMessage ?? "",
                Percentage = entity.Progress
            },
            Result = !string.IsNullOrEmpty(entity.Output) || !string.IsNullOrEmpty(entity.Error)
                ? new TaskResult
                {
                    Success = entity.Status == "Success",
                    Error = entity.Error,
                    Data = resultData
                }
                : null,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };
    }

    /// <summary>
    /// 为 TaskInfo 创建对应的 TaskEntity（用于持久化）
    /// </summary>
    public static TaskEntity ToEntity(TaskInfo task)
    {
        return new TaskEntity
        {
            TaskId = task.Id,
            TaskType = task.Type,
            Status = task.Status.ToString(),
            Input = task.Parameters != null ? JsonSerializer.Serialize(task.Parameters) : null,
            Output = task.Result?.Data != null ? JsonSerializer.Serialize(task.Result.Data) : null,
            Error = task.Result?.Error,
            Progress = (int)task.Progress.Percentage,
            ProgressMessage = task.Progress.Message,
            CreatedAt = task.CreatedAt,
            UpdatedAt = task.UpdatedAt
        };
    }
}
