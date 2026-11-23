using System.Collections.Concurrent;

namespace Baihua.Core.Services;

/// <summary>
/// 任务 CancellationTokenSource 管理器
/// 使用 ConcurrentDictionary 跟踪所有活跃任务的令牌
/// </summary>
public class TaskCancellationManager : ITaskCancellationManager
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningCts = new();

    public CancellationTokenSource CreateCts(string taskId, TimeSpan? timeout = null)
    {
        var cts = timeout.HasValue
            ? new CancellationTokenSource(timeout.Value)
            : new CancellationTokenSource();
        _runningCts[taskId] = cts;
        return cts;
    }

    public void RemoveCts(string taskId)
    {
        if (_runningCts.TryRemove(taskId, out var cts))
        {
            try { cts.Dispose(); } catch { /* 已释放或已取消，无需处理 */ }
        }
    }

    public bool TryCancel(string taskId)
    {
        if (_runningCts.TryGetValue(taskId, out var cts))
        {
            try
            {
                cts.Cancel();
                return true;
            }
            catch
            {
                /* 已取消或已释放，无需处理 */
            }
        }
        return false;
    }

    public bool HasActiveCts(string taskId)
    {
        return _runningCts.ContainsKey(taskId);
    }

    /// <summary>取消所有活跃的 CTS（用于应用关闭场景）</summary>
    public void CancelAll()
    {
        foreach (var kvp in _runningCts.ToArray())
        {
            TryCancel(kvp.Key);
        }
    }

    /// <summary>清空所有 CTS</summary>
    public void Clear()
    {
        foreach (var kvp in _runningCts.ToArray())
        {
            RemoveCts(kvp.Key);
        }
    }
}
