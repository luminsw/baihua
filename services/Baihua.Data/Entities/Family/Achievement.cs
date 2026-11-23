using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Baihua.Data.Entities;

/// <summary>
/// 成就解锁记录
/// </summary>
public class Achievement
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int LearnerId { get; set; }

    /// <summary>
    /// 成就唯一标识，如 streak_3, cards_10
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Key { get; set; } = "";

    [Required]
    [MaxLength(100)]
    public string Title { get; set; } = "";

    [MaxLength(500)]
    public string Description { get; set; } = "";

    [MaxLength(20)]
    public string Icon { get; set; } = "🏆";

    /// <summary>
    /// bronze, silver, gold, diamond
    /// </summary>
    [MaxLength(20)]
    public string Tier { get; set; } = "bronze";

    /// <summary>
    /// study, creation, exploration
    /// </summary>
    [MaxLength(20)]
    public string Category { get; set; } = "study";

    public DateTime UnlockedAt { get; set; } = DateTime.UtcNow;
}
