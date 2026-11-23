using System.ComponentModel.DataAnnotations;

namespace Baihua.Data.Entities;

/// <summary>
/// Benchmark 测试结果（SQLite 持久化）
/// </summary>
public class BenchmarkSessionEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// 会话唯一 ID
    /// </summary>
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N")[..16];

    /// <summary>
    /// 测试时间
    /// </summary>
    public DateTime TestedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 显示名称，如 ollama/qwen2.5:7b
    /// </summary>
    public string ModelName { get; set; } = "";

    /// <summary>
    /// 分类：tcm / coding
    /// </summary>
    public string Category { get; set; } = "";

    /// <summary>
    /// Provider ID
    /// </summary>
    public string ProviderId { get; set; } = "";

    /// <summary>
    /// 模型 ID
    /// </summary>
    public string ModelId { get; set; } = "";

    /// <summary>
    /// 结果 JSON 数组
    /// </summary>
    public string ResultsJson { get; set; } = "[]";

    /// <summary>
    /// 平均 TPS
    /// </summary>
    public double AvgTokensPerSecond { get; set; }

    /// <summary>
    /// 平均耗时(ms)
    /// </summary>
    public double AvgLatencyMs { get; set; }

    /// <summary>
    /// 平均质量分
    /// </summary>
    public double AvgQualityScore { get; set; }

    /// <summary>
    /// 完成率（%）
    /// </summary>
    public double CompletionRate { get; set; }
}
