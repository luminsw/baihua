namespace Baihua.Contracts.Generate;

/// <summary>AI 推荐的知识库主题词句（预置选题）</summary>
public class TopicSuggestion
{
    /// <summary>主题词句（可直接作为知识库选题，如"如何科学安排孩子的睡眠"）</summary>
    public string Title { get; set; } = "";

    /// <summary>类别：健康/科技/育儿/理财/生活/效率/兴趣/热点…</summary>
    public string Category { get; set; } = "";

    /// <summary>一句话说明（≤25 字）</summary>
    public string Description { get; set; } = "";
}

/// <summary>主题推荐响应</summary>
public class TopicSuggestionResponse
{
    public List<TopicSuggestion> Suggestions { get; set; } = new();

    /// <summary>来源：ai = AI 生成；fallback = 内置主题池（AI 不可用时兜底）</summary>
    public string Source { get; set; } = "fallback";

    public DateTime GeneratedAt { get; set; } = DateTime.Now;

    /// <summary>缓存过期时间（次日 0 点 → 每日刷新一次）</summary>
    public DateTime? ExpiresAt { get; set; }
}
