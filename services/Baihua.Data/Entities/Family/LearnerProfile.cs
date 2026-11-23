using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Baihua.Data.Entities;

/// <summary>
/// 学习者档案（家庭成员）
/// </summary>
public class LearnerProfile
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = "";

    [MaxLength(10)]
    public string AvatarEmoji { get; set; } = "👤";

    [MaxLength(20)]
    public string Color { get; set; } = "#007bff";

    /// <summary>
    /// 是否为默认学习者
    /// </summary>
    public bool IsDefault { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
