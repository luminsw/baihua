using Baihua.Contracts.Ai;
using Baihua.Modules.Family.Models;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 记忆上下文：摘要 + 最近完整对话
/// </summary>
public class MemoryContext
{
    /// <summary>
    /// 早期对话的压缩摘要
    /// </summary>
    public string? Summary { get; set; }

    /// <summary>
    /// 最近的完整对话历史（已按 Token 预算截断）
    /// </summary>
    public List<ChatHistoryItem> RecentHistory { get; set; } = new();
}

/// <summary>
/// 语义检索到的记忆条目
/// </summary>
public class RetrievedMemory
{
    public string UserContent { get; set; } = "";
    public string AssistantContent { get; set; } = "";
    public double Similarity { get; set; }
    public int Round { get; set; }
}
