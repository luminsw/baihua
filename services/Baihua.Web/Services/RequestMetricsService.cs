using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Baihua.Web.Services;

/// <summary>
/// 请求统计服务：记录请求耗时和频率，同时通过 OpenTelemetry Metrics 导出到 OpenObserve
/// </summary>
public class RequestMetricsService : IDisposable
{
    public const string MeterName = "Baihua.Web";

    // 保留最近的 1000 条请求记录（内存限制，供 WebUI 本地查询）
    private readonly ConcurrentQueue<RequestMetric> _metrics = new();
    private const int MaxMetricsCount = 1000;

    // OpenTelemetry Metrics
    private readonly Meter _meter;
    private readonly Histogram<double> _requestDuration;
    private readonly Counter<long> _requestTotal;

    public RequestMetricsService()
    {
        _meter = new Meter(MeterName, "1.0.0");
        _requestDuration = _meter.CreateHistogram<double>("http.request.duration_ms", unit: "ms", description: "WebUI HTTP request duration");
        _requestTotal = _meter.CreateCounter<long>("http.request.total", unit: "{request}", description: "WebUI HTTP request total");
    }

    /// <summary>
    /// 记录一条请求指标
    /// </summary>
    public void RecordRequest(string method, string path, long elapsedMs, int statusCode)
    {
        var metric = new RequestMetric
        {
            Timestamp = DateTime.UtcNow,
            Method = method,
            Path = path,
            ElapsedMilliseconds = elapsedMs,
            StatusCode = statusCode
        };

        _metrics.Enqueue(metric);

        // 限制队列大小
        while (_metrics.Count > MaxMetricsCount && _metrics.TryDequeue(out _))
        {
            // 移除最老的记录
        }

        // 同时通过 OpenTelemetry Metrics 推送（OpenObserve 可见）
        var tags = new TagList();
        tags.Add("method", method);
        tags.Add("path", path);
        tags.Add("status_code", statusCode);

        _requestTotal.Add(1, tags);
        _requestDuration.Record(elapsedMs, tags);
    }

    /// <summary>
    /// 获取耗时最长的10条请求
    /// </summary>
    public IReadOnlyList<RequestMetric> GetSlowestRequests(int count = 10)
    {
        return _metrics
            .OrderByDescending(m => m.ElapsedMilliseconds)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// 获取调用频率最高的10条请求（按路径分组）
    /// </summary>
    public IReadOnlyList<PathFrequency> GetMostFrequentPaths(int count = 10)
    {
        return _metrics
            .GroupBy(m => $"{m.Method} {m.Path}")
            .Select(g => new PathFrequency
            {
                Method = g.First().Method,
                Path = g.First().Path,
                FullPath = g.Key,
                Count = g.Count(),
                AvgElapsedMs = (long)g.Average(m => m.ElapsedMilliseconds),
                MaxElapsedMs = g.Max(m => m.ElapsedMilliseconds),
                MinElapsedMs = g.Min(m => m.ElapsedMilliseconds)
            })
            .OrderByDescending(p => p.Count)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// 获取总体统计
    /// </summary>
    public MetricsSummary GetSummary()
    {
        var metrics = _metrics.ToList();
        if (metrics.Count == 0)
        {
            return new MetricsSummary { TotalRequests = 0 };
        }

        return new MetricsSummary
        {
            TotalRequests = metrics.Count,
            AvgElapsedMs = (long)metrics.Average(m => m.ElapsedMilliseconds),
            MaxElapsedMs = metrics.Max(m => m.ElapsedMilliseconds),
            MinElapsedMs = metrics.Min(m => m.ElapsedMilliseconds),
            ErrorCount = metrics.Count(m => m.StatusCode >= 400),
            UniquePaths = metrics.Select(m => $"{m.Method} {m.Path}").Distinct().Count()
        };
    }

    /// <summary>
    /// 清空统计
    /// </summary>
    public void Clear()
    {
        _metrics.Clear();
    }

    /// <summary>
    /// 获取最近的错误请求（4xx/5xx）
    /// </summary>
    public IReadOnlyList<RequestMetric> GetRecentErrors(int count = 10)
    {
        return _metrics
            .Where(m => m.StatusCode >= 400)
            .OrderByDescending(m => m.Timestamp)
            .Take(count)
            .ToList();
    }

    public void Dispose()
    {
        _meter.Dispose();
    }
}


