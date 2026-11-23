namespace Baihua.Data.Entities;

/// <summary>
/// AI 调用性能指标记录
/// </summary>
public class AiUsageMetric
{
    public int Id { get; set; }

    /// <summary>
    /// 调用时间（UTC）
    /// </summary>
    public DateTime CalledAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 提供商 ID，如 ollama, siliconflow
    /// </summary>
    public string ProviderId { get; set; } = "";

    /// <summary>
    /// 提供商显示名称
    /// </summary>
    public string ProviderName { get; set; } = "";

    /// <summary>
    /// 模型 ID，如 qwen2.5:7b
    /// </summary>
    public string ModelId { get; set; } = "";

    /// <summary>
    /// 操作类型：chat, benchmark, task, split, index, embedding, openclaw
    /// </summary>
    public string Operation { get; set; } = "";

    /// <summary>
    /// 总耗时（毫秒）
    /// </summary>
    public long LatencyMs { get; set; }

    /// <summary>
    /// 输入 Token 数
    /// </summary>
    public int? InputTokens { get; set; }

    /// <summary>
    /// 输出 Token 数
    /// </summary>
    public int? OutputTokens { get; set; }

    /// <summary>
    /// 总 Token 数
    /// </summary>
    public int? TotalTokens { get; set; }

    /// <summary>
    /// 每秒 Token 数
    /// </summary>
    public double? TokensPerSecond { get; set; }

    /// <summary>
    /// 是否成功
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// 错误信息（失败时）
    /// </summary>
    public string? ErrorMessage { get; set; }
}
