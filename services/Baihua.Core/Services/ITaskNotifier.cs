namespace Baihua.Core.Services;

/// <summary>
/// 任务更新通知接口——从 TaskManager 中提取的 SignalR 推送关注点
/// </summary>
public interface ITaskNotifier
{
    /// <summary>推送 SupplementEvent 给所有连接客户端</summary>
    Task NotifySupplementEventAsync(string taskId, string eventName, object? data = null);

    /// <summary>推送 TaskUpdated 事件给所有连接客户端</summary>
    Task NotifyTaskUpdateAsync(string taskId);
}
