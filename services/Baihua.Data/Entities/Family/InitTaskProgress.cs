namespace Baihua.Data.Entities;

/// <summary>
/// 初始化任务进度
/// </summary>
public class InitTaskProgress
{
    public int Id { get; set; }
    public string TaskId { get; set; } = "";
    public string TaskType { get; set; } = "";
    public bool IsCompleted { get; set; }
    public bool IsSkipped { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
