using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Baihua.Data.Entities;

/// <summary>
/// 卡片复习状态（艾宾浩斯遗忘曲线调度）
/// </summary>
public class CardReviewState
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int LearnerId { get; set; }

    [Required]
    [MaxLength(50)]
    public string VaultId { get; set; } = "";

    [Required]
    [MaxLength(100)]
    public string CardId { get; set; } = "";

    /// <summary>
    /// 当前间隔天数
    /// </summary>
    public int IntervalDays { get; set; } = 1;

    /// <summary>
    /// 下次推荐复习日期
    /// </summary>
    public DateTime NextReviewDate { get; set; } = DateTime.UtcNow.Date;

    /// <summary>
    /// 连续"记得"次数
    /// </summary>
    public int ConsecutiveRemember { get; set; }

    /// <summary>
    /// 上次结果：remember/hard/forgot
    /// </summary>
    [MaxLength(20)]
    public string? LastResult { get; set; }

    /// <summary>
    /// 总学习次数
    /// </summary>
    public int TotalReviews { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
