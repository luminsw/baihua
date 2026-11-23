using System.Collections.Concurrent;
using System.Diagnostics;

namespace Baihua.Web.Services;

/// <summary>
/// 端到端性能监控服务
/// 追踪从用户操作 → API 调用 → 页面渲染完成的完整链路
/// </summary>
public class EndToEndPerformanceService
{
    // 活跃的追踪会话（按操作ID）
    private readonly ConcurrentDictionary<string, E2EPerformanceTrace> _activeTraces = new();
    
    // 已完成的追踪记录（保留最近 500 条）
    private readonly ConcurrentQueue<E2EPerformanceRecord> _completedRecords = new();
    private const int MaxRecordsCount = 500;
    
    private readonly ILogger<EndToEndPerformanceService> _logger;
    
    public EndToEndPerformanceService(ILogger<EndToEndPerformanceService> logger)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// 开始一个新的端到端追踪
    /// </summary>
    public string StartTrace(string operationName, string? pageName = null)
    {
        var traceId = $"{operationName}_{Guid.NewGuid():N}";
        var trace = new E2EPerformanceTrace
        {
            TraceId = traceId,
            OperationName = operationName,
            PageName = pageName ?? operationName,
            StartTime = DateTime.UtcNow,
            StartTimestamp = Stopwatch.GetTimestamp()
        };
        _activeTraces[traceId] = trace;
        _logger.LogDebug("[E2E-Trace-{TraceId}] 开始追踪: {Operation}", traceId, operationName);
        return traceId;
    }

    /// <summary>
    /// 记录 API 调用开始
    /// </summary>
    public void RecordApiCallStart(string traceId, string endpoint, string method)
    {
        if (_activeTraces.TryGetValue(traceId, out var trace))
        {
            var apiCall = new ApiCallTiming
            {
                Endpoint = endpoint,
                Method = method,
                StartTimestamp = Stopwatch.GetTimestamp()
            };
            trace.ApiCalls.Add(apiCall);
        }
    }
    
    /// <summary>
    /// 记录 API 调用结束
    /// </summary>
    public void RecordApiCallEnd(string traceId, string endpoint, long elapsedMs, bool success)
    {
        if (_activeTraces.TryGetValue(traceId, out var trace))
        {
            var apiCall = trace.ApiCalls.LastOrDefault(c => c.Endpoint == endpoint && c.EndTimestamp == 0);
            if (apiCall != null)
            {
                apiCall.EndTimestamp = Stopwatch.GetTimestamp();
                apiCall.ElapsedMilliseconds = elapsedMs;
                apiCall.Success = success;
            }
        }
    }
    
    /// <summary>
    /// 记录组件渲染完成
    /// </summary>
    public void RecordComponentRender(string traceId, string componentName, long renderTimeMs)
    {
        if (_activeTraces.TryGetValue(traceId, out var trace))
        {
            trace.ComponentRenders.Add(new ComponentRenderTiming
            {
                ComponentName = componentName,
                RenderTimeMs = renderTimeMs,
                Timestamp = Stopwatch.GetTimestamp()
            });
        }
    }
    
    public void EndTrace(string traceId)
    {
        if (!_activeTraces.TryRemove(traceId, out var trace))
            return;
        
        var endTimestamp = Stopwatch.GetTimestamp();
        var totalElapsedMs = GetElapsedMs(trace.StartTimestamp, endTimestamp);
        
        var record = new E2EPerformanceRecord
        {
            TraceId = trace.TraceId,
            OperationName = trace.OperationName,
            PageName = trace.PageName,
            StartTime = trace.StartTime,
            TotalElapsedMs = totalElapsedMs,
            NetworkTimeMs = trace.ApiCalls.Sum(c => c.ElapsedMilliseconds),
            ApiCallCount = trace.ApiCalls.Count,
            RenderingTimeMs = trace.ComponentRenders.Sum(r => r.RenderTimeMs),
            ComponentRenderCount = trace.ComponentRenders.Count,
            SlowestComponent = trace.ComponentRenders.OrderByDescending(r => r.RenderTimeMs).FirstOrDefault()?.ComponentName ?? "N/A",
            SlowestApiCall = trace.ApiCalls.OrderByDescending(c => c.ElapsedMilliseconds).FirstOrDefault(),
            FirstRenderDelayMs = trace.ComponentRenders.Any() 
                ? GetElapsedMs(trace.StartTimestamp, trace.ComponentRenders.Min(r => r.Timestamp))
                : 0,
            ApiCalls = trace.ApiCalls.ToList(),
            ComponentRenders = trace.ComponentRenders.ToList()
        };
        
        _completedRecords.Enqueue(record);
        
        // 限制记录数量
        while (_completedRecords.Count > MaxRecordsCount && _completedRecords.TryDequeue(out _))
        {
        }
        
        // 记录慢操作警告
        if (totalElapsedMs > 1000)
        {
            _logger.LogWarning("[E2E-Trace-{TraceId}] 慢操作: {Operation} 总耗时 {TotalMs}ms " +
                "(Network: {NetworkMs}ms, Render: {RenderMs}ms)",
                traceId, trace.OperationName, totalElapsedMs, 
                record.NetworkTimeMs, record.RenderingTimeMs);
        }
        else
        {
            _logger.LogDebug("[E2E-Trace-{TraceId}] 完成: {Operation} 总耗时 {TotalMs}ms " +
                "(Network: {NetworkMs}ms, Render: {RenderMs}ms)",
                traceId, trace.OperationName, totalElapsedMs,
                record.NetworkTimeMs, record.RenderingTimeMs);
        }
    }
    
    /// <summary>
    /// 获取所有已完成的追踪记录
    /// </summary>
    public IReadOnlyList<E2EPerformanceRecord> GetRecords()
    {
        return _completedRecords.ToList();
    }
    
    /// <summary>
    /// 获取最慢的操作记录
    /// </summary>
    public IReadOnlyList<E2EPerformanceRecord> GetSlowestOperations(int count = 10)
    {
        return _completedRecords
            .OrderByDescending(r => r.TotalElapsedMs)
            .Take(count)
            .ToList();
    }
    
    /// <summary>
    /// 获取统计摘要
    /// </summary>
    public E2EPerformanceSummary GetSummary()
    {
        var records = _completedRecords.ToList();
        if (!records.Any())
            return new E2EPerformanceSummary();
        
        return new E2EPerformanceSummary
        {
            TotalOperations = records.Count,
            AvgTotalTimeMs = (long)records.Average(r => r.TotalElapsedMs),
            MaxTotalTimeMs = records.Max(r => r.TotalElapsedMs),
            AvgNetworkTimeMs = (long)records.Average(r => r.NetworkTimeMs),
            AvgRenderingTimeMs = (long)records.Average(r => r.RenderingTimeMs),
            SlowOperationCount = records.Count(r => r.TotalElapsedMs > 1000),
            OperationsByPage = records
                .GroupBy(r => r.PageName)
                .Select(g => new PageOperationStats
                {
                    PageName = g.Key,
                    OperationCount = g.Count(),
                    AvgTimeMs = (long)g.Average(r => r.TotalElapsedMs)
                })
                .ToList()
        };
    }
    
    /// <summary>
    /// 清空所有记录
    /// </summary>
    public void Clear()
    {
        _completedRecords.Clear();
        _activeTraces.Clear();
    }
    
    private static long GetElapsedMs(long startTimestamp, long endTimestamp)
    {
        return (long)((endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency);
    }
}

/// <summary>
/// 端到端性能追踪（进行中）
/// </summary>
public class E2EPerformanceTrace
{
    public string TraceId { get; set; } = "";
    public string OperationName { get; set; } = "";
    public string PageName { get; set; } = "";
    public DateTime StartTime { get; set; }
    public long StartTimestamp { get; set; }
    public long DataReceivedTimestamp { get; set; }
    public List<ApiCallTiming> ApiCalls { get; set; } = new();
    public List<ComponentRenderTiming> ComponentRenders { get; set; } = new();
}

/// <summary>
/// API 调用时间记录
/// </summary>
public class ApiCallTiming
{
    public string Endpoint { get; set; } = "";
    public string Method { get; set; } = "";
    public long StartTimestamp { get; set; }
    public long EndTimestamp { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public bool Success { get; set; }
}

/// <summary>
/// 组件渲染时间记录
/// </summary>
public class ComponentRenderTiming
{
    public string ComponentName { get; set; } = "";
    public long RenderTimeMs { get; set; }
    public long Timestamp { get; set; }
}

/// <summary>
/// 端到端性能记录（已完成）
/// </summary>
public class E2EPerformanceRecord
{
    public string TraceId { get; set; } = "";
    public string OperationName { get; set; } = "";
    public string PageName { get; set; } = "";
    public DateTime StartTime { get; set; }
    
    // 总耗时
    public long TotalElapsedMs { get; set; }
    
    // 网络耗时（所有 API 调用之和）
    public long NetworkTimeMs { get; set; }
    public int ApiCallCount { get; set; }
    
    // 渲染耗时（所有组件渲染之和）
    public long RenderingTimeMs { get; set; }
    public int ComponentRenderCount { get; set; }
    
    // 关键指标
    public string SlowestComponent { get; set; } = "";
    public ApiCallTiming? SlowestApiCall { get; set; }
    public long FirstRenderDelayMs { get; set; }
    
    // 详细记录
    public List<ApiCallTiming> ApiCalls { get; set; } = new();
    public List<ComponentRenderTiming> ComponentRenders { get; set; } = new();
}

/// <summary>
/// 端到端性能统计摘要
/// </summary>
public class E2EPerformanceSummary
{
    public int TotalOperations { get; set; }
    public long AvgTotalTimeMs { get; set; }
    public long MaxTotalTimeMs { get; set; }
    public long AvgNetworkTimeMs { get; set; }
    public long AvgRenderingTimeMs { get; set; }
    public int SlowOperationCount { get; set; }
    public List<PageOperationStats> OperationsByPage { get; set; } = new();
}

public class PageOperationStats
{
    public string PageName { get; set; } = "";
    public int OperationCount { get; set; }
    public long AvgTimeMs { get; set; }
}
