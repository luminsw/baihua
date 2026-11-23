using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Baihua.Data.Entities;

/// <summary>
/// 待办目标（一级）：用目标组织一组具体的待办事项。
/// 由用户输入目标、AI 拆解生成，也可手动创建。
/// </summary>
public class TodoGoal
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>目标描述（如：办理机动车驾驶证 / 给孩子办出生医学证明）</summary>
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>该目标下的具体待办</summary>
    public List<TodoItem> Items { get; set; } = new();
}
