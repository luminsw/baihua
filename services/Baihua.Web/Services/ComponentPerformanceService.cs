using System.Collections.Concurrent;
using System.Diagnostics;

namespace Baihua.Web.Services;

/// <summary>
/// 组件性能监控服务 - 记录各组件渲染时间
/// </summary>
public class ComponentPerformanceService
{
    private readonly ConcurrentDictionary<string, ComponentMetrics> _metrics = new();
    private readonly ConcurrentQueue<RenderEvent> _recentEvents = new();
    private readonly int _maxRecentEvents = 1000;
    
    /// <summary>
    /// 慢组件阈值（毫秒）
    /// </summary>
    public int SlowThresholdMs { get; set; } = 100;
    
    /// <summary>
    /// 记录组件渲染开始
    /// </summary>
    public RenderToken BeginRender(string componentName, string? instanceId = null)
    {
        var token = new RenderToken
        {
            ComponentName = componentName,
            InstanceId = instanceId ?? Guid.NewGuid().ToString("N")[..8],
            StartTime = Stopwatch.GetTimestamp(),
            Stopwatch = Stopwatch.StartNew()
        };
        
        return token;
    }
    
    /// <summary>
    /// 记录组件渲染结束
    /// </summary>
    public void EndRender(RenderToken token, bool isFirstRender = false)
    {
        token.Stopwatch.Stop();
        var elapsedMs = token.Stopwatch.ElapsedMilliseconds;
        
        // 更新指标
        var metrics = _metrics.GetOrAdd(token.ComponentName, _ => new ComponentMetrics { ComponentName = token.ComponentName });
        lock (metrics)
        {
            metrics.RenderCount++;
            metrics.TotalRenderTimeMs += elapsedMs;
            metrics.AverageRenderTimeMs = metrics.TotalRenderTimeMs / metrics.RenderCount;
            metrics.MaxRenderTimeMs = Math.Max(metrics.MaxRenderTimeMs, elapsedMs);
            metrics.MinRenderTimeMs = metrics.MinRenderTimeMs == 0 ? elapsedMs : Math.Min(metrics.MinRenderTimeMs, elapsedMs);
            
            if (elapsedMs > SlowThresholdMs)
            {
                metrics.SlowRenderCount++;
            }
            
            metrics.LastRenderTime = DateTime.Now;
            metrics.LastRenderDurationMs = elapsedMs;
        }
        
        // 记录最近事件
        var evt = new RenderEvent
        {
            ComponentName = token.ComponentName,
            InstanceId = token.InstanceId,
            ElapsedMilliseconds = elapsedMs,
            IsFirstRender = isFirstRender,
            IsSlow = elapsedMs > SlowThresholdMs,
            Timestamp = DateTime.Now
        };
        
        _recentEvents.Enqueue(evt);
        
        // 限制队列大小
        while (_recentEvents.Count > _maxRecentEvents)
        {
            _recentEvents.TryDequeue(out _);
        }
    }
    
    /// <summary>
    /// 获取所有组件指标
    /// </summary>
    public List<ComponentMetrics> GetAllMetrics()
    {
        return _metrics.Values.OrderByDescending(m => m.TotalRenderTimeMs).ToList();
    }
    
    /// <summary>
    /// 获取慢组件（超过阈值的）
    /// </summary>
    public List<ComponentMetrics> GetSlowComponents()
    {
        return _metrics.Values
            .Where(m => m.AverageRenderTimeMs > SlowThresholdMs || m.MaxRenderTimeMs > SlowThresholdMs * 2)
            .OrderByDescending(m => m.AverageRenderTimeMs)
            .ToList();
    }
    
    /// <summary>
    /// 获取最近的渲染事件
    /// </summary>
    public List<RenderEvent> GetRecentEvents(int count = 100)
    {
        return _recentEvents.TakeLast(count).OrderByDescending(e => e.Timestamp).ToList();
    }
    
    /// <summary>
    /// 清空所有数据
    /// </summary>
    public void Clear()
    {
        _metrics.Clear();
        while (_recentEvents.TryDequeue(out _)) { }
    }
    
    /// <summary>
    /// 获取性能报告摘要
    /// </summary>
    public PerformanceSummary GetSummary()
    {
        var allMetrics = _metrics.Values.ToList();
        return new PerformanceSummary
        {
            TotalComponents = allMetrics.Count,
            TotalRenders = allMetrics.Sum(m => m.RenderCount),
            SlowRenders = allMetrics.Sum(m => m.SlowRenderCount),
            AverageRenderTimeMs = allMetrics.Any() ? allMetrics.Average(m => m.AverageRenderTimeMs) : 0,
            MaxRenderTimeMs = allMetrics.Any() ? allMetrics.Max(m => m.MaxRenderTimeMs) : 0,
            SlowComponentCount = allMetrics.Count(m => m.AverageRenderTimeMs > SlowThresholdMs),
            TopSlowComponents = GetSlowComponents().Take(5).ToList()
        };
    }
}

/// <summary>
/// 渲染令牌 - 用于跟踪一次渲染过程
/// </summary>
public class RenderToken
{
    public string ComponentName { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public long StartTime { get; set; }
    public Stopwatch Stopwatch { get; set; } = new();
}

/// <summary>
/// 组件指标
/// </summary>
public class ComponentMetrics
{
    public string ComponentName { get; set; } = "";
    public long RenderCount { get; set; }
    public long SlowRenderCount { get; set; }
    public double TotalRenderTimeMs { get; set; }
    public double AverageRenderTimeMs { get; set; }
    public double MaxRenderTimeMs { get; set; }
    public double MinRenderTimeMs { get; set; }
    public double LastRenderDurationMs { get; set; }
    public DateTime LastRenderTime { get; set; }
}

/// <summary>
/// 单次渲染事件
/// </summary>
public class RenderEvent
{
    public string ComponentName { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public double ElapsedMilliseconds { get; set; }
    public bool IsFirstRender { get; set; }
    public bool IsSlow { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// 性能报告摘要
/// </summary>
public class PerformanceSummary
{
    public int TotalComponents { get; set; }
    public long TotalRenders { get; set; }
    public long SlowRenders { get; set; }
    public double AverageRenderTimeMs { get; set; }
    public double MaxRenderTimeMs { get; set; }
    public int SlowComponentCount { get; set; }
    public List<ComponentMetrics> TopSlowComponents { get; set; } = new();
}
