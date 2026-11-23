using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Baihua.Data.Entities;

/// <summary>
/// 笔记向量缓存（替代 .embedding_cache.json 文件缓存）
/// </summary>
public class NoteEmbedding
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string VaultId { get; set; } = "";

    [Required]
    [MaxLength(500)]
    public string NotePath { get; set; } = "";

    /// <summary>
    /// 向量数据 JSON 数组，如 [0.12, -0.05, ...]
    /// </summary>
    public string VectorJson { get; set; } = "";

    public int Dimensions { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
