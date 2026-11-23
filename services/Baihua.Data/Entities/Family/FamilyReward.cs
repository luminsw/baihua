using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Baihua.Data.Entities;

/// <summary>
/// FAM-31 家庭奖励配置：家长自定义奖励（触发条件 + 奖励名 + 图标），家庭维度。
/// </summary>
public class FamilyReward
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// 触发条件类型：streak_days（连续打卡天数）/ achievement_count（成就数）/ card_count（学习卡片数）
    /// </summary>
    [Required]
    [MaxLength(30)]
    public string ConditionType { get; set; } = "streak_days";

    /// <summary>目标值（如连续 7 天 → TargetValue=7）</summary>
    public int TargetValue { get; set; }

    /// <summary>奖励名称（如"冰淇淋"）</summary>
    [Required]
    [MaxLength(100)]
    public string RewardName { get; set; } = "";

    /// <summary>奖励图标（emoji）</summary>
    [MaxLength(20)]
    public string RewardIcon { get; set; } = "🎁";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// FAM-31 奖励达成记录：谁、什么奖励、达成日期。每条件仅触发一次（去重）。
/// </summary>
public class RewardClaim
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int RewardId { get; set; }

    /// <summary>达成者 LearnerId（0 = 家庭维度）</summary>
    public int LearnerId { get; set; }

    public DateTime ClaimedAt { get; set; } = DateTime.UtcNow;
}
