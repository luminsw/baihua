using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Baihua.Data.Entities;

/// <summary>
/// 学习活动记录（用于成就统计和赛舟榜）
/// </summary>
public class StudyActivity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int LearnerId { get; set; }

    [Required]
    [MaxLength(50)]
    public string VaultId { get; set; } = "";

    /// <summary>
    /// study, create_card, generate_cards, chat
    /// </summary>
    [Required]
    [MaxLength(30)]
    public string ActivityType { get; set; } = "study";

    [MaxLength(100)]
    public string? CardId { get; set; }

    /// <summary>
    /// remember, hard, forgot
    /// </summary>
    [MaxLength(20)]
    public string? Result { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
