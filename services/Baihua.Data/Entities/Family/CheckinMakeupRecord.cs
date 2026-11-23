using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Baihua.Data.Entities;

/// <summary>
/// FAM-33 补签记录：对最近 3 天窗口内、有实际学习记录（StudyActivity）
/// 但未打卡的日期进行补签。每人（家庭维度）每月最多 3 次。
/// </summary>
public class CheckinMakeupRecord
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>补签的日期（北京时间自然日）</summary>
    [Required]
    public DateTime MakeupDate { get; set; }

    /// <summary>知识库 ID（可空 = 全部）</summary>
    [MaxLength(100)]
    public string? VaultId { get; set; }

    /// <summary>补签发生时间（UTC）</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
