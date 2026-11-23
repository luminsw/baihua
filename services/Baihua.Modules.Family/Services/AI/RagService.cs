using Baihua.Core.Models;
using Baihua.Core.Services;
using Microsoft.Extensions.AI;
using Baihua.Modules.Family.Controllers;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 统一 RAG（检索增强生成）服务：为所有 AI 入口提供知识库上下文增强
/// </summary>
public class RagService
{
    private readonly VaultNoteIndexer _vaultNoteIndexer;
    private readonly EmbeddingService _embeddingService;
    private readonly VaultSettingsService _vaultSettings;
    private readonly ILogger<RagService> _logger;

    public RagService(
        VaultNoteIndexer vaultNoteIndexer,
        EmbeddingService embeddingService,
        VaultSettingsService vaultSettings,
        ILogger<RagService> logger)
    {
        _vaultNoteIndexer = vaultNoteIndexer;
        _embeddingService = embeddingService;
        _vaultSettings = vaultSettings;
        _logger = logger;
    }

    /// <summary>
    /// 判断查询是否需要检索知识库
    /// </summary>
    public static bool ShouldSearchVault(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var keywords = new[]
        {
            "笔记", "症状", "辨证", "方剂", "中药", "针灸", "经络", "穴位",
            "养生", "内经", "伤寒", "金匮", "温病", "知识库", "笔记", "vault",
            "方", "药", "治", "病", "脉", "舌", "证", "穴"
        };
        var lower = message.ToLowerInvariant();
        return keywords.Any(k => lower.Contains(k));
    }

    /// <summary>
    /// 用知识库搜索结果丰富消息上下文（非流式）
    /// </summary>
    public async Task<List<ChatMessage>> EnrichMessagesWithVaultContextAsync(
        List<ChatMessage> messages, CancellationToken ct = default)
    {
        var lastUserMessage = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        if (!ShouldSearchVault(lastUserMessage))
            return messages;

        var enriched = await DoEnrichAsync(messages, lastUserMessage, ct);
        return enriched ?? messages;
    }

    /// <summary>
    /// 获取流式响应时的知识库检索状态（用于发送 SSE 提示消息）
    /// </summary>
    public async Task<(List<ChatMessage> Messages, bool WasEnriched)> TryEnrichForStreamingAsync(
        List<ChatMessage> messages, CancellationToken ct = default)
    {
        var lastUserMessage = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        if (!ShouldSearchVault(lastUserMessage))
            return (messages, false);

        var enriched = await DoEnrichAsync(messages, lastUserMessage, ct);
        return enriched != null ? (enriched, true) : (messages, false);
    }

    /// <summary>
    /// 执行实际的检索和增强
    /// </summary>
    private async Task<List<ChatMessage>?> DoEnrichAsync(
        List<ChatMessage> messages, string query, CancellationToken ct)
    {
        try
        {
            var activeVault = _vaultSettings.GetActiveVault();
            if (activeVault == null) return null;

            _logger.LogInformation("RAG 检索: vault={VaultId}, query={Query}", activeVault.Id, query);

            var results = await _vaultNoteIndexer.SearchAsync(activeVault.Id, query, ct);
            if (results.Count == 0)
            {
                _logger.LogInformation("RAG 未找到相关笔记");
                return null;
            }

            // 语义重排（如果启用）
            if (_embeddingService.IsSemanticSearchEnabled())
            {
                results = await _embeddingService.RerankBySimilarityAsync(query, results);
            }

            var context = string.Join("\n\n---\n\n", results.Take(5).Select(r =>
                $"【{r.Title}】\n{r.Preview}"));

            var enriched = new List<ChatMessage>(messages);
            var lastUserIndex = enriched.FindLastIndex(m => m.Role == ChatRole.User);
            if (lastUserIndex >= 0)
            {
                enriched[lastUserIndex] = new ChatMessage(ChatRole.User,
                    $"以下是与问题相关的知识库内容，请结合这些内容回答：\n\n{context}\n\n---\n\n用户问题：{query}");
            }

            _logger.LogInformation("RAG 增强完成: 找到 {Count} 条笔记", results.Count);
            return enriched;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RAG 知识库检索增强失败");
            return null;
        }
    }
}
