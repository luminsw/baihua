using System.Collections.Concurrent;
using System.Diagnostics;

namespace Baihua.Web.Services;

/// <summary>
/// WebUI → Baihua API 调用性能统计服务
/// </summary>
public class ApiCallMetricsService
{
    // 保留最近的 1000 条调用记录
    private readonly ConcurrentQueue<ApiCallMetric> _metrics = new();
    private const int MaxMetricsCount = 1000;

    /// <summary>
    /// 记录一次 API 调用
    /// </summary>
    public void RecordCall(string endpoint, string method, long elapsedMs, bool success, int? statusCode = null, string? error = null)
    {
        var metric = new ApiCallMetric
        {
            Timestamp = DateTime.UtcNow,
            Endpoint = endpoint,
            Method = method,
            ElapsedMilliseconds = elapsedMs,
            Success = success,
            StatusCode = statusCode,
            Error = error
        };

        _metrics.Enqueue(metric);

        // 限制队列大小
        while (_metrics.Count > MaxMetricsCount && _metrics.TryDequeue(out _))
        {
        }
    }

    /// <summary>
    /// 获取总体统计摘要
    /// </summary>
    public ApiCallMetricsSummary GetSummary()
    {
        var metrics = _metrics.ToList();
        if (metrics.Count == 0)
        {
            return new ApiCallMetricsSummary { TotalCalls = 0 };
        }

        var successMetrics = metrics.Where(m => m.Success).ToList();
        
        return new ApiCallMetricsSummary
        {
            TotalCalls = metrics.Count,
            SuccessCount = successMetrics.Count,
            ErrorCount = metrics.Count - successMetrics.Count,
            AvgElapsedMs = successMetrics.Any() ? (long)successMetrics.Average(m => m.ElapsedMilliseconds) : 0,
            MaxElapsedMs = successMetrics.Any() ? successMetrics.Max(m => m.ElapsedMilliseconds) : 0,
            MinElapsedMs = successMetrics.Any() ? successMetrics.Min(m => m.ElapsedMilliseconds) : 0,
            UniqueEndpoints = metrics.Select(m => $"{m.Method} {m.Endpoint}").Distinct().Count()
        };
    }

    /// <summary>
    /// 获取最慢的 API 调用
    /// </summary>
    public IReadOnlyList<ApiCallMetric> GetSlowestCalls(int count = 10)
    {
        return _metrics
            .Where(m => m.Success)
            .OrderByDescending(m => m.ElapsedMilliseconds)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// 获取最近的错误调用
    /// </summary>
    public IReadOnlyList<ApiCallMetric> GetRecentErrors(int count = 10)
    {
        return _metrics
            .Where(m => !m.Success)
            .OrderByDescending(m => m.Timestamp)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// 清空统计
    /// </summary>
    public void Clear()
    {
        _metrics.Clear();
    }

    /// <summary>
    /// 获取按端点分组的统计
    /// </summary>
    public IReadOnlyList<EndpointStats> GetEndpointStats()
    {
        return _metrics
            .GroupBy(m => $"{m.Method} {m.Endpoint}")
            .Select(g => new EndpointStats
            {
                Endpoint = g.First().Endpoint,
                Method = g.First().Method,
                CallCount = g.Count(),
                SuccessRate = g.Any() ? (double)g.Count(m => m.Success) / g.Count() * 100 : 0,
                AvgElapsedMs = (long)(g.Where(m => m.Success).Average(m => (double?)m.ElapsedMilliseconds) ?? 0),
                MaxElapsedMs = g.Where(m => m.Success).Max(m => (long?)m.ElapsedMilliseconds) ?? 0,
                P50ElapsedMs = CalculatePercentile(g.Where(m => m.Success).Select(m => m.ElapsedMilliseconds).ToList(), 50),
                P95ElapsedMs = CalculatePercentile(g.Where(m => m.Success).Select(m => m.ElapsedMilliseconds).ToList(), 95),
                P99ElapsedMs = CalculatePercentile(g.Where(m => m.Success).Select(m => m.ElapsedMilliseconds).ToList(), 99),
            })
            .OrderByDescending(s => s.CallCount)
            .ToList();
    }

    private static long CalculatePercentile(List<long> values, int percentile)
    {
        if (values == null || values.Count == 0) return 0;
        
        var sorted = values.OrderBy(v => v).ToList();
        var index = (int)Math.Ceiling(sorted.Count * percentile / 100.0) - 1;
        index = Math.Max(0, Math.Min(index, sorted.Count - 1));
        return sorted[index];
    }
}

/// <summary>
/// 单次 API 调用指标
/// </summary>
public class ApiCallMetric
{
    public DateTime Timestamp { get; set; }
    public string Endpoint { get; set; } = "";
    public string Method { get; set; } = "";
    public long ElapsedMilliseconds { get; set; }
    public bool Success { get; set; }
    public int? StatusCode { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// 端点详细统计
/// </summary>
public class EndpointStats
{
    public string Endpoint { get; set; } = "";
    public string Method { get; set; } = "";
    public int CallCount { get; set; }
    public double SuccessRate { get; set; }
    public long AvgElapsedMs { get; set; }
    public long MaxElapsedMs { get; set; }
    public long P50ElapsedMs { get; set; }
    public long P95ElapsedMs { get; set; }
    public long P99ElapsedMs { get; set; }
}

/// <summary>
/// API 调用统计摘要
/// </summary>
public class ApiCallMetricsSummary
{
    public int TotalCalls { get; set; }
    public int SuccessCount { get; set; }
    public int ErrorCount { get; set; }
    public long AvgElapsedMs { get; set; }
    public long MaxElapsedMs { get; set; }
    public long MinElapsedMs { get; set; }
    public int UniqueEndpoints { get; set; }
    
    public double SuccessRate => TotalCalls > 0 ? (double)SuccessCount / TotalCalls * 100 : 0;
}
