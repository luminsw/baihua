namespace Baihua.Contracts.Metrics;

/// <summary>
/// AI 调用指标总览
/// </summary>
public class AiMetricsSummaryDto
{
    public int TotalCalls { get; set; }
    public long AvgLatencyMs { get; set; }
    public double SuccessRate { get; set; }
    public long TotalTokens { get; set; }
    public string? BestProvider { get; set; }
    public double? BestProviderTps { get; set; }
}

/// <summary>
/// Provider 排行榜条目
/// </summary>
public class AiProviderMetricsDto
{
    public string ProviderId { get; set; } = "";
    public string ProviderName { get; set; } = "";
    public int CallCount { get; set; }
    public long AvgLatencyMs { get; set; }
    public double? AvgTokensPerSecond { get; set; }
    public double SuccessRate { get; set; }
    public long TotalTokens { get; set; }
}

/// <summary>
/// 模型排行榜条目
/// </summary>
public class AiModelMetricsDto
{
    public string ModelId { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string ProviderName { get; set; } = "";
    public int CallCount { get; set; }
    public long AvgLatencyMs { get; set; }
    public double? AvgTokensPerSecond { get; set; }
    public double SuccessRate { get; set; }
}

/// <summary>
/// 每日趋势数据点
/// </summary>
public class AiMetricsTrendDto
{
    public string Date { get; set; } = "";
    public int CallCount { get; set; }
    public long AvgLatencyMs { get; set; }
    public long TotalTokens { get; set; }
}

/// <summary>
/// 最近调用记录
/// </summary>
public class AiUsageMetricDto
{
    public int Id { get; set; }
    public DateTime CalledAt { get; set; }
    public string ProviderId { get; set; } = "";
    public string ProviderName { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string Operation { get; set; } = "";
    public long LatencyMs { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? TotalTokens { get; set; }
    public double? TokensPerSecond { get; set; }
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
}
