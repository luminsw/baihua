using Baihua.Core.Models;
using Baihua.Core.Services;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Baihua.Contracts.Generate;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 知识库主题推荐服务：在 AI 生成知识库页预置选题词句。
/// 每天刷新一次（缓存到次日 0 点过期），可按用户知识库构成个性化，
/// 结合"用户已有兴趣 + 当前热点 + 高价值实用"三类主题。
/// AI 不可用时回退到内置主题池，保证页面永不空态。
/// </summary>
public class TopicSuggestionService
{
    private readonly AiClientService _aiClient;
    private readonly AiSettingsService _aiSettings;
    private readonly IDistributedCache _cache;
    private readonly ILogger<TopicSuggestionService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TopicSuggestionService(
        AiClientService aiClient,
        AiSettingsService aiSettings,
        IDistributedCache cache,
        ILogger<TopicSuggestionService> logger)
    {
        _aiClient = aiClient;
        _aiSettings = aiSettings;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// 获取今日推荐主题（缓存命中直接返回；refresh=true 强制重新生成）。
    /// context：用户知识库构成摘要（如"中医/中医抗敏,计算机,烘焙初体验"），用于个性化。
    /// </summary>
    public async Task<TopicSuggestionResponse> GetSuggestionsAsync(string? context, bool refresh, CancellationToken ct)
    {
        var contextKey = string.IsNullOrWhiteSpace(context)
            ? ""
            : "|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context)))[..12];
        var cacheKey = "gen:topics" + contextKey;

        if (!refresh)
        {
            var cached = await _cache.GetStringAsync(cacheKey, ct);
            if (cached != null)
            {
                var hit = JsonSerializer.Deserialize<TopicSuggestionResponse>(cached);
                if (hit is { Suggestions.Count: > 0 }) return hit;
            }
        }

        await _gate.WaitAsync(ct);
        try
        {
            // 拿到锁后二次检查（并发去重：同一时刻只有一个请求真正调 AI）
            if (!refresh)
            {
                var cached = await _cache.GetStringAsync(cacheKey, ct);
                if (cached != null)
                {
                    var hit = JsonSerializer.Deserialize<TopicSuggestionResponse>(cached);
                    if (hit is { Suggestions.Count: > 0 }) return hit;
                }
            }

            // 换一批：把上一批标题带进提示词，要求避开，避免 AI 输出趋同
            var avoidTitles = new List<string>();
            if (refresh)
            {
                var old = await _cache.GetStringAsync(cacheKey, ct);
                if (old != null)
                {
                    var oldResp = JsonSerializer.Deserialize<TopicSuggestionResponse>(old);
                    if (oldResp?.Suggestions != null)
                        avoidTitles.AddRange(oldResp.Suggestions.Select(s => s.Title));
                }
            }

            var response = await GenerateCoreAsync(context, avoidTitles, ct);
            // 次日 0 点过期 → 每天刷新一次
            var expires = DateTime.Now.Date.AddDays(1);
            response.ExpiresAt = expires;
            await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(response),
                new DistributedCacheEntryOptions { AbsoluteExpiration = expires }, ct);
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TopicSuggestionResponse> GenerateCoreAsync(string? context, List<string> avoidTitles, CancellationToken ct)
    {
        var provider = _aiSettings.GetMainAiProvider();
        if (provider == null)
        {
            _logger.LogWarning("未配置 AI 提供商，主题推荐使用内置主题池");
            return Fallback();
        }
        var model = _aiSettings.GetModelForProvider(provider.Id);

        var contextLine = string.IsNullOrWhiteSpace(context)
            ? "（暂无用户知识库信息，请推荐通用的高价值主题）"
            : $"用户知识库已有方向：{context.Trim()}。请优先推荐与之相关的拓展主题，同时兼顾 2-3 个全新领域。";
        var avoidLine = avoidTitles.Count == 0
            ? ""
            : $"\n6. 以下主题是刚刚推荐过的，请完全避开（不要只改几个字）：{string.Join("、", avoidTitles.Take(10))}";

        var prompt = $$"""
            你是家庭知识库的内容选题策划。用户会从你推荐的主题中挑一个，用来生成一份完整的知识库（约 10-40 篇笔记）。
            请推荐 10 个主题词句，要求：
            1. 混合构成：一部分贴近用户已有兴趣（结合下方知识库信息），一部分是当前值得了解的热点/新领域，一部分是高价值的实用生活主题（健康、育儿、理财、家庭、效率等）。
            2. 每个主题是可直接作为知识库选题的短语或短句（8-30 字），如"如何科学安排孩子的睡眠"、"家用 NAS 入门与数据备份"。
            3. 主题要具体、可展开、有吸引力，避免空泛（"健康"太宽泛，应具体到"体检报告怎么看：常见指标解读"）。
            4. 避免政治、宗教、争议性敏感话题。
            5. 只输出一个 JSON 数组（不要 markdown 代码块，不要任何其他文字）：
            [{"title":"主题词句","category":"健康|科技|育儿|理财|生活|效率|兴趣|热点","description":"一句话说明这个主题能学到什么（≤25字）"}]
            {{contextLine}}
            {{avoidLine}}
            """;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "你是资深的内容选题策划，只输出严格 JSON。"),
            new(ChatRole.User, prompt)
        };
        var options = new ChatOptions { Temperature = 0.8f, MaxOutputTokens = 2000 };

        ChatResponse raw;
        try
        {
            raw = await _aiClient.GetChatResponseWithAutoStartAsync(
                provider, model, messages, options, ct, operation: "topics");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "主题推荐 AI 调用失败，使用内置主题池");
            return Fallback();
        }

        var list = ParseSuggestions(raw.Text ?? "");
        if (list.Count < 6)
        {
            _logger.LogWarning("主题推荐解析结果过少({Count})，使用内置主题池", list.Count);
            return Fallback();
        }
        return new TopicSuggestionResponse
        {
            Suggestions = list,
            Source = "ai",
            GeneratedAt = DateTime.Now
        };
    }

    /// <summary>内置主题池（AI 不可用/未配置时的兜底，保证页面永不空态）</summary>
    private static readonly TopicSuggestion[] FallbackPool =
    [
        new() { Title = "体检报告怎么看：常见指标解读", Category = "健康", Description = "读懂血常规、血脂、血糖等关键指标" },
        new() { Title = "睡眠质量提升的科学方法", Category = "健康", Description = "睡眠周期、环境与习惯的系统调整" },
        new() { Title = "营养早餐搭配指南", Category = "健康", Description = "一周不重样的快手早餐方案" },
        new() { Title = "孩子上网课护眼指南", Category = "育儿", Description = "屏幕时间管理与视力保护的科学方法" },
        new() { Title = "孩子零花钱管理与财商启蒙", Category = "育儿", Description = "压岁钱、零花钱与储蓄习惯养成" },
        new() { Title = "家庭记账与月度预算实操", Category = "理财", Description = "从记账到预算分配的家庭理财入门" },
        new() { Title = "AI 工具入门：大模型能帮你做什么", Category = "科技", Description = "普通人用得上 AI 的 20 个场景" },
        new() { Title = "家用 NAS 入门与数据备份", Category = "科技", Description = "家庭照片与文件的安全存储方案" },
        new() { Title = "整理收纳与极简生活", Category = "生活", Description = "从玄关到衣柜的断舍离实操" },
        new() { Title = "家庭照片与视频归档整理", Category = "效率", Description = "备份、去重、按事件整理数字回忆" }
    ];

    private static TopicSuggestionResponse Fallback() => new()
    {
        Suggestions = FallbackPool.ToList(),
        Source = "fallback",
        GeneratedAt = DateTime.Now
    };

    #region JSON 容错解析

    /// <summary>从模型输出中提取 JSON 数组文本（去 markdown 围栏、取最外层括号块）</summary>
    private static string? TryExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        var fence = Regex.Match(t, "```(?:json)?\\s*([\\s\\S]*?)```");
        if (fence.Success) t = fence.Groups[1].Value.Trim();

        var arrStart = t.IndexOf('[');
        var arrEnd = t.LastIndexOf(']');
        var objStart = t.IndexOf('{');
        var objEnd = t.LastIndexOf('}');

        if (arrStart >= 0 && arrEnd > arrStart && (objStart < 0 || arrStart < objStart))
            return t[arrStart..(arrEnd + 1)];
        if (objStart >= 0 && objEnd > objStart)
            return t[objStart..(objEnd + 1)];
        return null;
    }

    private List<TopicSuggestion> ParseSuggestions(string text)
    {
        var json = TryExtractJson(text);
        if (json == null) return new List<TopicSuggestion>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var arr = root.ValueKind == JsonValueKind.Array
                ? root
                : root.TryGetProperty("suggestions", out var s) ? s : default;
            if (arr.ValueKind != JsonValueKind.Array) return new List<TopicSuggestion>();

            var list = new List<TopicSuggestion>();
            foreach (var item in arr.EnumerateArray())
            {
                var title = GetStr(item, "title").Trim();
                if (title.Length < 3 || title.Length > 40) continue; // 过滤空/超长
                var category = GetStr(item, "category").Trim();
                if (category.Length > 6) category = "兴趣";
                var description = GetStr(item, "description").Trim();
                if (description.Length > 40) description = description[..40];
                list.Add(new TopicSuggestion { Title = title, Category = category, Description = description });
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "主题推荐 JSON 解析失败");
            return new List<TopicSuggestion>();
        }
    }

    private static string GetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    #endregion
}
