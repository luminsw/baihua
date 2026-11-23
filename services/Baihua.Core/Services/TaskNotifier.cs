using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Baihua.Core;
using Baihua.Core.Hubs;

namespace Baihua.Core.Services;

/// <summary>
/// 通过 SignalR Hub 推送任务状态/进度更新
/// </summary>
public class TaskNotifier : ITaskNotifier
{
    private readonly IHubContext<TaskProgressHub>? _hubContext;
    private readonly ILogger<TaskNotifier>? _logger;
    private readonly Lazy<TaskManager>? _taskManagerAccessor;

    /// <summary>
    /// 构造通知器
    /// </summary>
    /// <param name="hubContext">SignalR Hub 上下文（可为 null）</param>
    /// <param name="logger">日志记录器（可为 null）</param>
    /// <param name="taskManagerAccessor">延迟获取 TaskManager 实例的访问器（用于读取最新任务状态）</param>
    public TaskNotifier(
        IHubContext<TaskProgressHub>? hubContext = null,
        ILogger<TaskNotifier>? logger = null,
        Lazy<TaskManager>? taskManagerAccessor = null)
    {
        _hubContext = hubContext;
        _logger = logger;
        _taskManagerAccessor = taskManagerAccessor;
    }

    public async Task NotifySupplementEventAsync(string taskId, string eventName, object? data = null)
    {
        if (_hubContext == null) return;
        try
        {
            await _hubContext.Clients.All.SendAsync("SupplementEvent", new
            {
                TaskId = taskId,
                Event = eventName,
                Data = data
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("推送 SupplementEvent 失败：{Message}", ex.Message);
        }
    }

    public async Task NotifyTaskUpdateAsync(string taskId)
    {
        if (_hubContext == null) return;
        try
        {
            if (_taskManagerAccessor?.Value != null)
            {
                var task = _taskManagerAccessor.Value.GetTask(taskId);
                if (task != null)
                {
                    await _hubContext.Clients.All.SendAsync("TaskUpdated", task);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("推送任务更新失败：{Message}", ex.Message);
        }
    }
}
