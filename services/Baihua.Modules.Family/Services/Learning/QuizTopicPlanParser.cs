using System.Text.Json;
using Baihua.Contracts.Learn;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 知识点规划解析器（自适应题量的第一阶段）：从 AI 输出中提取 topics 清单。
/// 解析失败返回 null，调用方回退到「固定分批」策略。
/// </summary>
public static class QuizTopicPlanParser
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static QuizTopicPlanDto? Parse(string? raw)
    {
        var json = QuizBankResponseParser.ExtractJson(raw);
        if (json is null) return null;

        try
        {
            var plan = JsonSerializer.Deserialize<QuizTopicPlanDto>(json, JsonOpts);
            if (plan?.Topics is null) return null;
            plan.Topics = plan.Topics
                .Where(t => !string.IsNullOrWhiteSpace(t.Title))
                .ToList();
            return plan;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}