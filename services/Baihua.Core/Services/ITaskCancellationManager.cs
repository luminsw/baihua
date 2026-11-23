namespace Baihua.Core.Services;

/// <summary>
/// 任务取消令牌管理——从 TaskManager 中提取的 CancellationTokenSource 生命周期关注点
/// </summary>
public interface ITaskCancellationManager
{
    /// <summary>为指定任务创建 CancellationTokenSource（可选超时）</summary>
    CancellationTokenSource CreateCts(string taskId, TimeSpan? timeout = null);

    /// <summary>移除并释放任务的 CancellationTokenSource</summary>
    void RemoveCts(string taskId);

    /// <summary>尝试取消指定任务（返回是否成功发起取消）</summary>
    bool TryCancel(string taskId);

    /// <summary>检查是否有活跃的 CTS（用于测试/断线重连场景）</summary>
    bool HasActiveCts(string taskId);
}
