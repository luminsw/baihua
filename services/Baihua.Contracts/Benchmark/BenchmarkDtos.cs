namespace Baihua.Contracts.Benchmark;

/// <summary>
/// 模型基准测试配置（前端选择要测的模型）
/// </summary>
public class BenchmarkModelConfig
{
    public string ProviderId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = ""; // "tcm" | "coding"
}

/// <summary>
/// 内置推荐模型（通用 / 编程）
/// </summary>
public class RecommendedBenchmarkModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public string SizeInfo { get; set; } = "";
    public string VramInfo { get; set; } = "";
    public string? OllamaName { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();
}

/// <summary>
/// 显存等级内的模型条目
/// </summary>
public class ModelInTier
{
    public string Name { get; set; } = "";
    public string Params { get; set; } = "";
    public string SizeGb { get; set; } = "";
    public string Description { get; set; } = "";
    public string OllamaName { get; set; } = "";
    public string Company { get; set; } = "其他";
}

/// <summary>
/// 显存等级推荐（含 INT4 / INT8 模型列表）
/// </summary>
public class VramTierDto
{
    public int VramGb { get; set; }
    public List<ModelInTier> Int4Models { get; set; } = new();
    public List<ModelInTier> Int8Models { get; set; } = new();
    public bool IsRecommendedForCurrentHardware { get; set; }
}

/// <summary>

/// 单个测试提示词定义
/// </summary>
public class BenchmarkPrompt
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = ""; // "tcm" | "coding"
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string[] ExpectedKeywords { get; set; } = Array.Empty<string>();
    public int MaxTokens { get; set; } = 1024;
    public int Weight { get; set; } = 1;
}

/// <summary>
/// 运行基准测试的请求
/// </summary>
public class RunBenchmarkRequest
{
    public BenchmarkModelConfig Model { get; set; } = new();
    public string[]? PromptIds { get; set; }
}

/// <summary>
/// 单次提示词的测试结果
/// </summary>
public class BenchmarkPromptResult
{
    public string PromptId { get; set; } = "";
    public string PromptTitle { get; set; } = "";
    public long LatencyMs { get; set; }
    public int OutputChars { get; set; }
    public double TokensPerSecond { get; set; }
    public string ResponseText { get; set; } = "";
    public int QualityScore { get; set; }
    public string[] MatchedKeywords { get; set; } = Array.Empty<string>();
    public string[] MissingKeywords { get; set; } = Array.Empty<string>();
    public string? Error { get; set; }
    public bool IsTimeout { get; set; }
    public bool IsError => !string.IsNullOrEmpty(Error) && !IsTimeout;
}

/// <summary>
/// 一次完整测试会话的结果
/// </summary>
public class BenchmarkSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime TestedAt { get; set; } = DateTime.Now;
    public string ModelName { get; set; } = "";
    public string Category { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public List<BenchmarkPromptResult> Results { get; set; } = new();

    private List<BenchmarkPromptResult> CompletedResults => Results.Where(r => !r.IsTimeout && !r.IsError && r.LatencyMs > 0).ToList();

    public double AvgTokensPerSecond => CompletedResults.Count > 0 ? CompletedResults.Average(r => r.TokensPerSecond) : 0;
    public double AvgLatencyMs => CompletedResults.Count > 0 ? CompletedResults.Average(r => r.LatencyMs) : 0;
    public double AvgQualityScore => CompletedResults.Count > 0 ? CompletedResults.Average(r => r.QualityScore) : 0;
    public int TotalScore => CompletedResults.Count > 0 ? (int)CompletedResults.Average(r => r.QualityScore) : 0;
    public double CompletionRate => Results.Count > 0 ? (double)CompletedResults.Count / Results.Count : 0;
}

/// <summary>
/// 排行榜条目
/// </summary>
public class BenchmarkLeaderboardEntry
{
    public string ModelName { get; set; } = "";
    public string Category { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public double AvgTokensPerSecond { get; set; }
    public double AvgLatencyMs { get; set; }
    public double AvgQualityScore { get; set; }
    public int TestCount { get; set; }
    public DateTime? LastTestedAt { get; set; }
}

/// <summary>
/// 基准测试状态（用于前端轮询）
/// </summary>
public class BenchmarkStatusDto
{
    public string Status { get; set; } = "idle"; // idle | running | completed | failed
    public string? CurrentPromptTitle { get; set; }
    public int CompletedCount { get; set; }
    public int TotalCount { get; set; }
    public string? Error { get; set; }
    public BenchmarkSession? Result { get; set; }
}
