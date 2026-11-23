using System.Collections.Concurrent;

namespace Baihua.Web.Services;

/// <summary>
/// 错误日志条目
/// </summary>
public class ErrorLogEntry
{
    public DateTime Timestamp { get; set; }
    public string Category { get; set; } = "";
    public LogLevel Level { get; set; }
    public string Message { get; set; } = "";
    public string? Exception { get; set; }
    public string? StackTrace { get; set; }
}

/// <summary>
/// 错误日志服务：内存中保留最近的错误日志
/// </summary>
public class ErrorLogService
{
    // 保留最近的 500 条错误日志（避免内存无限增长）
    private readonly ConcurrentQueue<ErrorLogEntry> _errors = new();
    private const int MaxErrorCount = 500;

    /// <summary>
    /// 记录一条错误日志
    /// </summary>
    public void LogError(ErrorLogEntry entry)
    {
        _errors.Enqueue(entry);

        // 限制队列大小
        while (_errors.Count > MaxErrorCount && _errors.TryDequeue(out _))
        {
            // 移除最老的记录
        }
    }

    /// <summary>
    /// 获取最近的错误日志
    /// </summary>
    public IReadOnlyList<ErrorLogEntry> GetRecentErrors(int count = 10)
    {
        return _errors
            .OrderByDescending(e => e.Timestamp)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// 获取总体统计
    /// </summary>
    public ErrorLogSummary GetSummary()
    {
        var errors = _errors.ToList();
        return new ErrorLogSummary
        {
            TotalErrors = errors.Count,
            CriticalCount = errors.Count(e => e.Level == LogLevel.Critical),
            ErrorCount = errors.Count(e => e.Level == LogLevel.Error),
            WarningCount = errors.Count(e => e.Level == LogLevel.Warning),
            RecentErrorTime = errors.Any() ? errors.Max(e => e.Timestamp) : (DateTime?)null
        };
    }

    /// <summary>
    /// 清空错误日志
    /// </summary>
    public void Clear()
    {
        _errors.Clear();
    }
}

/// <summary>
/// 错误日志统计摘要
/// </summary>
public class ErrorLogSummary
{
    public int TotalErrors { get; set; }
    public int CriticalCount { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public DateTime? RecentErrorTime { get; set; }
}
