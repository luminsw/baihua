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
    #region Layer 3: 语义检索记忆

    /// <summary>
    /// 存储一轮对话到记忆库（含向量索引）
    /// </summary>
    public async Task StoreMemoryAsync(
        string sessionId,
        int round,
        string userContent,
        string assistantContent,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        try
        {
            var userSummary = TruncateForSummary(userContent, 200);
            var assistantSummary = TruncateForSummary(assistantContent, 200);
            var totalTokens = EstimateTokens(userContent) + EstimateTokens(assistantContent);

            // 生成向量（用于语义检索）
            string? vectorJson = null;
            int dimensions = 0;
            var textToEmbed = $"用户: {userSummary}\nAI: {assistantSummary}";
            var embedding = await _embeddingService.GetEmbeddingAsync(textToEmbed);
            if (embedding != null)
            {
                vectorJson = JsonSerializer.Serialize(embedding);
                dimensions = embedding.Count;
            }

            using var db = await _dbFactory.CreateDbContextAsync();

            // 清理该会话过旧的记忆
            var existing = await db.Set<ChatMemoryEntry>()
                .Where(e => e.SessionId == sessionId && e.Round > 0)
                .OrderByDescending(e => e.Round)
                .ToListAsync();

            if (existing.Count >= MaxMemoryEntriesPerSession)
            {
                var toRemove = existing.Skip(MaxMemoryEntriesPerSession).ToList();
                db.Set<ChatMemoryEntry>().RemoveRange(toRemove);
            }

            db.Set<ChatMemoryEntry>().Add(new ChatMemoryEntry
            {
                SessionId = sessionId,
                Round = round,
                UserSummary = userSummary,
                AssistantSummary = assistantSummary,
                UserContent = userContent,
                AssistantContent = assistantContent,
                VectorJson = vectorJson,
                Dimensions = dimensions,
                EstimatedTokens = totalTokens,
                CreatedAt = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
            _logger.LogDebug("存储对话记忆：会话 {SessionId}，轮次 {Round}", sessionId, round);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "存储对话记忆失败（不影响主流程）");
        }
    }

    /// <summary>
    /// 语义检索与当前问题相关的历史记忆
    /// </summary>
    public async Task<List<RetrievedMemory>> RetrieveRelevantMemoriesAsync(
        string sessionId,
        string query,
        int topK = MaxRetrievedMemories,
        CancellationToken ct = default)
    {
        var result = new List<RetrievedMemory>();

        if (string.IsNullOrEmpty(sessionId) || !_embeddingService.IsSemanticSearchEnabled())
            return result;

        try
        {
            var queryEmbedding = await _embeddingService.GetEmbeddingAsync(query);
            if (queryEmbedding == null)
                return result;

            using var db = await _dbFactory.CreateDbContextAsync();
            var memories = await db.Set<ChatMemoryEntry>()
                .Where(e => e.SessionId == sessionId && e.Round > 0 && e.VectorJson != null)
                .OrderByDescending(e => e.Round)
                .Take(50) // 只对最近 50 条做相似度计算
                .ToListAsync();

            var scored = new List<(ChatMemoryEntry entry, double score)>();
            foreach (var mem in memories)
            {
                try
                {
                    var vector = JsonSerializer.Deserialize<List<double>>(mem.VectorJson!);
                    if (vector != null && vector.Count > 0)
                    {
                        var similarity = CosineSimilarity(queryEmbedding, vector);
                        scored.Add((mem, similarity));
                    }
                }
                catch { /* 跳过无效向量 */ }
            }

            // 取相似度最高的 topK 条
            foreach (var (entry, score) in scored.OrderByDescending(x => x.score).Take(topK))
            {
                if (score < 0.5) continue; // 相似度阈值
                result.Add(new RetrievedMemory
                {
                    UserContent = entry.UserContent,
                    AssistantContent = entry.AssistantContent,
                    Similarity = score,
                    Round = entry.Round
                });
            }

            _logger.LogDebug("语义检索记忆：查询 '{Query}'，找到 {Count} 条相关记忆", 
                TruncateForSummary(query, 50), result.Count);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "语义检索记忆失败");
        }

        return result;
    }

    private static double CosineSimilarity(List<double> a, List<double> b)
    {
        if (a.Count != b.Count || a.Count == 0)
            return 0;

        double dotProduct = 0, magnitudeA = 0, magnitudeB = 0;
        for (int i = 0; i < a.Count; i++)
        {
            dotProduct += a[i] * b[i];
            magnitudeA += a[i] * a[i];
            magnitudeB += b[i] * b[i];
        }

        magnitudeA = Math.Sqrt(magnitudeA);
        magnitudeB = Math.Sqrt(magnitudeB);

        if (magnitudeA == 0 || magnitudeB == 0)
            return 0;

        return dotProduct / (magnitudeA * magnitudeB);
    }

    #endregion

    /// <summary>
    /// 构建最终发送给 AI 的消息列表（整合三层记忆）
    /// </summary>
    public async Task<List<ChatMessage>> BuildMessagesWithMemoryAsync(
        List<ChatHistoryItem>? history,
        string providerId,
        string model,
        string currentMessage,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>();

        // System Prompt
        var template = _scenePromptService.GetTemplate();
        messages.Add(new ChatMessage(ChatRole.System, template.ChatSystemPrompt));

        if (history == null || history.Count == 0)
        {
            messages.Add(new ChatMessage(ChatRole.User, currentMessage));
            return messages;
        }

        // Layer 2: 摘要压缩 + Layer 1: Token 预算截断
        var memoryContext = await BuildMemoryContextAsync(history, model, providerId, sessionId, ct);

        // 注入摘要（如果有）
        if (!string.IsNullOrEmpty(memoryContext.Summary))
        {
            messages.Add(new ChatMessage(ChatRole.System,
                $"{_loc["ChatMemory_HistorySummary"]}\n{memoryContext.Summary}"));
        }

        // Layer 3: 语义检索相关记忆
        if (!string.IsNullOrEmpty(sessionId) && _embeddingService.IsSemanticSearchEnabled())
        {
            var relevantMemories = await RetrieveRelevantMemoriesAsync(sessionId, currentMessage, ct: ct);
            if (relevantMemories.Count > 0)
            {
                var memoryText = string.Join("\n---\n", relevantMemories.Select(m =>
                    $"用户: {TruncateForSummary(m.UserContent, 300)}\nAI: {TruncateForSummary(m.AssistantContent, 300)}"));
                messages.Add(new ChatMessage(ChatRole.System,
                    $"{_loc["ChatMemory_RelevantMemories"]}\n{memoryText}"));
            }
        }

        // 注入最近完整对话
        foreach (var item in memoryContext.RecentHistory)
        {
            var role = string.Equals(item.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.Assistant
                : ChatRole.User;
            messages.Add(new ChatMessage(role, item.Content));
        }

        // 当前用户消息
        messages.Add(new ChatMessage(ChatRole.User, currentMessage));

        return messages;
    }
}
