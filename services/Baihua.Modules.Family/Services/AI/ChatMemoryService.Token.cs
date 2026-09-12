using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Baihua.Contracts.Ai;
using Baihua.Modules.Family.Controllers;
using Baihua.Data;
using Baihua.Data.Entities;

namespace Baihua.Modules.Family.Services;

public partial class ChatMemoryService
{
    #region Layer 1: Token 预算截断

    /// <summary>
    /// 根据模型名称估算上下文窗口大小（token 数）
    /// </summary>
    public int GetContextWindowTokens(string model)
    {
        if (string.IsNullOrEmpty(model))
            return 8_000;

        var m = model.ToLowerInvariant();

        // 常见模型的上下文窗口
        if (m.Contains("128k")) return 128_000;
        if (m.Contains("32k")) return 32_000;
        if (m.Contains("gpt-4o") || m.Contains("gpt4o")) return 128_000;
        if (m.Contains("gpt-4") || m.Contains("gpt4")) return 8_192;
        if (m.Contains("claude-3.5") || m.Contains("claude3.5")) return 200_000;
        if (m.Contains("claude-3")) return 200_000;
        if (m.Contains("qwen2.5-72b") || m.Contains("qwen2_5-72b")) return 32_768;
        if (m.Contains("qwen2.5-14b") || m.Contains("qwen2_5-14b")) return 32_768;
        if (m.Contains("qwen2.5-7b") || m.Contains("qwen2_5-7b")) return 32_768;
        if (m.Contains("deepseek-r1") || m.Contains("deepseek_v3")) return 128_000;
        if (m.Contains("deepseek")) return 64_000;
        if (m.Contains("llama3.1") || m.Contains("llama3_1")) return 128_000;
        if (m.Contains("llama3")) return 8_192;
        if (m.Contains("glm-4")) return 128_000;

        // 默认保守值
        return 8_000;
    }

    /// <summary>
    /// 粗估文本的 token 数
    /// 中文约 1.5 字/token，英文约 4 字符/token，混合取中间值
    /// </summary>
    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        // 统计中文字符数
        int cjkCount = 0;
        int totalChars = 0;
        foreach (char c in text)
        {
            if (c > 0x4E00 && c < 0x9FFF) // CJK Unified Ideographs
                cjkCount++;
            totalChars++;
        }

        // 中文: ~1.5 字/token, 非中文: ~4 字符/token
        var cjkTokens = cjkCount / 1.5;
        var otherTokens = (totalChars - cjkCount) / 4.0;
        return (int)Math.Ceiling(cjkTokens + otherTokens);
    }

    /// <summary>
    /// 按 Token 预算截断历史消息（从最新往前保留）
    /// </summary>
    public List<ChatHistoryItem> TrimByTokenBudget(List<ChatHistoryItem> history, string model, int reserveForOutput = 2000, int reserveForSystem = 1000)
    {
        if (history == null || history.Count == 0)
            return history ?? new List<ChatHistoryItem>();

        var budget = GetContextWindowTokens(model) - reserveForOutput - reserveForSystem;
        if (budget < 1000) budget = 1000; // 最低保底

        var result = new List<ChatHistoryItem>();
        var usedTokens = 0;

        // 从最新消息往前加
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var cost = EstimateTokens(history[i].Content);
            if (usedTokens + cost > budget)
                break;
            usedTokens += cost;
            result.Insert(0, history[i]);
        }

        _logger.LogDebug("Token 预算截断：模型 {Model} 预算 {Budget}，保留 {Kept}/{Total} 条历史，使用 {Used} tokens",
            model, budget, result.Count, history.Count, usedTokens);

        return result;
    }

    #endregion

}
