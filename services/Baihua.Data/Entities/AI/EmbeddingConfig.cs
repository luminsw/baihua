using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Baihua.Data.Entities;

/// <summary>
/// Embedding 模型配置（一级·固本模型，用于 RAG 向量检索）
/// </summary>
public class EmbeddingConfig
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// 提供商ID，如 "ollama", "openai"
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string ProviderId { get; set; } = "";

    /// <summary>
    /// 模型名称，如 "nomic-embed-text", "bge-small-zh-v1.5"
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string Model { get; set; } = "";

    /// <summary>
    /// API 基础地址
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// 加密的 API Key（本地 Ollama 可为空）
    /// </summary>
    [MaxLength(2000)]
    public string? EncryptedApiKey { get; set; }

    /// <summary>
    /// 向量维度（如 768, 1024, 1536）
    /// </summary>
    public int? Dimensions { get; set; }

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool IsEnabled { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
