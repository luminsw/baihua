using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Baihua.Data.Entities;

/// <summary>
/// 对话记忆条目：存储每轮对话的摘要和向量，用于语义检索记忆
/// </summary>
public class ChatMemoryEntry
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// 会话 ID（前端 localStorage 中的 chat_session_id）
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string SessionId { get; set; } = "";

    /// <summary>
    /// 对话轮次编号
    /// </summary>
    public int Round { get; set; }

    /// <summary>
    /// 用户消息摘要
    /// </summary>
    [Required]
    public string UserSummary { get; set; } = "";

    /// <summary>
    /// AI 回复摘要
    /// </summary>
    [Required]
    public string AssistantSummary { get; set; } = "";

    /// <summary>
    /// 完整用户消息（用于上下文注入）
    /// </summary>
    public string UserContent { get; set; } = "";

    /// <summary>
    /// 完整 AI 回复（用于上下文注入）
    /// </summary>
    public string AssistantContent { get; set; } = "";

    /// <summary>
    /// 向量数据 JSON 数组（用于语义检索）
    /// </summary>
    public string? VectorJson { get; set; }

    public int Dimensions { get; set; }

    /// <summary>
    /// 估算的 token 数
    /// </summary>
    public int EstimatedTokens { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
