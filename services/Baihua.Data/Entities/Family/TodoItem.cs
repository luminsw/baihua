using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Baihua.Data.Entities;

/// <summary>
/// 个人待办事项：单用户、极简（标题 + 完成状态）。
/// </summary>
public class TodoItem
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>待办标题</summary>
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = "";

    /// <summary>是否已完成</summary>
    public bool IsDone { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>完成时间（未完成时为 null）</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>所属目标 Id（无目标时为 null，即"其他待办"）</summary>
    public int? GoalId { get; set; }

    /// <summary>所属目标</summary>
    public TodoGoal? Goal { get; set; }

    /// <summary>执行指引（AI 生成：去哪个机构、登录哪个网站、准备什么证件、填哪些表单等）</summary>
    [MaxLength(1000)]
    public string? Note { get; set; }
}
