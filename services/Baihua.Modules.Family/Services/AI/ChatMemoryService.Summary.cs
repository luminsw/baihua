using Baihua.Core.Models;
using Baihua.Core.Services;
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
    #region Layer 2: 摘要压缩

    /// <summary>
    /// 对历史消息进行摘要压缩：超过阈值时，将早期对话压缩为摘要
    /// </summary>
    public async Task<MemoryContext> BuildMemoryContextAsync(
        List<ChatHistoryItem> history,
        string model,
        string providerId,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        var result = new MemoryContext();

        if (history == null || history.Count == 0)
            return result;

        // 先按 Token 预算截断
        var trimmed = TrimByTokenBudget(history, model);

        // 判断是否需要摘要压缩
        var roundCount = trimmed.Count / 2; // 粗估轮数
        if (roundCount <= SummaryThreshold)
        {
            // 不需要压缩，直接返回截断后的历史
            result.RecentHistory = trimmed;
            return result;
        }

        // 分割：早期部分压缩为摘要，最近部分保留完整
        var splitIndex = Math.Max(0, trimmed.Count - RecentRoundsToKeep * 2);
        var earlyHistory = trimmed.Take(splitIndex).ToList();
        var recentHistory = trimmed.Skip(splitIndex).ToList();

        // 获取或生成早期对话的摘要
        result.Summary = await GetOrCreateSummaryAsync(earlyHistory, model, providerId, sessionId, ct);
        result.RecentHistory = recentHistory;

        _logger.LogInformation("记忆压缩：{EarlyCount} 条早期对话压缩为摘要，保留 {RecentCount} 条最近对话",
            earlyHistory.Count, recentHistory.Count);

        return result;
    }

    /// <summary>
    /// 获取或创建早期对话的摘要
    /// </summary>
    private async Task<string?> GetOrCreateSummaryAsync(
        List<ChatHistoryItem> earlyHistory,
        string model,
        string providerId,
        string? sessionId,
        CancellationToken ct)
    {
        if (earlyHistory.Count == 0)
            return null;

        // 尝试从数据库读取已缓存的摘要
        if (!string.IsNullOrEmpty(sessionId))
        {
            var cached = await GetCachedSummaryAsync(sessionId);
            if (cached != null)
            {
                _logger.LogDebug("使用缓存的对话摘要（会话 {SessionId}）", sessionId);
                return cached;
            }
        }

        // 调用 AI 生成摘要
        try
        {
            var summary = await SummarizeHistoryAsync(earlyHistory, model, providerId, ct);

            // 缓存摘要到数据库
            if (!string.IsNullOrEmpty(sessionId) && summary != null)
            {
                await CacheSummaryAsync(sessionId, summary);
            }

            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "生成对话摘要失败，回退到直接使用早期历史");
            return null;
        }
    }

    /// <summary>
    /// 调用 AI 将早期对话压缩为摘要
    /// </summary>
    private async Task<string?> SummarizeHistoryAsync(
        List<ChatHistoryItem> earlyHistory,
        string model,
        string providerId,
        CancellationToken ct)
    {
        var historyText = string.Join("\n", earlyHistory.Select(h =>
            $"{(h.Role == "user" ? "用户" : "AI")}: {TruncateForSummary(h.Content, 500)}"));

        var prompt = $"请将以下对话历史压缩为一段简洁的摘要（200字以内），保留关键事实、决策和结论，省略寒暄和重复：\n\n{historyText}";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "你是一个对话摘要助手，输出简洁准确的中文摘要。"),
            new(ChatRole.User, prompt)
        };

        var chatClient = _aiClientService.CreateChatClient(providerId, model);
        var options = AiClientService.BuildChatOptions(temperature: 0.3f, maxOutputTokens: 500);

        var response = await chatClient.GetResponseAsync(messages, options, ct);
        return response.Text;
    }

    private static string TruncateForSummary(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            return text;
        return text.Substring(0, maxChars) + "...";
    }

    private async Task<string?> GetCachedSummaryAsync(string sessionId)
    {
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            // 查找该会话最新的摘要（Round=0 表示摘要）
            var entry = await db.Set<ChatMemoryEntry>()
                .Where(e => e.SessionId == sessionId && e.Round == 0)
                .OrderByDescending(e => e.CreatedAt)
                .FirstOrDefaultAsync();
            return entry?.UserSummary;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取缓存摘要失败");
            return null;
        }
    }

    private async Task CacheSummaryAsync(string sessionId, string summary)
    {
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            // 删除旧摘要
            var old = await db.Set<ChatMemoryEntry>()
                .Where(e => e.SessionId == sessionId && e.Round == 0)
                .ToListAsync();
            if (old.Count > 0)
                db.Set<ChatMemoryEntry>().RemoveRange(old);

            db.Set<ChatMemoryEntry>().Add(new ChatMemoryEntry
            {
                SessionId = sessionId,
                Round = 0,
                UserSummary = summary,
                AssistantSummary = "",
                UserContent = "",
                AssistantContent = "",
                EstimatedTokens = EstimateTokens(summary),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "缓存摘要失败");
        }
    }

    #endregion

}
