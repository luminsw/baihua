using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Baihua.Data.Entities;

/// <summary>
/// AI 提供商配置（存储在 SQLite 中，ApiKey 加密存储）
/// </summary>
public class AiProviderSetting
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// 提供商唯一标识，如 "siliconflow", "aliyun"
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string ProviderId { get; set; } = "";

    /// <summary>
    /// 显示名称，如 "硅基流动"
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string ProviderName { get; set; } = "";

    /// <summary>
    /// API 基础 URL（OpenAI 兼容协议）
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// Anthropic 协议 Base URL（可选，用于 OpenAI 返回为空时 fallback）
    /// </summary>
    [MaxLength(500)]
    public string? AnthropicBaseUrl { get; set; }

    /// <summary>
    /// 加密的 API Key（存储前通过 Data Protection 加密）
    /// </summary>
    [MaxLength(2000)]
    public string? EncryptedApiKey { get; set; }

    /// <summary>
    /// 是否为主提供商
    /// </summary>
    public bool IsMain { get; set; }

    /// <summary>
    /// 模型列表（JSON 数组）
    /// 格式: [{"name": "model1", "isMain": true}, ...]
    /// </summary>
    public string ModelsJson { get; set; } = "[]";

    /// <summary>
    /// 排序顺序
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 模型等级：0=未分级, 1=固本(Embedding), 2=问道(本地大模型), 3=通玄(云端超大模型)
    /// </summary>
    public int Tier { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}


